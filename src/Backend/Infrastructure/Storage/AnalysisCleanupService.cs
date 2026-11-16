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

    /// <inheritdoc />
    public async Task<int> CleanupStaleDirectoriesAsync(CancellationToken cancellationToken = default)
    {
        var deletedCount = 0;

        if (!Directory.Exists(_baseStoragePath))
        {
            return 0;
        }

        var fullBasePath = Path.GetFullPath(_baseStoragePath);

        // 1. Получить перечень AnalysisId, для которых существуют рабочие каталоги.
        string[] analysisDirectories;
        try
        {
            analysisDirectories = Directory.GetDirectories(fullBasePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Ошибка при перечислении каталогов анализов в {BasePath}", fullBasePath);
            return 0;
        }

        foreach (var dir in analysisDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dirName = Path.GetFileName(dir);
            if (!int.TryParse(dirName, out var analysisId) || analysisId <= 0)
            {
                continue;
            }

            var fullAnalysisDir = Path.GetFullPath(dir);
            if (!fullAnalysisDir.StartsWith(fullBasePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Пропускаем символические ссылки на уровне корневого каталога анализа
            var analysisDirInfo = new DirectoryInfo(fullAnalysisDir);
            if (analysisDirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            // 2. Для каждого анализа запросить эксклюзивную PostgreSQL session-level advisory lock
            // в отдельном выделенном физическом соединении.
            NpgsqlConnection? connection = null;
            FileStream? workspaceFileLock = null;
            try
            {
                connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);

                await using var lockCmd = connection.CreateCommand();
                lockCmd.CommandText = "SELECT pg_try_advisory_lock(hashtext('netscope_analysis_workspace'), @analysisId);";
                lockCmd.Parameters.AddWithValue("analysisId", analysisId);

                var lockResult = await lockCmd.ExecuteScalarAsync(cancellationToken);
                var isLocked = lockResult is bool b && b;

                // 3. Если блокировка не получена, пропустить анализ и перейти к следующему.
                if (!isLocked)
                {
                    _logger?.LogDebug("Анализ {AnalysisId} заблокирован активным исполнителем. Пропуск очистки.", analysisId);
                    continue;
                }

                // 4. После получения PostgreSQL ExclusiveLock получить файловую ExclusiveLock
                workspaceFileLock = _workspaceFileLockProvider.TryAcquireExclusive(analysisId);
                if (workspaceFileLock == null)
                {
                    _logger?.LogDebug("Файловая блокировка анализа {AnalysisId} занята. Пропуск очистки.", analysisId);
                    continue;
                }

                // 5. Только после получения обеих блокировок разрешено читать актуальные данные анализа
                if (connection.State != ConnectionState.Open || !await IsLockHeldByCurrentSessionAsync(connection, analysisId, cancellationToken))
                {
                    continue;
                }

                await using var queryCmd = connection.CreateCommand();
                queryCmd.CommandText = """
                    SELECT "Status", "ExecutionToken", "LeaseExpiration"
                    FROM "Analyses"
                    WHERE "Id" = @analysisId;
                    """;
                queryCmd.Parameters.AddWithValue("analysisId", analysisId);

                int? status = null;
                Guid? activeExecutionToken = null;
                var recordFound = false;

                await using (var reader = await queryCmd.ExecuteReaderAsync(cancellationToken))
                {
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        recordFound = true;
                        status = reader.GetInt32(0);
                        activeExecutionToken = reader.IsDBNull(1) ? null : reader.GetGuid(1);
                    }
                }

                // Нельзя удалять каталог, если состояние анализа не удалось прочитать или оно неопределённо.
                if (!recordFound || status == null)
                {
                    _logger?.LogWarning("Не удалось прочитать состояние анализа {AnalysisId} из PostgreSQL. Очистка отменена.", analysisId);
                    continue;
                }

                // Вызов тестовой точки синхронизации (если задана) после подтверждения владения и чтения состояния
                if (SyncPointBeforeDeleteAsync != null)
                {
                    await SyncPointBeforeDeleteAsync(analysisId);
                }

                // Повторно проверяем владение PostgreSQL lock до любых файловых операций
                if (connection.State != ConnectionState.Open || !await IsLockHeldByCurrentSessionAsync(connection, analysisId, cancellationToken))
                {
                    _logger?.LogWarning("Потеряна блокировка PostgreSQL для анализа {AnalysisId} после SyncPoint.", analysisId);
                    continue;
                }

                // 6. Перечислить каталоги исполнений данного AnalysisId.
                string[] executionDirs;
                try
                {
                    executionDirs = Directory.GetDirectories(fullAnalysisDir);
                }
                catch
                {
                    continue;
                }

                var lockLost = false;
                foreach (var execDir in executionDirs)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var fullExecDir = Path.GetFullPath(execDir);
                    if (!fullExecDir.StartsWith(fullAnalysisDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var execDirInfo = new DirectoryInfo(fullExecDir);
                    if (execDirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    var execTokenName = Path.GetFileName(fullExecDir);
                    if (!Guid.TryParse(execTokenName, out var folderToken))
                    {
                        continue;
                    }

                    // 7. Правила удаления:
                    // Никогда не удалять каталог текущего исполнения!
                    if (activeExecutionToken.HasValue && folderToken == activeExecutionToken.Value)
                    {
                        _logger?.LogDebug("Каталог {FolderToken} анализа {AnalysisId} принадлежит активному токену. Сохранение.", folderToken, analysisId);
                        continue;
                    }

                    // Если активного токена нет, а статус Running — неопределённое состояние, не удаляем.
                    if (!activeExecutionToken.HasValue && status == 1)
                    {
                        continue;
                    }

                    // Перед удалением каждого каталога повторно проверить,
                    // что соединение активно и блокировка всё ещё принадлежит текущей сессии!
                    if (connection.State != ConnectionState.Open || !await IsLockHeldByCurrentSessionAsync(connection, analysisId, cancellationToken))
                    {
                        _logger?.LogWarning("Потеряна блокировка или разорвано соединение для анализа {AnalysisId}. Прекращение удаления.", analysisId);
                        lockLost = true;
                        break;
                    }

                    try
                    {
                        Directory.Delete(fullExecDir, recursive: true);
                        deletedCount++;
                        _logger?.LogInformation("Удалён устаревший рабочий каталог {FullExecDir} для анализа {AnalysisId}.", fullExecDir, analysisId);
                    }
                    catch (Exception deleteEx)
                    {
                        _logger?.LogWarning(deleteEx, "Не удалось удалить рабочий каталог {FullExecDir}.", fullExecDir);
                    }

                    // Также удаляем устаревший каталог обмена контейнера для данного токена
                    var containerExecDir = Path.Combine(
                        Path.GetTempPath(),
                        "netscope-database-analyzer",
                        $"{analysisId}_{folderToken:N}");
                    if (Directory.Exists(containerExecDir))
                    {
                        if (connection.State != ConnectionState.Open || !await IsLockHeldByCurrentSessionAsync(connection, analysisId, cancellationToken))
                        {
                            lockLost = true;
                            break;
                        }

                        try
                        {
                            var cDirInfo = new DirectoryInfo(containerExecDir);
                            if (!cDirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            {
                                Directory.Delete(containerExecDir, recursive: true);
                                _logger?.LogInformation("Удалён устаревший каталог обмена контейнера {ContainerExecDir} для анализа {AnalysisId}.", containerExecDir, analysisId);
                            }
                        }
                        catch (Exception cEx)
                        {
                            _logger?.LogWarning(cEx, "Не удалось удалить каталог обмена контейнера {ContainerExecDir}.", containerExecDir);
                        }
                    }
                }

                if (lockLost)
                {
                    continue;
                }

                // Очистка оставшихся каталогов обмена контейнера для завершённого/сбойного анализа
                var containerBaseDir = Path.Combine(Path.GetTempPath(), "netscope-database-analyzer");
                if (Directory.Exists(containerBaseDir) && (status == 2 || status == 3))
                {
                    if (connection.State != ConnectionState.Open || !await IsLockHeldByCurrentSessionAsync(connection, analysisId, cancellationToken))
                    {
                        continue;
                    }

                    try
                    {
                        var matchingDirs = Directory.GetDirectories(containerBaseDir, $"{analysisId}_*");
                        foreach (var mDir in matchingDirs)
                        {
                            var containerDirName = Path.GetFileName(mDir);
                            var underscoreIdx = containerDirName.IndexOf('_');
                            if (underscoreIdx > 0 && Guid.TryParse(containerDirName.AsSpan(underscoreIdx + 1), out var dirToken))
                            {
                                if (activeExecutionToken.HasValue && dirToken == activeExecutionToken.Value)
                                {
                                    continue;
                                }
                            }

                            if (connection.State != ConnectionState.Open || !await IsLockHeldByCurrentSessionAsync(connection, analysisId, cancellationToken))
                            {
                                break;
                            }

                            var dirInfo = new DirectoryInfo(mDir);
                            if (!dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            {
                                Directory.Delete(mDir, recursive: true);
                            }
                        }
                    }
                    catch
                    {
                    }
                }

                // Удаляем пустой родительский каталог анализа ТОЛЬКО если блокировка всё ещё удерживается текущей сессией
                if (connection.State == ConnectionState.Open && await IsLockHeldByCurrentSessionAsync(connection, analysisId, cancellationToken))
                {
                    try
                    {
                        if (Directory.Exists(fullAnalysisDir) && !Directory.EnumerateFileSystemEntries(fullAnalysisDir).Any())
                        {
                            Directory.Delete(fullAnalysisDir, recursive: false);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Ошибка при обработке очистки для анализа {AnalysisId}.", analysisId);
            }
            finally
            {
                // Порядок освобождения ресурсов:
                // 1. workspaceFileLock.Dispose();
                // 2. PostgreSQL pg_advisory_unlock;
                // 3. connection.DisposeAsync().
                try
                {
                    workspaceFileLock?.Dispose();
                }
                catch
                {
                }

                if (connection != null)
                {
                    try
                    {
                        if (connection.State == ConnectionState.Open)
                        {
                            await using var unlockCmd = connection.CreateCommand();
                            unlockCmd.CommandText = "SELECT pg_advisory_unlock(hashtext('netscope_analysis_workspace'), @analysisId);";
                            unlockCmd.Parameters.AddWithValue("analysisId", analysisId);
                            await unlockCmd.ExecuteScalarAsync(CancellationToken.None);
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
                        }
                    }
                    finally
                    {
                        try
                        {
                            await connection.DisposeAsync();
                        }
                        catch
                        {
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
