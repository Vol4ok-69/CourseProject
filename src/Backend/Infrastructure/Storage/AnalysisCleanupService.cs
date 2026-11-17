using System.Data;
using Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Infrastructure.Storage;

/// <summary>
/// Реализация безопасной фоновой очистки устаревших рабочих каталогов анализов (TD-003).
/// Координирует доступ к рабочим каталогам через PostgreSQL session-level advisory locks и файловые блокировки,
/// предотвращая удаление каталогов активных исполнений и устраняя гонки с воркерами очереди.
/// </summary>
public sealed class AnalysisCleanupService : IAnalysisCleanupService
{
    private readonly string _connectionString;
    private readonly string _baseStoragePath;
    private readonly IWorkspaceFileLockProvider _workspaceFileLockProvider;
    private readonly ILogger<AnalysisCleanupService>? _logger;

    /// <summary>
    /// Внутренняя точка синхронизации для детерминированного тестирования потери соединения и race conditions.
    /// </summary>
    internal Func<int, Task>? SyncPointBeforeDeleteAsync { get; set; }

    public AnalysisCleanupService(
        string connectionString,
        IWorkspaceFileLockProvider workspaceFileLockProvider,
        string? baseStoragePath = null,
        ILogger<AnalysisCleanupService>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(workspaceFileLockProvider);

        _connectionString = connectionString;
        _workspaceFileLockProvider = workspaceFileLockProvider;
        _logger = logger;

        _baseStoragePath = !string.IsNullOrWhiteSpace(baseStoragePath)
            ? Path.GetFullPath(baseStoragePath)
            : Path.Combine(Path.GetTempPath(), "CourseProject", "analyses");
    }

    public AnalysisCleanupService(
        string connectionString,
        string? baseStoragePath = null,
        ILogger<AnalysisCleanupService>? logger = null)
        : this(connectionString, new WorkspaceFileLockProvider(baseStoragePath), baseStoragePath, logger)
    {
    }

    public AnalysisCleanupService(
        IConfiguration configuration,
        IWorkspaceFileLockProvider workspaceFileLockProvider,
        ILogger<AnalysisCleanupService>? logger = null)
        : this(
            configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Строка подключения 'DefaultConnection' не найдена."),
            workspaceFileLockProvider,
            configuration["Storage:AnalysisPath"],
            logger)
    {
    }

    public AnalysisCleanupService(
        IConfiguration configuration,
        ILogger<AnalysisCleanupService>? logger = null)
        : this(
            configuration,
            new WorkspaceFileLockProvider(configuration["Storage:AnalysisPath"]),
            logger)
    {
    }

    private sealed class WorkspaceLockLostException : Exception
    {
        public WorkspaceLockLostException(int analysisId)
            : base($"Потеряна PostgreSQL-блокировка workspace для анализа {analysisId}.")
        {
        }
    }

