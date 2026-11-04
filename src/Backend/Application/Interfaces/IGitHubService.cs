namespace Application.Interfaces;

public interface IGitHubService
{
    Task<IReadOnlyList<GitHubCommitInfo>> GetCommitsAsync(string repositoryUrl, string personalAccessToken, CancellationToken cancellationToken = default);

    Task<string> VerifyTokenAndGetUsernameAsync(string personalAccessToken, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubCommitInfo>> GetCommitsAsync(string repositoryUrl, CancellationToken cancellationToken = default);
}

public record GitHubCommitInfo(string Hash, DateTime? Date);
