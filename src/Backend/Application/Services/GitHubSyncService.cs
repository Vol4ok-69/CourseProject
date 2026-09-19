using Application.DTOs;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Domain.Entities;

namespace Application.Services;

public class GitHubSyncService(
    IGitHubService gitHubService,
    IProjectRepository projectRepository,
    ICommitRepository commitRepository)
{
    public async Task<GitHubSyncResultDto?> SyncAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var project = await projectRepository.GetByIdAsync(projectId, cancellationToken);

        if (project is null)
        {
            return null;
        }

        var githubCommits = await gitHubService.GetCommitsAsync(project.RepoUrl, cancellationToken);

        var added = 0;
        var skipped = 0;

        foreach (var githubCommit in githubCommits)
        {
            var existingCommit = await commitRepository.GetByHashAsync(projectId, githubCommit.Hash, cancellationToken);

            if (existingCommit is not null)
            {
                skipped++;
                continue;
            }

            if (githubCommit.Date is null)
            {
                continue;
            }

            var commit = new Commit
            {
                ProjectId = projectId,
                CommitHash = githubCommit.Hash,
                CommitDate = githubCommit.Date.Value
            };

            await commitRepository.AddAsync(commit, cancellationToken);
            added++;
        }

        return new GitHubSyncResultDto
        {
            Added = added,
            Skipped = skipped
        };
    }
}