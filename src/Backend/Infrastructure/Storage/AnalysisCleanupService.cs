using System.Data;
using System.Globalization;
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
    private const string LocksDirectoryName = ".locks";
    private const string ContainerBaseDirectoryName = "netscope-database-analyzer";

    private readonly string _connectionString;
    private readonly string _baseStoragePath;
    private readonly string _containerBasePath;
    private readonly IWorkspaceFileLockProvider _workspaceFileLockProvider;
    private readonly ILogger<AnalysisCleanupService>? _logger;

    /// <summary>
    /// Внутренняя точка синхронизации для детерминированного тестирования потери соединения и race conditions.
    /// </summary>
    internal Func<int, Task>? SyncPointBeforeDeleteAsync { get; set; }

    /// <summary>
    /// Внутренняя точка синхронизации после удаления отдельного каталога для тестирования потери соединения в середине цикла очистки.
    /// </summary>
    internal Func<int, string, Task>? SyncPointAfterDirectoryDeleteAsync { get; set; }

    public AnalysisCleanupService(
        string connectionString,
        IWorkspaceFileLockProvider workspaceFileLockProvider,
        string? baseStoragePath = null,
        ILogger<AnalysisCleanupService>? logger = null)
        : this(
            connectionString,
            workspaceFileLockProvider,
            baseStoragePath,
            containerBasePath: null,
            logger)
    {
    }

    internal AnalysisCleanupService(
        string connectionString,
        IWorkspaceFileLockProvider workspaceFileLockProvider,
        string? baseStoragePath,
        string? containerBasePath,
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

        _containerBasePath = !string.IsNullOrWhiteSpace(containerBasePath)
            ? Path.GetFullPath(containerBasePath)
            : Path.Combine(Path.GetTempPath(), ContainerBaseDirectoryName);
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
        var candidateIds = DiscoverCandidateAnalysisIds(cancellationToken);
        if (candidateIds.Count == 0)
        {
            return 0;
        }

        var deletedCount = 0;

        foreach (var analysisId in candidateIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            NpgsqlConnection? connection = null;
            FileStream? workspaceFileLock = null;
            var lockAttemptStarted = false;
            var lockAttemptCompleted = false;
            var postgresLockAcquired = false;
            var deletedTokensForAnalysis = new HashSet<Guid>();

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

                    lockAttemptStarted = true;
                    var result = await lockCommand.ExecuteScalarAsync(cancellationToken);
                    lockAttemptCompleted = true;

                    postgresLockAcquired = result is true;
                    if (!postgresLockAcquired)
                    {
                        _logger?.LogDebug(
                            "Очистка анализа {AnalysisId} пропущена: не удалось получить PostgreSQL exclusive advisory lock.",
                            analysisId);
                        continue;
                    }
                }

                // Только после PostgreSQL lock запрашиваем эксклюзивную файловую блокировку.
                workspaceFileLock = _workspaceFileLockProvider.TryAcquireExclusive(analysisId);

                if (workspaceFileLock is null)
                {
                    _logger?.LogDebug(
                        "Очистка анализа {AnalysisId} пропущена: не удалось получить файловую exclusive-блокировку.",
                        analysisId);
                    continue;
                }

                await EnsurePostgreSqlLockAsync(
                    connection, analysisId, cancellationToken);

                // На том же физическом соединении повторно проверяем состояние записи Analysis в БД.
                var (analysisExists, status, activeExecutionToken) =
                    await ReadAnalysisStateAsync(connection, analysisId, cancellationToken);

                if (SyncPointBeforeDeleteAsync is not null)
                    await SyncPointBeforeDeleteAsync(analysisId);

                await EnsurePostgreSqlLockAsync(
                    connection, analysisId, cancellationToken);

                await CleanupWorkspaceStorageAsync(
                    connection,
                    analysisId,
                    analysisExists,
                    status,
                    activeExecutionToken,
                    deletedTokensForAnalysis,
                    cancellationToken);

                await CleanupContainerExchangeStorageAsync(
                    connection,
                    analysisId,
                    analysisExists,
                    status,
                    activeExecutionToken,
                    deletedTokensForAnalysis,
                    cancellationToken);
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
            catch (NpgsqlException ex) when (!postgresLockAcquired)
            {
                // Состояние D (fail-closed): при недоступности PostgreSQL прекращаем цикл очистки без удаления каталогов.
                _logger?.LogError(
                    ex,
                    "Очистка остановлена (fail-closed): PostgreSQL недоступен при обработке анализа {AnalysisId}.",
                    analysisId);
                break;
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
                deletedCount += deletedTokensForAnalysis.Count;

                try
                {
                    workspaceFileLock?.Dispose();
                }
                catch
                {
                    // Освобождение файловой блокировки не должно скрывать исходную ошибку.
                }

                await ReleasePostgreSqlConnectionAsync(
                    connection,
                    analysisId,
                    lockAttemptStarted,
                    lockAttemptCompleted,
                    postgresLockAcquired);
            }
        }

        return deletedCount;
    }

    private SortedSet<int> DiscoverCandidateAnalysisIds(CancellationToken cancellationToken)
    {
        var candidateIds = new SortedSet<int>();

        CollectWorkspaceCandidateIds(candidateIds, cancellationToken);
        CollectContainerCandidateIds(candidateIds, cancellationToken);

        return candidateIds;
    }

    private void CollectWorkspaceCandidateIds(
        SortedSet<int> candidateIds,
        CancellationToken cancellationToken)
    {
        var fullBasePath = Path.GetFullPath(_baseStoragePath);
        if (!Directory.Exists(fullBasePath))
        {
            return;
        }

        var baseDirectoryInfo = new DirectoryInfo(fullBasePath);
        if (IsReparsePoint(baseDirectoryInfo))
        {
            _logger?.LogWarning(
                "Обнаружение кандидатов пропущено: корневой каталог хранилища {BaseStoragePath} является символической ссылкой или reparse point.",
                fullBasePath);
            return;
        }

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(fullBasePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(
                ex,
                "Не удалось перечислить каталоги кандидатов в {BaseStoragePath}.",
                fullBasePath);
            return;
        }

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fullDirectory = Path.GetFullPath(directory);
            if (!IsStrictlySubdirectory(fullBasePath, fullDirectory))
            {
                continue;
            }

            var directoryName = GetDirectoryName(fullDirectory);
            if (string.Equals(directoryName, LocksDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryParsePositiveIntExact(directoryName, out var analysisId))
            {
                continue;
            }

            if (IsReparsePoint(new DirectoryInfo(fullDirectory)))
            {
                _logger?.LogWarning(
                    "Каталог анализа {Directory} пропущен при обнаружении: является символической ссылкой или reparse point.",
                    fullDirectory);
                continue;
            }

            candidateIds.Add(analysisId);
        }
    }

    private void CollectContainerCandidateIds(
        SortedSet<int> candidateIds,
        CancellationToken cancellationToken)
    {
        var fullContainerBasePath = Path.GetFullPath(_containerBasePath);
        if (!Directory.Exists(fullContainerBasePath))
        {
            return;
        }

        var containerBaseInfo = new DirectoryInfo(fullContainerBasePath);
        if (IsReparsePoint(containerBaseInfo))
        {
            _logger?.LogWarning(
                "Обнаружение контейнерных каталогов пропущено: корневой каталог {ContainerBasePath} является символической ссылкой или reparse point.",
                fullContainerBasePath);
            return;
        }

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(fullContainerBasePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(
                ex,
                "Не удалось перечислить контейнерные каталоги в {ContainerBasePath}.",
                fullContainerBasePath);
            return;
        }

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fullDirectory = Path.GetFullPath(directory);
            if (!IsStrictlySubdirectory(fullContainerBasePath, fullDirectory))
            {
                continue;
            }

            var directoryName = GetDirectoryName(fullDirectory);
            if (!TryParseContainerDirectoryName(directoryName, out var analysisId, out _))
            {
                continue;
            }

            if (IsReparsePoint(new DirectoryInfo(fullDirectory)))
            {
                _logger?.LogWarning(
                    "Контейнерный каталог {Directory} пропущен при обнаружении: является символической ссылкой или reparse point.",
                    fullDirectory);
                continue;
            }

            candidateIds.Add(analysisId);
        }
    }

    private static async Task<(bool Exists, int Status, Guid? ActiveExecutionToken)> ReadAnalysisStateAsync(
        NpgsqlConnection connection,
        int analysisId,
        CancellationToken cancellationToken)
    {
        await using var stateCommand = connection.CreateCommand();
        stateCommand.CommandText = """
            SELECT "Status", "ExecutionToken"
            FROM "Analyses"
            WHERE "Id" = @analysisId;
            """;

        stateCommand.Parameters.AddWithValue("analysisId", analysisId);

        await using var reader = await stateCommand.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return (false, 0, null);
        }

        var status = reader.GetInt32(0);
        var activeExecutionToken = reader.IsDBNull(1)
            ? (Guid?)null
            : reader.GetGuid(1);

        return (true, status, activeExecutionToken);
    }

    private async Task CleanupWorkspaceStorageAsync(
        NpgsqlConnection connection,
        int analysisId,
        bool analysisExists,
        int status,
        Guid? activeExecutionToken,
        HashSet<Guid> deletedTokensForAnalysis,
        CancellationToken cancellationToken)
    {
        var fullBasePath = Path.GetFullPath(_baseStoragePath);
        var fullAnalysisDir = Path.GetFullPath(
            Path.Combine(fullBasePath, analysisId.ToString(CultureInfo.InvariantCulture)));

        if (!IsStrictlySubdirectory(fullBasePath, fullAnalysisDir))
        {
            _logger?.LogWarning(
                "Путь рабочего каталога анализа {AnalysisDir} выходит за границы корня {BaseStoragePath}.",
                fullAnalysisDir,
                fullBasePath);
            return;
        }

        // Проверка существования каталога выполняется только под обеими блокировками.
        await EnsurePostgreSqlLockAsync(connection, analysisId, cancellationToken);

        if (!Directory.Exists(fullBasePath))
        {
            return;
        }

        if (IsReparsePoint(new DirectoryInfo(fullBasePath)))
        {
            _logger?.LogWarning(
                "Очистка рабочего каталога анализа {AnalysisId} пропущена: корневой каталог {BaseStoragePath} является символической ссылкой или reparse point.",
                analysisId,
                fullBasePath);
            return;
        }

        if (!Directory.Exists(fullAnalysisDir))
        {
            return;
        }

        await EnsurePostgreSqlLockAsync(connection, analysisId, cancellationToken);

        var analysisDirectoryInfo = new DirectoryInfo(fullAnalysisDir);
        if (IsReparsePoint(analysisDirectoryInfo))
        {
            _logger?.LogWarning(
                "Рабочий каталог анализа {AnalysisDir} пропущен: является символической ссылкой или reparse point.",
                fullAnalysisDir);
            return;
        }

        await EnsurePostgreSqlLockAsync(connection, analysisId, cancellationToken);

        var executionDirs = Directory.GetDirectories(fullAnalysisDir);

        foreach (var executionDir in executionDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await EnsurePostgreSqlLockAsync(connection, analysisId, cancellationToken);

            var fullExecutionDir = Path.GetFullPath(executionDir);

            if (!IsStrictlySubdirectory(fullAnalysisDir, fullExecutionDir))
            {
                continue;
            }

            var folderName = GetDirectoryName(fullExecutionDir);
            if (!TryParseWorkspaceExecutionToken(folderName, out var folderToken))
            {
                continue;
            }

            if (!CanDeleteExecutionDirectory(analysisExists, status, activeExecutionToken, folderToken))
            {
                continue;
            }

            await EnsurePostgreSqlLockAsync(connection, analysisId, cancellationToken);

            var executionDirectoryInfo = new DirectoryInfo(fullExecutionDir);
            if (IsReparsePoint(executionDirectoryInfo))
            {
                _logger?.LogWarning(
                    "Каталог исполнения {ExecutionDirectory} пропущен: является символической ссылкой или reparse point.",
                    fullExecutionDir);
                continue;
            }

            var deletedCurrentDirectory = false;
            try
            {
                Directory.Delete(fullExecutionDir, recursive: true);
                deletedCurrentDirectory = true;
                deletedTokensForAnalysis.Add(folderToken);

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

            if (deletedCurrentDirectory && SyncPointAfterDirectoryDeleteAsync is not null)
            {
                await SyncPointAfterDirectoryDeleteAsync(analysisId, fullExecutionDir);
            }
        }

        // Удаляем пустой родительский каталог анализа.
        await EnsurePostgreSqlLockAsync(connection, analysisId, cancellationToken);

        if (Directory.Exists(fullAnalysisDir))
        {
            await EnsurePostgreSqlLockAsync(connection, analysisId, cancellationToken);

            if (!IsReparsePoint(new DirectoryInfo(fullAnalysisDir)) &&
                !Directory.EnumerateFileSystemEntries(fullAnalysisDir).Any())
            {
                await EnsurePostgreSqlLockAsync(connection, analysisId, cancellationToken);

                if (!IsReparsePoint(new DirectoryInfo(fullAnalysisDir)))
                {
                    Directory.Delete(fullAnalysisDir, recursive: false);
                }
            }
        }
    }

    private async Task CleanupContainerExchangeStorageAsync(
        NpgsqlConnection connection,
        int analysisId,
        bool analysisExists,
        int status,
        Guid? activeExecutionToken,
        HashSet<Guid> deletedTokensForAnalysis,
        CancellationToken cancellationToken)
    {
        var fullContainerBaseDirectory = Path.GetFullPath(_containerBasePath);

        await EnsurePostgreSqlLockAsync(connection, analysisId, cancellationToken);

        if (!Directory.Exists(fullContainerBaseDirectory))
        {
            return;
        }

        await EnsurePostgreSqlLockAsync(connection, analysisId, cancellationToken);

        var containerBaseDirectoryInfo = new DirectoryInfo(fullContainerBaseDirectory);

        // Не перечисляем и не удаляем содержимое через символическую ссылку
        // или другой reparse point в качестве родительского каталога.
        if (IsReparsePoint(containerBaseDirectoryInfo))
        {
            _logger?.LogWarning(
                "Каталог обмена контейнеров {ContainerBaseDirectory} пропущен: является символической ссылкой или reparse point.",
                fullContainerBaseDirectory);
            return;
        }

        await EnsurePostgreSqlLockAsync(connection, analysisId, cancellationToken);

        var matchingDirectories = Directory.GetDirectories(
            fullContainerBaseDirectory,
            $"{analysisId.ToString(CultureInfo.InvariantCulture)}_*");

        foreach (var matchingDirectory in matchingDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await EnsurePostgreSqlLockAsync(connection, analysisId, cancellationToken);

            var fullContainerDirectory = Path.GetFullPath(matchingDirectory);

            if (!IsStrictlySubdirectory(fullContainerBaseDirectory, fullContainerDirectory))
            {
                continue;
            }

            var directoryName = GetDirectoryName(fullContainerDirectory);
            if (!TryParseContainerDirectoryName(directoryName, out var directoryAnalysisId, out var directoryToken) ||
                directoryAnalysisId != analysisId)
            {
                continue;
            }

            if (!CanDeleteExecutionDirectory(analysisExists, status, activeExecutionToken, directoryToken))
            {
                continue;
            }

            await EnsurePostgreSqlLockAsync(connection, analysisId, cancellationToken);

            var directoryInfo = new DirectoryInfo(fullContainerDirectory);
            if (IsReparsePoint(directoryInfo))
            {
                _logger?.LogWarning(
                    "Каталог обмена контейнера {ContainerDirectory} пропущен: является символической ссылкой или reparse point.",
                    fullContainerDirectory);
                continue;
            }

            var deletedContainerDirectory = false;
            try
            {
                Directory.Delete(fullContainerDirectory, recursive: true);
                deletedContainerDirectory = true;
                deletedTokensForAnalysis.Add(directoryToken);

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

            if (deletedContainerDirectory && SyncPointAfterDirectoryDeleteAsync is not null)
            {
                await SyncPointAfterDirectoryDeleteAsync(analysisId, fullContainerDirectory);
            }
        }
    }

    private static bool CanDeleteExecutionDirectory(
        bool analysisExists,
        int status,
        Guid? activeExecutionToken,
        Guid folderToken)
    {
        // Состояние A: запись Analysis отсутствует в БД (подтверждено под обеими блокировками).
        if (!analysisExists)
        {
            return true;
        }

        // Состояние C: терминальный статус (Completed = 2, Failed = 3).
        // Обе exclusive-блокировки гарантируют, что ни один worker не удерживает shared-блокировки и не читает/очищает каталог.
        if (status is 2 or 3)
        {
            return true;
        }

        // Состояние B: активная или ожидающая выполнения задача (Pending = 0, Running = 1).
        if (status is 0 or 1)
        {
            // Никогда не удаляем каталог текущего исполнения.
            if (activeExecutionToken.HasValue && folderToken == activeExecutionToken.Value)
            {
                return false;
            }

            // Running без ExecutionToken трактуется консервативно:
            // каталоги такого анализа не удаляем.
            if (status == 1 && !activeExecutionToken.HasValue)
            {
                return false;
            }

            return true;
        }

        return false;
    }

    private static bool TryParsePositiveIntExact(ReadOnlySpan<char> value, out int analysisId)
    {
        analysisId = 0;

        if (value.IsEmpty || value[0] == '0')
        {
            return false;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out analysisId) &&
               analysisId > 0;
    }

    private static bool TryParseWorkspaceExecutionToken(string folderName, out Guid token)
    {
        token = Guid.Empty;
        return Guid.TryParseExact(folderName, "D", out token) &&
               token != Guid.Empty &&
               string.Equals(folderName, token.ToString("D"), StringComparison.Ordinal);
    }

    private static bool TryParseContainerDirectoryName(
        string directoryName,
        out int analysisId,
        out Guid token)
    {
        analysisId = 0;
        token = Guid.Empty;

        var separatorIndex = directoryName.IndexOf('_');
        if (separatorIndex <= 0)
        {
            return false;
        }

        if (!TryParsePositiveIntExact(directoryName.AsSpan(0, separatorIndex), out analysisId))
        {
            return false;
        }

        var tokenSpan = directoryName.AsSpan(separatorIndex + 1);
        return Guid.TryParseExact(tokenSpan, "N", out token) &&
               token != Guid.Empty &&
               tokenSpan.Equals(token.ToString("N"), StringComparison.Ordinal);
    }

    private static bool IsReparsePoint(DirectoryInfo directoryInfo)
    {
        return directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static string GetDirectoryName(string fullDirectoryPath)
    {
        var trimmed = fullDirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed);
    }

    private static bool IsStrictlySubdirectory(string rootDirectory, string candidateDirectory)
    {
        var fullRoot = Path.GetFullPath(rootDirectory);
        var fullCandidate = Path.GetFullPath(candidateDirectory);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var trimmedRoot = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var trimmedCandidate = fullCandidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(trimmedCandidate, trimmedRoot, comparison))
        {
            return false;
        }

        var rootPrefix = trimmedRoot + Path.DirectorySeparatorChar;
        if (!fullCandidate.StartsWith(rootPrefix, comparison))
        {
            return false;
        }

        var relative = Path.GetRelativePath(fullRoot, fullCandidate);
        if (string.IsNullOrEmpty(relative) ||
            relative == "." ||
            relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            return false;
        }

        return true;
    }

    private async Task ReleasePostgreSqlConnectionAsync(
        NpgsqlConnection? connection,
        int analysisId,
        bool lockAttemptStarted,
        bool lockAttemptCompleted,
        bool postgresLockAcquired)
    {
        if (connection is null)
        {
            return;
        }

        var mustClearPool = lockAttemptStarted && !lockAttemptCompleted;

        try
        {
            if (postgresLockAcquired)
            {
                if (connection.State == ConnectionState.Open)
                {
                    try
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
                    catch
                    {
                        mustClearPool = true;
                    }
                }
                else
                {
                    mustClearPool = true;
                }
            }

            if (mustClearPool)
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
