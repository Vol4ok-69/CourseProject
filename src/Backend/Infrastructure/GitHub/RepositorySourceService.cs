using Application.Exceptions;
using Application.Interfaces;
using Infrastructure.GitHub.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;

namespace Infrastructure.GitHub;

public class RepositorySourceService : IRepositorySourceService
{
    public const long MaxArchiveSizeBytes = 50 * 1024 * 1024; // 50 МиБ (52 428 800 байт)
    public const long MaxSingleFileSizeBytes = 50 * 1024 * 1024; // 50 МиБ (52 428 800 байт)
    public const long MaxTotalExtractedSizeBytes = 250 * 1024 * 1024; // 250 МиБ (262 144 000 байт)
    public const int MaxFileCount = 10_000;

    private readonly HttpClient _httpClient;
    private readonly string _baseStoragePath;
    private readonly IAnalysisLeaseGuardProvider? _leaseGuardProvider;
    private readonly ILogger<RepositorySourceService>? _logger;

    [ActivatorUtilitiesConstructor]
    public RepositorySourceService(
        HttpClient httpClient,
        IConfiguration? configuration = null,
        IAnalysisLeaseGuardProvider? leaseGuardProvider = null,
        ILogger<RepositorySourceService>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _leaseGuardProvider = leaseGuardProvider;
        _logger = logger;

        var configuredPath = configuration?["Storage:AnalysisPath"];
        _baseStoragePath = !string.IsNullOrWhiteSpace(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.Combine(Path.GetTempPath(), "CourseProject", "analyses");
    }

    public RepositorySourceService(HttpClient httpClient)
        : this(httpClient, null, null, null)
    {
    }

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        ".idea",
        "bin",
        "obj",
        "TestResults",
        "node_modules",
        ".gradle",
        "build"
    };

    private string GetExecutionDirectory(int analysisId, Guid executionToken)
    {
        return Path.Combine(_baseStoragePath, analysisId.ToString(), executionToken.ToString("D"));
    }

    private static async Task EnsureWorkspaceOwnershipAsync(
        IAnalysisLeaseGuard guard,
        int analysisId,
        Guid executionToken,
        CancellationToken cancellationToken)
    {
        if (!guard.IsAcquired ||
            guard.AnalysisId != analysisId ||
            guard.ExecutionToken != executionToken)
        {
            throw new InvalidOperationException(
                "Выполнение операции отклонено: владение рабочим пространством не подтверждено.");
        }

        var isOwner = await guard.ValidateOwnershipAsync(
            [(int)Domain.Enums.AnalysisStatus.Running],
            cancellationToken);

        if (!isOwner)
        {
            throw new InvalidOperationException(
                "Выполнение операции отклонено: владение рабочим пространством не подтверждено.");
        }
    }

    private static async Task EnsureWorkspaceCleanupOwnershipAsync(
        IAnalysisLeaseGuard guard,
        int analysisId,
        Guid executionToken,
        CancellationToken cancellationToken)
    {
        if (!guard.IsAcquired ||
            guard.AnalysisId != analysisId ||
            guard.ExecutionToken != executionToken)
        {
            throw new InvalidOperationException(
                "Удаление рабочего пространства отклонено: владение не подтверждено.");
        }

        var isOwner = await guard.ValidateOwnershipAsync(
            [
                (int)Domain.Enums.AnalysisStatus.Running,
                (int)Domain.Enums.AnalysisStatus.Completed,
                (int)Domain.Enums.AnalysisStatus.Failed
            ],
            cancellationToken);

        if (!isOwner)
        {
            throw new InvalidOperationException(
                "Удаление рабочего пространства отклонено: владение не подтверждено.");
        }
    }

    public Task<string> DownloadAndExtractAsync(
        string repositoryUrl,
        string commitHash,
        int analysisId,
        Guid executionToken,
        string personalAccessToken,
        CancellationToken cancellationToken = default)
    {
        return DownloadAndExtractAsync(
            repositoryUrl,
            commitHash,
            analysisId,
            executionToken,
            personalAccessToken,
            guard: null!,
            cancellationToken);
    }

