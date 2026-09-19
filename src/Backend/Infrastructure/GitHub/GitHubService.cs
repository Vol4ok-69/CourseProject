using Application.Interfaces;
using Infrastructure.GitHub.Models;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Infrastructure.GitHub;

public class GitHubService(HttpClient httpClient) : IGitHubService
{
    public async Task<IReadOnlyList<GitHubCommitInfo>> GetCommitsAsync(string repositoryUrl, CancellationToken cancellationToken = default)
    {
        var repository = GitHubRepositoryReference.Parse(repositoryUrl);

        var response = await httpClient.GetAsync($"repos/{repository.Owner}/{repository.Name}/commits", cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var commits = await JsonSerializer.DeserializeAsync<List<GitHubCommitResponse>>(stream, cancellationToken: cancellationToken);

        return commits?
            .Select(commit => new GitHubCommitInfo(
                commit.Sha,
                commit.Commit.Author.Date))
            .ToList()
            ?? [];
    }
}