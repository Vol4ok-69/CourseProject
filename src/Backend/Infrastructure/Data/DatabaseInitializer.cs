using Application.Interfaces;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Net.Sockets;

namespace Infrastructure.Data;

/// <summary>
/// Компонент безопасной и идемпотентной инициализации базы данных PostgreSQL:
/// применение миграций EF Core, гарантированное создание системных ролей и синхронизация identity sequence.
/// </summary>
public sealed class DatabaseInitializer : IDatabaseInitializer
{
    private const int DefaultMaxRetryAttempts = 10;
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(3);

    private readonly AppDbContext _context;
    private readonly ILogger<DatabaseInitializer> _logger;
    private readonly int _maxRetryAttempts;
    private readonly TimeSpan _retryDelay;

    public DatabaseInitializer(
        AppDbContext context,
        ILogger<DatabaseInitializer> logger,
        int maxRetryAttempts = DefaultMaxRetryAttempts,
        TimeSpan? retryDelay = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxRetryAttempts = maxRetryAttempts > 0 ? maxRetryAttempts : DefaultMaxRetryAttempts;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Запуск инициализации базы данных...");

        await MigrateWithRetryAsync(cancellationToken);
        await SeedRolesAsync(cancellationToken);

        _logger.LogInformation("Инициализация базы данных успешно завершена.");
    }

    private async Task MigrateWithRetryAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (true)
        {
            attempt++;
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _logger.LogInformation("Применение миграций EF Core (попытка {Attempt} из {MaxAttempts})...", attempt, _maxRetryAttempts);

                await _context.Database.MigrateAsync(cancellationToken);

                _logger.LogInformation("Миграции EF Core успешно применены.");
                return;
            }
            catch (Exception ex) when (IsTransientConnectionException(ex) && attempt < _maxRetryAttempts)
            {
                _logger.LogWarning(
                    ex,
                    "Попытка {Attempt} подключения к PostgreSQL завершилась ошибкой. Ожидание {Delay} секунд перед повтором...",
                    attempt,
                    _retryDelay.TotalSeconds);

                await Task.Delay(_retryDelay, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Критическая ошибка при инициализации базы данных на попытке {Attempt} из {MaxAttempts}.",
                    attempt,
                    _maxRetryAttempts);

                throw new InvalidOperationException(
                    $"Не удалось выполнить миграции базы данных после {attempt} попыток: {ex.Message}",
                    ex);
            }
        }
    }

    private async Task SeedRolesAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Проверка наличия и консистентности системных ролей...");

        var existingRoles = await _context.Roles.ToListAsync(cancellationToken);
        var rolesToAdd = new List<Role>();

        EnsureRoleConsistent(existingRoles, expectedId: 1, expectedName: "Admin", rolesToAdd);
        EnsureRoleConsistent(existingRoles, expectedId: 2, expectedName: "User", rolesToAdd);

        if (rolesToAdd.Count > 0)
        {
            _context.Roles.AddRange(rolesToAdd);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Созданы недостающие системные роли: {Roles}.",
                string.Join(", ", rolesToAdd.Select(r => $"{r.Name} (Id={r.Id})")));
        }
        else
        {
            _logger.LogInformation("Системные роли (Admin, User) уже существуют и соответствуют ожидаемой схеме.");
        }

        if (_context.Database.IsNpgsql())
        {
            await _context.Database.ExecuteSqlRawAsync(
                "SELECT setval(pg_get_serial_sequence('\"Roles\"', 'Id'), GREATEST((SELECT COALESCE(MAX(\"Id\"), 1) FROM \"Roles\"), 1));",
                cancellationToken);

            _logger.LogInformation("Последовательность идентификаторов PostgreSQL ('Roles_Id_seq') синхронизирована.");
        }
    }

    private static void EnsureRoleConsistent(
        List<Role> existingRoles,
        int expectedId,
        string expectedName,
        List<Role> rolesToAdd)
    {
        var roleById = existingRoles.FirstOrDefault(r => r.Id == expectedId);
        var roleByName = existingRoles.FirstOrDefault(r => string.Equals(r.Name, expectedName, StringComparison.OrdinalIgnoreCase));

        if (roleById != null && string.Equals(roleById.Name, expectedName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (roleById != null && !string.Equals(roleById.Name, expectedName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Конфликт системной роли: идентификатор Id={expectedId} уже занят ролью '{roleById.Name}', " +
                $"тогда как ожидалась системная роль '{expectedName}'.");
        }

        if (roleByName != null && roleByName.Id != expectedId)
        {
            throw new InvalidOperationException(
                $"Конфликт системной роли: роль '{expectedName}' уже существует с идентификатором Id={roleByName.Id}, " +
                $"тогда как ожидался обязательный системный идентификатор Id={expectedId}.");
        }

        rolesToAdd.Add(new Role { Id = expectedId, Name = expectedName });
    }

    private static bool IsTransientConnectionException(Exception ex)
    {
        if (ex is NpgsqlException or SocketException or TimeoutException)
        {
            return true;
        }

        if (ex.InnerException != null)
        {
            return IsTransientConnectionException(ex.InnerException);
        }

        return false;
    }
}