    private static async Task EnsurePostgreSqlLockAsync(
        NpgsqlConnection connection,
        int analysisId,
        CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open ||
            !await IsLockHeldByCurrentSessionAsync(
                connection,
                analysisId,
                cancellationToken))
        {
            throw new WorkspaceLockLostException(analysisId);
        }
    }

    /// <inheritdoc />
    public async Task<int> CleanupStaleDirectoriesAsync(
        CancellationToken cancellationToken = default)
    {
        var deletedCount = 0;
        var analysisIds = new List<int>();

        // Получаем кандидатов из БД. До получения блокировок
        // файловая система workspace не затрагивается.
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT "Id"
                FROM "Analyses"
                ORDER BY "Id";
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
                analysisIds.Add(reader.GetInt32(0));
        }

        foreach (var analysisId in analysisIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            NpgsqlConnection? connection = null;
            FileStream? workspaceFileLock = null;

            try
            {
                connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);

                // Сначала PostgreSQL exclusive advisory lock.
                await using (var lockCommand = connection.CreateCommand())
                {
                    lockCommand.CommandText = """
                        SELECT pg_try_advisory_lock(
                            hashtext('netscope_analysis_workspace'),
                            @analysisId);
                        """;

                    lockCommand.Parameters.AddWithValue("analysisId", analysisId);

                    var result = await lockCommand.ExecuteScalarAsync(cancellationToken);

                    if (result is not true)
                        continue;
                }

                // Только после PostgreSQL lock запрашиваем файловую блокировку.
                workspaceFileLock =
                    _workspaceFileLockProvider.TryAcquireExclusive(analysisId);

                if (workspaceFileLock is null)
                    continue;

                await EnsurePostgreSqlLockAsync(
                    connection, analysisId, cancellationToken);

                int status;
                Guid? activeExecutionToken;

                await using (var stateCommand = connection.CreateCommand())
                {
                    stateCommand.CommandText = """
                        SELECT "Status", "ExecutionToken"
                        FROM "Analyses"
                        WHERE "Id" = @analysisId;
                        """;

                    stateCommand.Parameters.AddWithValue("analysisId", analysisId);

                    await using var reader =
                        await stateCommand.ExecuteReaderAsync(cancellationToken);

                    if (!await reader.ReadAsync(cancellationToken))
                        continue;

                    status = reader.GetInt32(0);
                    activeExecutionToken = reader.IsDBNull(1)
                        ? null
                        : reader.GetGuid(1);
                }

                if (SyncPointBeforeDeleteAsync is not null)
                    await SyncPointBeforeDeleteAsync(analysisId);

                await EnsurePostgreSqlLockAsync(
                    connection, analysisId, cancellationToken);

                var fullBasePath = Path.GetFullPath(_baseStoragePath);
                var fullAnalysisDir = Path.GetFullPath(
                    Path.Combine(fullBasePath, analysisId.ToString()));

                if (!fullAnalysisDir.StartsWith(
                        fullBasePath + Path.DirectorySeparatorChar,
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                {
                    continue;
                }

                // Проверка существования каталога выполняется только под обеими блокировками.
                await EnsurePostgreSqlLockAsync(
                    connection, analysisId, cancellationToken);

                if (!Directory.Exists(fullAnalysisDir))
                    continue;

                await EnsurePostgreSqlLockAsync(
                    connection, analysisId, cancellationToken);

                var analysisDirectoryInfo = new DirectoryInfo(fullAnalysisDir);

                if (analysisDirectoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;

                await EnsurePostgreSqlLockAsync(
                    connection, analysisId, cancellationToken);

                var executionDirs = Directory.GetDirectories(fullAnalysisDir);

                foreach (var executionDir in executionDirs)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await EnsurePostgreSqlLockAsync(
                        connection, analysisId, cancellationToken);

                    var fullExecutionDir = Path.GetFullPath(executionDir);

                    if (!fullExecutionDir.StartsWith(
                            fullAnalysisDir + Path.DirectorySeparatorChar,
                            OperatingSystem.IsWindows()
                                ? StringComparison.OrdinalIgnoreCase
                                : StringComparison.Ordinal))
                    {
                        continue;
                    }

                    await EnsurePostgreSqlLockAsync(
                        connection, analysisId, cancellationToken);

                    var executionDirectoryInfo = new DirectoryInfo(fullExecutionDir);

                    if (executionDirectoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;

                    if (!Guid.TryParse(
                            Path.GetFileName(fullExecutionDir),
                            out var folderToken))
                    {
                        continue;
                    }

                    // Никогда не удаляем каталог текущего исполнения.
                    if (activeExecutionToken.HasValue &&
                        folderToken == activeExecutionToken.Value)
                    {
                        continue;
                    }

                    // Running без ExecutionToken трактуется консервативно:
                    // каталоги такого анализа не удаляем.
                    if (status == 1 && !activeExecutionToken.HasValue)
                        continue;

                    await EnsurePostgreSqlLockAsync(
                        connection, analysisId, cancellationToken);

                    try
                    {
                        Directory.Delete(fullExecutionDir, recursive: true);
                        deletedCount++;

                        _logger?.LogInformation(
                            "Удалён устаревший рабочий каталог {ExecutionDirectory} для анализа {AnalysisId}.",
                            fullExecutionDir,
                            analysisId);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(
                            ex,
                            "Не удалось удалить рабочий каталог {ExecutionDirectory}.",
                            fullExecutionDir);
                    }

                    // Каталог обмена контейнера для того же токена.
                    var containerExecutionDir = Path.Combine(
                        Path.GetTempPath(),
                        "netscope-database-analyzer",
                        $"{analysisId}_{folderToken:N}");

                    await EnsurePostgreSqlLockAsync(
                        connection, analysisId, cancellationToken);

                    if (!Directory.Exists(containerExecutionDir))
                        continue;

                    await EnsurePostgreSqlLockAsync(
                        connection, analysisId, cancellationToken);

                    var containerDirectoryInfo =
                        new DirectoryInfo(containerExecutionDir);

                    if (containerDirectoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;

                    await EnsurePostgreSqlLockAsync(
                        connection, analysisId, cancellationToken);

                    try
                    {
                        Directory.Delete(containerExecutionDir, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(
                            ex,
                            "Не удалось удалить каталог обмена контейнера {ContainerDirectory}.",
                            containerExecutionDir);
                    }
                }

                // Для завершённых и аварийно завершённых анализов дополнительно
                // очищаем orphan-каталоги обмена контейнера, даже если соответствующего
                // workspace-каталога уже нет.
                if (status is 2 or 3)
                {
                    var containerBaseDir = Path.Combine(
                        Path.GetTempPath(),
                        "netscope-database-analyzer");

                    await EnsurePostgreSqlLockAsync(
                        connection,
                        analysisId,
                        cancellationToken);

                    if (Directory.Exists(containerBaseDir))
                    {
                        await EnsurePostgreSqlLockAsync(
                            connection,
                            analysisId,
                            cancellationToken);

                        var containerBaseDirectoryInfo = new DirectoryInfo(
                            Path.GetFullPath(containerBaseDir));

                        // Не перечисляем и не удаляем содержимое через символическую ссылку
                        // или другой reparse point в качестве родительского каталога.
                        if (!containerBaseDirectoryInfo.Attributes.HasFlag(
                                FileAttributes.ReparsePoint))
                        {
                            await EnsurePostgreSqlLockAsync(
                                connection,
                                analysisId,
                                cancellationToken);

                            var matchingDirectories = Directory.GetDirectories(
                                containerBaseDir,
                                $"{analysisId}_*");

                            foreach (var matchingDirectory in matchingDirectories)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            await EnsurePostgreSqlLockAsync(
                                connection,
                                analysisId,
                                cancellationToken);

                            var directoryName = Path.GetFileName(matchingDirectory);
                            var separatorIndex = directoryName.IndexOf('_');

                            if (separatorIndex <= 0 ||
                                !int.TryParse(
                                    directoryName.AsSpan(0, separatorIndex),
                                    out var directoryAnalysisId) ||
                                directoryAnalysisId != analysisId ||
                                !Guid.TryParse(
                                    directoryName.AsSpan(separatorIndex + 1),
                                    out var directoryToken))
                            {
                                continue;
                            }

                            if (activeExecutionToken.HasValue &&
                                directoryToken == activeExecutionToken.Value)
                            {
                                continue;
                            }

                            var fullContainerDirectory = Path.GetFullPath(
                                matchingDirectory);

                            var fullContainerBaseDirectory = Path.GetFullPath(
                                containerBaseDir);

                            var containerPrefix = fullContainerBaseDirectory.TrimEnd(
                                Path.DirectorySeparatorChar,
                                Path.AltDirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;

                            if (!fullContainerDirectory.StartsWith(
                                    containerPrefix,
                                    OperatingSystem.IsWindows()
                                        ? StringComparison.OrdinalIgnoreCase
                                        : StringComparison.Ordinal))
                            {
                                continue;
                            }

                            await EnsurePostgreSqlLockAsync(
                                connection,
                                analysisId,
                                cancellationToken);

                            var directoryInfo = new DirectoryInfo(
                                fullContainerDirectory);

                            if (directoryInfo.Attributes.HasFlag(
                                    FileAttributes.ReparsePoint))
                            {
                                continue;
                            }

                            await EnsurePostgreSqlLockAsync(
                                connection,
                                analysisId,
                                cancellationToken);

                            try
                            {
                                Directory.Delete(
                                    fullContainerDirectory,
                                    recursive: true);

                                _logger?.LogInformation(
                                    "Удалён orphan-каталог обмена контейнера {ContainerDirectory} для анализа {AnalysisId}.",
                                    fullContainerDirectory,
                                    analysisId);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning(
                                    ex,
                                    "Не удалось удалить orphan-каталог обмена контейнера {ContainerDirectory}.",
                                    fullContainerDirectory);
                            }
                        }
                    }
                }
                }

                // Удаляем пустой родительский каталог анализа.
                await EnsurePostgreSqlLockAsync(
                    connection, analysisId, cancellationToken);

                if (Directory.Exists(fullAnalysisDir))
                {
                    await EnsurePostgreSqlLockAsync(
                        connection, analysisId, cancellationToken);

                    if (!Directory.EnumerateFileSystemEntries(fullAnalysisDir).Any())
                    {
                        await EnsurePostgreSqlLockAsync(
                            connection, analysisId, cancellationToken);

                        Directory.Delete(fullAnalysisDir, recursive: false);
                    }
                }
            }
            catch (WorkspaceLockLostException ex)
            {
                // После потери соединения или блокировки никаких новых
                // файловых операций для этого анализа не выполняем.
                _logger?.LogWarning(ex, "Очистка анализа {AnalysisId} остановлена.", analysisId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(
                    ex,
                    "Ошибка при обработке очистки для анализа {AnalysisId}.",
                    analysisId);
            }
            finally
            {
                try
                {
                    workspaceFileLock?.Dispose();
                }
                catch
                {
                    // Освобождение файловой блокировки не должно скрывать исходную ошибку.
                }

                if (connection is not null)
                {
                    try
                    {
                        if (connection.State == ConnectionState.Open)
                        {
                            await using var unlockCommand = connection.CreateCommand();

                            unlockCommand.CommandText = """
                                SELECT pg_advisory_unlock(
                                    hashtext('netscope_analysis_workspace'),
                                    @analysisId);
                                """;

                            unlockCommand.Parameters.AddWithValue("analysisId", analysisId);

                            await unlockCommand.ExecuteScalarAsync(CancellationToken.None);
                        }
                    }
                    catch
                    {
                        try
                        {
                            NpgsqlConnection.ClearPool(connection);
                        }
                        catch
                        {
                            // Соединение будет освобождено ниже.
                        }
                    }
                    finally
                    {
                        try
                        {
                            await connection.DisposeAsync();
                        }
                        catch (Exception disposeException)
                        {
                            _logger?.LogWarning(
                                disposeException,
                                "Не удалось корректно освободить PostgreSQL-соединение очистителя для анализа {AnalysisId}.",
                                analysisId);
                        }
                    }
                }
            }
        }

        return deletedCount;
    }

    private static async Task<bool> IsLockHeldByCurrentSessionAsync(
        NpgsqlConnection connection,
        int analysisId,
        CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
        {
            return false;
        }

        try
        {
            await using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = """
                SELECT CASE
                    WHEN EXISTS (
                        SELECT 1 FROM pg_locks
                        WHERE locktype = 'advisory'
                          AND classid = hashtext('netscope_analysis_workspace')
                          AND objid = @analysisId
                          AND objsubid = 2
                          AND pid = pg_backend_pid()
                          AND mode = 'ExclusiveLock'
                          AND granted = true
                    ) THEN 1
                    ELSE 0
                END;
                """;
            checkCmd.Parameters.AddWithValue("analysisId", analysisId);

            var result = await checkCmd.ExecuteScalarAsync(cancellationToken);
            return result is 1 or 1L;
        }
        catch
        {
            return false;
        }
    }
}