    public async Task<string> DownloadAndExtractAsync(
        string repositoryUrl,
        string commitHash,
        int analysisId,
        Guid executionToken,
        string personalAccessToken,
        IAnalysisLeaseGuard guard,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
            throw new ArgumentException("Не указан URL репозитория.", nameof(repositoryUrl));

        if (string.IsNullOrWhiteSpace(commitHash))
            throw new ArgumentException("Не указан хэш коммита.", nameof(commitHash));

        if (string.IsNullOrWhiteSpace(personalAccessToken))
            throw new ArgumentException("Не указан персональный токен доступа GitHub (PAT).", nameof(personalAccessToken));

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(analysisId);

        if (executionToken == Guid.Empty)
            throw new ArgumentException("Не указан токен выполнения анализа.", nameof(executionToken));

        if (guard is null)
        {
            throw new InvalidOperationException(
                "Файловая операция рабочего пространства отклонена: отсутствует действующий IAnalysisLeaseGuard.");
        }

        await EnsureWorkspaceOwnershipAsync(
            guard,
            analysisId,
            executionToken,
            cancellationToken);

        var repository = GitHubRepositoryReference.Parse(repositoryUrl);

        var executionDirectory = GetExecutionDirectory(analysisId, executionToken);
        var repositoryPath = Path.Combine(executionDirectory, "repository");
        var tempZipPath = Path.Combine(executionDirectory, "archive.zip");

        // Проверяем владение непосредственно перед первым обращением к workspace.
        await EnsureWorkspaceOwnershipAsync(
            guard,
            analysisId,
            executionToken,
            cancellationToken);
        Directory.CreateDirectory(repositoryPath);

        try
        {
            var archiveUrl = $"repos/{repository.Owner}/{repository.Name}/zipball/{commitHash}";

            using var request = new HttpRequestMessage(HttpMethod.Get, archiveUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", personalAccessToken);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new GitHubIntegrationException(
                    GitHubErrorKind.ServiceUnavailable,
                    "Сервис GitHub недоступен или превышено время ожидания ответа при загрузке архива.",
                    ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    await HandleArchiveErrorResponseAsync(response, cancellationToken);
                }

                if (response.Content.Headers.ContentLength.HasValue &&
                    response.Content.Headers.ContentLength.Value > MaxArchiveSizeBytes)
                {
                    throw new ArchiveLimitExceededException("Размер архива превышает допустимый лимит 50 МиБ.");
                }

                // Потоковое скачивание с ограничением размера входного архива.
                await using (var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken))
                {
                    await EnsureWorkspaceOwnershipAsync(
                        guard, analysisId, executionToken, cancellationToken);

                    await using var tempFileStream = new FileStream(
                        tempZipPath,
                        FileMode.Create,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        81920,
                        useAsync: true);

                    var downloadBuffer = new byte[81920];
                    long totalDownloaded = 0;
                    int read;

                    while ((read = await networkStream.ReadAsync(
                        downloadBuffer.AsMemory(0, downloadBuffer.Length),
                        cancellationToken)) > 0)
                    {
                        totalDownloaded += read;
                        if (totalDownloaded > MaxArchiveSizeBytes)
                        {
                            throw new ArchiveLimitExceededException(
                                "Размер архива превышает допустимый лимит 50 МиБ.");
                        }

                        await EnsureWorkspaceOwnershipAsync(
                            guard, analysisId, executionToken, cancellationToken);

                        await tempFileStream.WriteAsync(
                            downloadBuffer.AsMemory(0, read),
                            cancellationToken);
                    }
                }
            }

            // Открываем архив и распаковываем его только пока guard подтверждает владение.
            await EnsureWorkspaceOwnershipAsync(
                guard, analysisId, executionToken, cancellationToken);

            await using (var zipFileStream = new FileStream(
                tempZipPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                useAsync: true))
            {
                await ExtractArchiveInternalCoreAsync(
                    zipFileStream,
                    repositoryPath,
                    cancellationToken,
                    () => EnsureWorkspaceOwnershipAsync(
                        guard, analysisId, executionToken, cancellationToken));
            }

