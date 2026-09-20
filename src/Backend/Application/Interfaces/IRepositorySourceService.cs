namespace Application.Interfaces;

public interface IRepositorySourceService
{
    Task<string> DownloadAndExtractAsync(string repositoryUrl, string commitHash, int analysisId, CancellationToken cancellationToken = default);

    Task CleanupAsync(int analysisId, CancellationToken cancellationToken = default);
}