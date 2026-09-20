using Application.Interfaces;
using Infrastructure.GitHub.Models;
using System.IO.Compression;

namespace Infrastructure.GitHub;

public class RepositorySourceService(HttpClient httpClient) : IRepositorySourceService
{
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
            analysisId.ToString());

        var repositoryPath = Path.Combine(analysisDirectory, "repository");

        Directory.CreateDirectory(repositoryPath);

        try
        {
            var archiveUrl = $"repos/{repository.Owner}/{repository.Name}/zipball/{commitHash}";

            using var response = await httpClient.GetAsync(archiveUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            response.EnsureSuccessStatusCode();

            await using var archiveStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = GetRelativePath(entry.FullName);

                if (string.IsNullOrEmpty(relativePath))
                    continue;

                if (ShouldExclude(relativePath))
                    continue;

                var destinationPath = GetSafeDestinationPath(repositoryPath, relativePath);

                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                var destinationDirectory = Path.GetDirectoryName(destinationPath);

                if (destinationDirectory is not null)
                    Directory.CreateDirectory(destinationDirectory);

                await using var entryStream = entry.Open();
                await using var destinationStream = new FileStream(
                    destinationPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    useAsync: true);

                await entryStream.CopyToAsync(destinationStream, cancellationToken);
            }

            return repositoryPath;
        }
        catch
        {
            await CleanupAsync(analysisId, CancellationToken.None);
            throw;
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
            analysisId.ToString());

        if (Directory.Exists(analysisDirectory))
            Directory.Delete(analysisDirectory, recursive: true);

        return Task.CompletedTask;
    }

    private static string GetRelativePath(string archivePath)
    {
        var normalizedPath = archivePath.Replace('\\', '/');
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length <= 1)
            return string.Empty;

        return string.Join(
            Path.DirectorySeparatorChar,
            segments.Skip(1));
    }

    private static bool ShouldExclude(string relativePath)
    {
        var segments = relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        return segments.Any(ExcludedDirectories.Contains);
    }

    private static string GetSafeDestinationPath(string repositoryPath, string relativePath)
    {
        var fullRepositoryPath = Path.GetFullPath(repositoryPath).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var destinationPath = Path.GetFullPath(
            Path.Combine(repositoryPath, relativePath));

        if (!destinationPath.StartsWith(fullRepositoryPath, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Archive entry is outside repository directory: {relativePath}");
        }

        return destinationPath;
    }
}