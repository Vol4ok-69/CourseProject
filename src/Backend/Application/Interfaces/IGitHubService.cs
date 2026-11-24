using Application.DTOs;

namespace Application.Interfaces;

public interface IGitHubService
{
    Task<GitHubRepositoryInfoDto> GetRepositoryInfoAsync(
        string repositoryUrl,
        string personalAccessToken,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<IReadOnlyList<GitHubCommitInfo>> GetCommitPagesAsync(
        string repositoryUrl,
        string? sha,
        string personalAccessToken,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubCommitInfo>> GetCommitsAsync(
        string repositoryUrl,
        string personalAccessToken,
        CancellationToken cancellationToken = default);

    Task<string> VerifyTokenAndGetUsernameAsync(
        string personalAccessToken,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubCommitInfo>> GetCommitsAsync(
        string repositoryUrl,
        CancellationToken cancellationToken = default);
}

public record GitHubCommitInfo(
    string Hash,
    DateTime? Date,
    IReadOnlyList<string> ParentHashes)
{
    public GitHubCommitInfo(string hash, DateTime? date)
        : this(hash, date, Array.Empty<string>())
    {
    }
}
