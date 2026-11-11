using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;
using Xunit;

namespace Backend.Tests.Integration;

/// <summary>
/// Фикстура для управления жизненным циклом тестового контейнера PostgreSQL и сброса состояния БД.
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("courseproject_test")
        .WithUsername("test_user")
        .WithPassword("test_password")
        .Build();

    private Respawner? _respawner;

    /// <summary>
    /// Строка подключения к тестовой базе данных PostgreSQL.
    /// </summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <summary>
    /// Запускает контейнер PostgreSQL, применяет миграции EF Core и инициализирует Respawner.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        await using var context = new AppDbContext(options);
        await context.Database.MigrateAsync();

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            TablesToIgnore = ["__EFMigrationsHistory", "Roles"]
        });

        await EnsureDefaultRolesAsync(context);
    }

    /// <summary>
    /// Очищает все таблицы базы данных (кроме истории миграций и системных ролей) между тестами.
    /// </summary>
    public async Task ResetDatabaseAsync()
    {
        if (_respawner != null)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await _respawner.ResetAsync(connection);
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        await using var context = new AppDbContext(options);
        await EnsureDefaultRolesAsync(context);
    }

    private static async Task EnsureDefaultRolesAsync(AppDbContext context)
    {
        if (!await context.Roles.AnyAsync(r => r.Id == 1))
        {
            context.Roles.Add(new Role { Id = 1, Name = "Admin" });
        }

        if (!await context.Roles.AnyAsync(r => r.Id == 2))
        {
            context.Roles.Add(new Role { Id = 2, Name = "User" });
        }

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Останавливает и удаляет тестовый контейнер PostgreSQL.
    /// </summary>
    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