            return repositoryPath;
        }
        catch (Exception ex)
        {
            try
            {
                if (guard != null && guard.IsAcquired)
                {
                    var isStillOwner = await guard.ValidateOwnershipAsync(
                        [(int)Domain.Enums.AnalysisStatus.Running],
                        CancellationToken.None);

                    if (isStillOwner)
                    {
                        await CleanupAsync(analysisId, executionToken, guard, CancellationToken.None);
                    }
                }
            }
            catch (Exception cleanupEx)
            {
                _logger?.LogError(cleanupEx,
                    "Ошибка при токенизированной очистке каталога анализа {AnalysisId} (токен {ExecutionToken}) после сбоя загрузки архива.",
                    analysisId, executionToken);
                ex.Data["CleanupException"] = cleanupEx.Message;
            }
            throw;
        }
        finally
        {
            try
            {
                // При потере владения ZIP оставляем cleaner'у. Даже File.Exists
                // не вызывается до подтверждения владения.
                await EnsureWorkspaceOwnershipAsync(
                    guard,
                    analysisId,
                    executionToken,
                    CancellationToken.None);

                if (File.Exists(tempZipPath))
                {
                    await EnsureWorkspaceOwnershipAsync(
                        guard,
                        analysisId,
                        executionToken,
                        CancellationToken.None);

                    File.Delete(tempZipPath);
                }
            }
            catch (Exception cleanupEx)
            {
                _logger?.LogDebug(
                    cleanupEx,
                    "Временный архив {TempZipPath} оставлен для последующей очистки: владение workspace не подтверждено.",
                    tempZipPath);
            }
        }
    }

    public Task<string> DownloadAndExtractAsync(
        string repositoryUrl,
        string commitHash,
        int analysisId,
        string personalAccessToken,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Файловая операция рабочего пространства запрещена без действующего IAnalysisLeaseGuard.");
    }

    public Task<string> DownloadAndExtractAsync(
        string repositoryUrl,
        string commitHash,
        int analysisId,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Файловая операция рабочего пространства запрещена без действующего IAnalysisLeaseGuard.");
    }

    /// <summary>
    /// Распаковывает архив из потока с проверкой ограничений размера и квот безопасности.
    /// </summary>
    public Task ExtractArchiveAsync(
        Stream archiveStream,
        string destinationRepositoryPath,
        int analysisId,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Файловая операция рабочего пространства запрещена без действующего IAnalysisLeaseGuard.");
    }

    public Task ExtractArchiveAsync(
        Stream archiveStream,
        string destinationRepositoryPath,
        int analysisId,
        Guid executionToken,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Файловая операция рабочего пространства запрещена без действующего IAnalysisLeaseGuard.");
    }

    internal static Task ExtractArchiveInternalAsync(
        Stream archiveStream,
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        return ExtractArchiveInternalCoreAsync(
            archiveStream,
            repositoryPath,
            cancellationToken,
            ensureOwnership: null);
    }

    private static async Task ExtractArchiveInternalCoreAsync(
        Stream archiveStream,
        string repositoryPath,
        CancellationToken cancellationToken,
        Func<Task>? ensureOwnership)
    {
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);

        var fileCount = 0;
        long totalExtractedBytes = 0;
        var copyBuffer = new byte[81920];

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ensureOwnership is not null)
                await ensureOwnership();

            var relativePath = GetRelativePath(entry.FullName);
            if (string.IsNullOrEmpty(relativePath) || ShouldExclude(relativePath))
                continue;

            var destinationPath = GetSafeDestinationPath(repositoryPath, relativePath);

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                if (ensureOwnership is not null)
                    await ensureOwnership();

                Directory.CreateDirectory(destinationPath);
                continue;
            }

            // Unix S_IFLNK = 0xA000. Символические ссылки не извлекаются.
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                continue;

            fileCount++;
            if (fileCount > MaxFileCount)
            {
                throw new ArchiveLimitExceededException(
                    "Количество файлов в архиве превышает лимит 10 000.");
            }

            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (destinationDirectory is not null)
            {
                if (ensureOwnership is not null)
                    await ensureOwnership();

                Directory.CreateDirectory(destinationDirectory);
            }

            await using var entryStream = entry.Open();

            if (ensureOwnership is not null)
                await ensureOwnership();

            await using var destinationStream = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);

            long entryBytes = 0;
            int read;

            while ((read = await entryStream.ReadAsync(
                copyBuffer.AsMemory(0, copyBuffer.Length),
                cancellationToken)) > 0)
            {
                entryBytes += read;
                totalExtractedBytes += read;

                if (entryBytes > MaxSingleFileSizeBytes)
                {
                    throw new ArchiveLimitExceededException(
                        "Размер одного извлечённого файла превышает лимит 50 МиБ.");
                }

                if (totalExtractedBytes > MaxTotalExtractedSizeBytes)
                {
                    throw new ArchiveLimitExceededException(
                        "Суммарный объём извлечённых файлов превышает лимит 250 МиБ.");
                }

                if (ensureOwnership is not null)
                    await ensureOwnership();

                await destinationStream.WriteAsync(
                    copyBuffer.AsMemory(0, read),
                    cancellationToken);
            }
        }
    }

    public async Task CleanupAsync(
        int analysisId,
        Guid executionToken,
        IAnalysisLeaseGuard activeGuard,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(analysisId);

        if (executionToken == Guid.Empty)
            throw new ArgumentException("Не указан токен выполнения анализа.", nameof(executionToken));

        ArgumentNullException.ThrowIfNull(activeGuard);

        if (activeGuard.AnalysisId != analysisId || activeGuard.ExecutionToken != executionToken)
        {
            throw new ArgumentException(
                $"Указанный activeGuard ({activeGuard.AnalysisId}, {activeGuard.ExecutionToken}) не соответствует параметрам анализа ({analysisId}, {executionToken}).",
                nameof(activeGuard));
        }

        var fullBaseDir = Path.GetFullPath(_baseStoragePath);
        var fullTargetDir = Path.GetFullPath(GetExecutionDirectory(analysisId, executionToken));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var basePrefix = fullBaseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!fullTargetDir.StartsWith(basePrefix, comparison))
        {
            throw new InvalidOperationException(
                $"Целевой каталог {fullTargetDir} выходит за границы разрешённого хранилища {fullBaseDir}.");
        }

        var fullAnalysisParent = Path.GetFullPath(Path.Combine(fullBaseDir, analysisId.ToString()));
        if (fullTargetDir.Equals(fullBaseDir, comparison) ||
            fullTargetDir.Equals(fullAnalysisParent, comparison))
        {
            throw new InvalidOperationException(
                $"Целевой каталог {fullTargetDir} не является изолированным каталогом конкретного исполнения.");
        }

        await EnsureWorkspaceCleanupOwnershipAsync(
            activeGuard, analysisId, executionToken, cancellationToken);

        if (!Directory.Exists(fullTargetDir))
            return;

        await EnsureWorkspaceCleanupOwnershipAsync(
            activeGuard, analysisId, executionToken, cancellationToken);

        var targetDirectoryInfo = new DirectoryInfo(fullTargetDir);
        if (targetDirectoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return;

        await EnsureWorkspaceCleanupOwnershipAsync(
            activeGuard, analysisId, executionToken, cancellationToken);
        Directory.Delete(fullTargetDir, recursive: true);

        // Пустой каталог анализа удаляется только после отдельных проверок перед
        // Exists, перечислением и Delete. Потеря guard не поглощается.
        await EnsureWorkspaceCleanupOwnershipAsync(
            activeGuard, analysisId, executionToken, CancellationToken.None);

        if (Directory.Exists(fullAnalysisParent))
        {
            await EnsureWorkspaceCleanupOwnershipAsync(
                activeGuard, analysisId, executionToken, CancellationToken.None);

            var hasEntries = Directory.EnumerateFileSystemEntries(fullAnalysisParent).Any();
            if (!hasEntries)
            {
                await EnsureWorkspaceCleanupOwnershipAsync(
                    activeGuard, analysisId, executionToken, CancellationToken.None);
                Directory.Delete(fullAnalysisParent, recursive: false);
            }
        }

        var containerExchangeDir = Path.Combine(
            Path.GetTempPath(),
            "netscope-database-analyzer",
            $"{analysisId}_{executionToken:N}");

        await EnsureWorkspaceCleanupOwnershipAsync(
            activeGuard, analysisId, executionToken, CancellationToken.None);

        if (Directory.Exists(containerExchangeDir))
        {
            await EnsureWorkspaceCleanupOwnershipAsync(
                activeGuard, analysisId, executionToken, CancellationToken.None);

            var containerDirectoryInfo = new DirectoryInfo(containerExchangeDir);
            if (!containerDirectoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                await EnsureWorkspaceCleanupOwnershipAsync(
                    activeGuard, analysisId, executionToken, CancellationToken.None);
                Directory.Delete(containerExchangeDir, recursive: true);
            }
        }
    }

    public async Task CleanupAsync(int analysisId, Guid executionToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(analysisId);

        if (executionToken == Guid.Empty)
        {
            throw new ArgumentException("Не указан токен выполнения анализа.", nameof(executionToken));
        }

        if (_leaseGuardProvider == null)
        {
            throw new InvalidOperationException(
                $"Безопасное удаление рабочего каталога анализа {analysisId} (токен {executionToken}) невозможно: не настроен IAnalysisLeaseGuardProvider (fail-closed).");
        }

        await using var guard = await _leaseGuardProvider.AcquireGuardAsync(analysisId, executionToken, cancellationToken);
        if (guard == null || !guard.IsAcquired)
        {
            throw new InvalidOperationException(
                $"Удаление рабочего каталога анализа {analysisId} (токен {executionToken}) отклонено: не удалось получить advisory lock (блокировка удерживается другим процессом).");
        }

        await CleanupAsync(analysisId, executionToken, guard, cancellationToken);
    }

    public Task CleanupAsync(int analysisId, CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Файловая операция рабочего пространства запрещена без действующего IAnalysisLeaseGuard.");
    }

    private static async Task HandleArchiveErrorResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var statusCode = response.StatusCode;

        if (statusCode == HttpStatusCode.Unauthorized)
        {
            throw new GitHubIntegrationException(
                GitHubErrorKind.TokenExpired,
                "Персональный токен GitHub недействителен или отозван.");
        }

        if (statusCode == HttpStatusCode.NotFound)
        {
            throw new GitHubIntegrationException(
                GitHubErrorKind.RepoNotFound,
                "Репозиторий GitHub не найден или токен не имеет к нему доступа.");
        }

        string body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
        }

        if ((int)statusCode == 429)
        {
            throw Application.Services.GitHubRateLimitParser.ParseRateLimitException(response, body, DateTimeOffset.UtcNow);
        }

        if (statusCode == HttpStatusCode.Forbidden)
        {
            var isRateLimited = false;

            if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues) &&
                int.TryParse(remainingValues.FirstOrDefault(), out var remaining) &&
                remaining == 0)
            {
                isRateLimited = true;
            }

            if (!isRateLimited && !string.IsNullOrEmpty(body) &&
                (body.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("API rate limit exceeded", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase)))
            {
                isRateLimited = true;
            }

            if (isRateLimited)
            {
                throw Application.Services.GitHubRateLimitParser.ParseRateLimitException(response, body, DateTimeOffset.UtcNow);
            }

            throw new GitHubIntegrationException(
                GitHubErrorKind.AccessDenied,
                "Доступ к репозиторию GitHub запрещен (недостаточно прав или ограничения организации).")
            {
                StatusCode = statusCode
            };
        }

        if ((int)statusCode >= 500)
        {
            throw new GitHubIntegrationException(
                GitHubErrorKind.ServiceUnavailable,
                "Сервис GitHub временно недоступен.");
        }

        throw new GitHubIntegrationException(
            GitHubErrorKind.ServiceUnavailable,
            $"GitHub API вернул статус {(int)statusCode} {statusCode} при загрузке архива.");
    }

    private static string GetRelativePath(string archivePath)
    {
        var normalizedPath = archivePath.Replace('\\', '/');
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length <= 1)
        {
            return string.Empty;
        }

        return string.Join(Path.DirectorySeparatorChar, segments.Skip(1));
    }

    private static bool ShouldExclude(string relativePath)
    {
        var segments = relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        return segments.Any(ExcludedDirectories.Contains);
    }

    private static string GetSafeDestinationPath(string repositoryPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Относительный путь файла в архиве не может быть пустым.");
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Обнаружена попытка выхода за пределы целевой директории (Zip Slip): абсолютный путь.");
        }

        var fullRepositoryPath = Path.GetFullPath(repositoryPath);
        var destinationPath = Path.GetFullPath(Path.Combine(fullRepositoryPath, relativePath));

        var relativeToRoot = Path.GetRelativePath(fullRepositoryPath, destinationPath);

        if (relativeToRoot.Equals("..", StringComparison.Ordinal) ||
            relativeToRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativeToRoot.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relativeToRoot))
        {
            throw new InvalidOperationException("Обнаружена попытка выхода за пределы целевой директории (Zip Slip).");
        }

        var canonicalRootDir = fullRepositoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!destinationPath.StartsWith(canonicalRootDir, comparison) &&
            !destinationPath.Equals(fullRepositoryPath, comparison))
        {
            throw new InvalidOperationException("Обнаружена попытка выхода за пределы целевой директории (Zip Slip).");
        }

        return destinationPath;
    }
}
