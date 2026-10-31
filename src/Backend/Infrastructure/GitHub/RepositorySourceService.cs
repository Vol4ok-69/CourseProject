using Application.Exceptions;
using Application.Interfaces;
using Infrastructure.GitHub.Models;
using System.IO.Compression;

namespace Infrastructure.GitHub;

public class RepositorySourceService(HttpClient httpClient) : IRepositorySourceService
{
    public const long MaxArchiveSizeBytes = 50 * 1024 * 1024; // 50 МиБ (52 428 800 байт)
    public const long MaxSingleFileSizeBytes = 50 * 1024 * 1024; // 50 МиБ (52 428 800 байт)
    public const long MaxTotalExtractedSizeBytes = 250 * 1024 * 1024; // 250 МиБ (262 144 000 байт)
    public const int MaxFileCount = 10_000;

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

    public async Task<string> DownloadAndExtractAsync(string repositoryUrl, string commitHash, int analysisId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
            throw new ArgumentException("Repository URL is required.", nameof(repositoryUrl));

        if (string.IsNullOrWhiteSpace(commitHash))
            throw new ArgumentException("Commit hash is required.", nameof(commitHash));

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(analysisId);

        var repository = GitHubRepositoryReference.Parse(repositoryUrl);

        var analysisDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "data",
            "analysis",
            analysisId.ToString()
        );

        var repositoryPath = Path.Combine(analysisDirectory, "repository");
        Directory.CreateDirectory(repositoryPath);

        var tempZipPath = Path.Combine(analysisDirectory, "archive.zip");

        try
        {
            var archiveUrl = $"repos/{repository.Owner}/{repository.Name}/zipball/{commitHash}";

            using var response = await httpClient.GetAsync(archiveUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength.HasValue &&
                response.Content.Headers.ContentLength.Value > MaxArchiveSizeBytes)
            {
                throw new ArchiveLimitExceededException("Размер архива превышает допустимый лимит 50 МиБ.");
            }

            // Потоковое скачивание с ограничением размера входного архива
            await using (var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var tempFileStream = new FileStream(
                tempZipPath,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                81920,
                useAsync: true))
            {
                var downloadBuffer = new byte[81920];
                long totalDownloaded = 0;
                int read;

                while ((read = await networkStream.ReadAsync(downloadBuffer.AsMemory(0, downloadBuffer.Length), cancellationToken)) > 0)
                {
                    totalDownloaded += read;
                    if (totalDownloaded > MaxArchiveSizeBytes)
                    {
                        throw new ArchiveLimitExceededException("Размер архива превышает допустимый лимит 50 МиБ.");
                    }

                    await tempFileStream.WriteAsync(downloadBuffer.AsMemory(0, read), cancellationToken);
                }
            }

            // Потоковая контролируемая распаковка архива с проверкой квот
            await using (var zipFileStream = new FileStream(
                tempZipPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                useAsync: true))
            {
                await ExtractArchiveInternalAsync(zipFileStream, repositoryPath, cancellationToken);
            }

            return repositoryPath;
        }
        catch
        {
            await CleanupAsync(analysisId, CancellationToken.None);
            throw;
        }
        finally
        {
            if (File.Exists(tempZipPath))
            {
                try
                {
                    File.Delete(tempZipPath);
                }
                catch
                {
                }
            }
        }
    }

    /// <summary>
    /// Распаковывает архив из потока с проверкой ограничений размера и квот безопасности.
    /// </summary>
    public async Task ExtractArchiveAsync(
        Stream archiveStream,
        string destinationRepositoryPath,
        int analysisId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRepositoryPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(analysisId);

        Directory.CreateDirectory(destinationRepositoryPath);

        try
        {
            await ExtractArchiveInternalAsync(archiveStream, destinationRepositoryPath, cancellationToken);
        }
        catch
        {
            await CleanupAsync(analysisId, CancellationToken.None);
            throw;
        }
    }

    private static async Task ExtractArchiveInternalAsync(
        Stream archiveStream,
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);

        var fileCount = 0;
        long totalExtractedBytes = 0;
        var copyBuffer = new byte[81920];

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = GetRelativePath(entry.FullName);

            if (string.IsNullOrEmpty(relativePath) || ShouldExclude(relativePath))
            {
                continue;
            }

            var destinationPath = GetSafeDestinationPath(repositoryPath, relativePath);

            // Обработка директории
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            // Запрет символических ссылок (Unix permissions S_IFLNK = 0xA000 в старших 16 битах)
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
            {
                continue;
            }

            // Контроль количества файлов
            fileCount++;
            if (fileCount > MaxFileCount)
            {
                throw new ArchiveLimitExceededException("Количество файлов в архиве превышает лимит 10 000.");
            }

            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (destinationDirectory is not null)
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            await using var entryStream = entry.Open();
            await using var destinationStream = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);

            long entryBytes = 0;
            int read;

            // Поточный контроль размера отдельного файла и суммарного объема
            while ((read = await entryStream.ReadAsync(copyBuffer.AsMemory(0, copyBuffer.Length), cancellationToken)) > 0)
            {
                entryBytes += read;
                totalExtractedBytes += read;

                if (entryBytes > MaxSingleFileSizeBytes)
                {
                    throw new ArchiveLimitExceededException("Размер одного извлечённого файла превышает лимит 50 МиБ.");
                }

                if (totalExtractedBytes > MaxTotalExtractedSizeBytes)
                {
                    throw new ArchiveLimitExceededException("Суммарный объём извлечённых файлов превышает лимит 250 МиБ.");
                }

                await destinationStream.WriteAsync(copyBuffer.AsMemory(0, read), cancellationToken);
            }
        }
    }

    public Task CleanupAsync(int analysisId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(analysisId);

        var analysisDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "data",
            "analysis",
            analysisId.ToString()
        );

        if (Directory.Exists(analysisDirectory))
        {
            try
            {
                Directory.Delete(analysisDirectory, recursive: true);
            }
            catch
            {
            }
        }

        return Task.CompletedTask;
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
        var fullRepositoryPath = Path.GetFullPath(repositoryPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var destinationPath = Path.GetFullPath(Path.Combine(repositoryPath, relativePath));

        if (!destinationPath.StartsWith(fullRepositoryPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Обнаружена попытка выхода за пределы целевой директории (Zip Slip).");
        }

        return destinationPath;
    }
}
