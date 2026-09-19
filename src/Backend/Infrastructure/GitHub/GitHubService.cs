using Application.Interfaces;
using Infrastructure.GitHub.Models;
using System.Text.Json;

namespace Infrastructure.GitHub;

public class GitHubService(HttpClient httpClient) : IGitHubService
{
    public async Task<IReadOnlyList<GitHubCommitInfo>> GetCommitsAsync(string repositoryUrl, CancellationToken cancellationToken = default)
    {
        var repository = GitHubRepositoryReference.Parse(repositoryUrl);

        var response = await httpClient.GetAsync(
            $"repos/{repository.Owner}/{repository.Name}/commits",
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);


        var commits = JsonSerializer.Deserialize<List<GitHubCommitResponse>>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

        return commits?
            .Select(commit => new GitHubCommitInfo(
                commit.Sha,
                commit.Commit?.Author?.Date))
            .ToList()
            ?? [];
    }
}