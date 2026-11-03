using Application.DTOs;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Domain.Entities;

namespace Application.Services;

public class GitHubSyncService(IGitHubService gitHubService, IProjectRepository projectRepository, ICommitRepository commitRepository)
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

        // 1. Отбираем коммиты с валидной датой и учитываем внутрипакетные дубликаты
        var validCommits = new List<GitHubCommitInfo>();
        var candidateHashes = new HashSet<string>();

        foreach (var githubCommit in githubCommits)
        {
            if (githubCommit.Date is null)
            {
                continue;
            }

            if (!candidateHashes.Add(githubCommit.Hash))
            {
                skipped++;
                continue;
            }

            validCommits.Add(githubCommit);
        }

        if (validCommits.Count == 0)
        {
            return new GitHubSyncResultDto
            {
                Added = 0,
                Skipped = skipped
            };
        }

        // 2. Пакетная проверка наличия коммитов в базе данных за один SQL-запрос
        var existingHashes = await commitRepository.GetExistingHashesAsync(
            projectId,
            candidateHashes,
            cancellationToken);

        var commitsToInsert = new List<Commit>();

        foreach (var commitInfo in validCommits)
        {
            if (existingHashes.Contains(commitInfo.Hash))
            {
                skipped++;
            }
            else
            {
                commitsToInsert.Add(new Commit
                {
                    ProjectId = projectId,
                    CommitHash = commitInfo.Hash,
                    CommitDate = commitInfo.Date!.Value
                });
            }
        }

        // 3. Пакетное сохранение новых коммитов
        if (commitsToInsert.Count > 0)
        {
            var insertedCount = await commitRepository.AddRangeAsync(commitsToInsert, cancellationToken);
            added += insertedCount;
            skipped += Math.Max(0, commitsToInsert.Count - insertedCount);
        }

        return new GitHubSyncResultDto
        {
            Added = added,
            Skipped = skipped
        };
    }
}
