namespace Application.Interfaces;

public interface IGitHubService
{
    Task<IReadOnlyList<GitHubCommitInfo>> GetCommitsAsync(string repositoryUrl, CancellationToken cancellationToken = default);
}

public record GitHubCommitInfo(string Hash, DateTime? Date);