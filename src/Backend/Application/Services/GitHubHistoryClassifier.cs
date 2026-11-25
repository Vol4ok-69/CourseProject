using Application.DTOs;
using Domain.Entities;
using Application.Enums;
using Application.Interfaces.Repositories;

namespace Application.Services;

public static class GitHubHistoryClassifier
{
    public const int DefaultMaxTraversalNodes = 10_000;

    private enum AncestorResult
    {
        IsAncestor,
        NotAncestor,
        IncompleteGraph
    }

    private enum CommonAncestorResult
    {
        FoundCommon,
        NoCommonRootReached,
        IncompleteGraph
    }

    public static async Task<HistoryClassification> ClassifyAsync(
        int projectId,
        ProjectCheckpointSnapshotDto projectSnapshot,
        GitHubRepositoryInfoDto repoInfo,
        string headSha,
        IReadOnlySet<string> fetchedHashes,
        IReadOnlySet<string> verifiedBoundaries,
        ICommitRepository commitRepository,
        CancellationToken cancellationToken = default,
        int maxTraversalNodes = DefaultMaxTraversalNodes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTraversalNodes, 1);
        var prevHead = projectSnapshot.LastSyncedCommitHash;

        // 1. InitialSync: чекпоинт отсутствует
        if (string.IsNullOrWhiteSpace(prevHead))
        {
            return HistoryClassification.InitialSync;
        }

        // 2. RepositoryChanged: изменился хост или идентификатор репозитория
        if (!string.Equals(projectSnapshot.LastSyncedRepositoryHost, repoInfo.ApiHost, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(projectSnapshot.LastSyncedRepositoryId, repoInfo.RepositoryId, StringComparison.OrdinalIgnoreCase))
        {
            return HistoryClassification.RepositoryChanged;
        }

        // 3. BranchSwitch: репозиторий тот же, но имя отслеживаемой ветки изменилось
        if (!string.Equals(projectSnapshot.LastSyncedBranch, repoInfo.DefaultBranch, StringComparison.OrdinalIgnoreCase))
        {
            return HistoryClassification.BranchSwitch;
        }

        // 4. Проверяем доказанность старого чекпоинта в базе данных
        var prevStatus = await commitRepository.GetHashesVerificationStatusAsync(projectId, [prevHead], cancellationToken);
        if (!prevStatus.TryGetValue(prevHead, out var isPrevVerified) || !isPrevVerified)
        {
            return HistoryClassification.InsufficientEvidence;
        }

        // 5. FastForward: prevHead совпадает с актуальным headSha или получен в текущем обходе
        if (string.Equals(prevHead, headSha, StringComparison.OrdinalIgnoreCase) || fetchedHashes.Contains(prevHead))
        {
            return HistoryClassification.FastForward;
        }

        // 6–7. Единый ограниченный обход предков verifiedBoundaries с кэшированием узлов:
        // одновременно проверяем, является ли prevHead предком (FastForward),
        // и собираем достижимых предков в currentAncestry (разделяя known и processed).
        var commitCache = new Dictionary<string, Commit?>(StringComparer.OrdinalIgnoreCase);
        var currentAncestry = new HashSet<string>(fetchedHashes, StringComparer.OrdinalIgnoreCase);
        var processedBoundaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var boundaryQueue = new Queue<string>();
        var boundaryGraphIncomplete = false;

        foreach (var b in verifiedBoundaries)
        {
            currentAncestry.Add(b);
            if (!processedBoundaries.Contains(b))
            {
                boundaryQueue.Enqueue(b);
            }
        }

        while (boundaryQueue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = boundaryQueue.Dequeue();

            if (!processedBoundaries.Add(current))
            {
                continue;
            }

            if (string.Equals(current, prevHead, StringComparison.OrdinalIgnoreCase))
            {
                return HistoryClassification.FastForward;
            }

            var (limitExceeded, commit) = await TryGetCommitCachedAsync(
                projectId,
                current,
                commitRepository,
                commitCache,
                maxTraversalNodes,
                cancellationToken);

            if (limitExceeded)
            {
                if (boundaryQueue.Any(queued => string.Equals(queued, prevHead, StringComparison.OrdinalIgnoreCase)))
                {
                    return HistoryClassification.FastForward;
                }

                boundaryGraphIncomplete = true;
                break;
            }

            if (commit is null || !commit.IsVerified)
            {
                boundaryGraphIncomplete = true;
                continue;
            }

            currentAncestry.Add(current);

            foreach (var parent in commit.ParentHashes)
            {
                if (!processedBoundaries.Contains(parent))
                {
                    boundaryQueue.Enqueue(parent);
                }
            }
        }

        var commonResult = await FindCommonAncestorAsync(
            projectId,
            prevHead,
            currentAncestry,
            commitRepository,
            commitCache,
            maxTraversalNodes,
            cancellationToken);

        return commonResult switch
        {
            CommonAncestorResult.FoundCommon => HistoryClassification.NonFastForwardHistory,
            CommonAncestorResult.NoCommonRootReached when !boundaryGraphIncomplete => HistoryClassification.IndependentHistory,
            _ => HistoryClassification.InsufficientEvidence
        };
    }

    private static async Task<(bool LimitExceeded, Commit? Commit)> TryGetCommitCachedAsync(
        int projectId,
        string hash,
        ICommitRepository commitRepository,
        Dictionary<string, Commit?> cache,
        int maxTraversalNodes,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(hash, out var cached))
        {
            return (false, cached);
        }

        if (cache.Count >= maxTraversalNodes)
        {
            return (true, null);
        }

        var commit = await commitRepository.GetByHashAsync(projectId, hash, cancellationToken);
        cache[hash] = commit;
        return (false, commit);
    }

    private static async Task<CommonAncestorResult> FindCommonAncestorAsync(
        int projectId,
        string prevHead,
        IReadOnlySet<string> currentAncestry,
        ICommitRepository commitRepository,
        Dictionary<string, Commit?> commitCache,
        int maxTraversalNodes,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        queue.Enqueue(prevHead);
        visited.Add(prevHead);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = queue.Dequeue();

            if (currentAncestry.Contains(current))
            {
                return CommonAncestorResult.FoundCommon;
            }

            var (limitExceeded, commit) = await TryGetCommitCachedAsync(
                projectId,
                current,
                commitRepository,
                commitCache,
                maxTraversalNodes,
                cancellationToken);

            if (limitExceeded || commit is null || !commit.IsVerified)
            {
                return CommonAncestorResult.IncompleteGraph;
            }

            foreach (var parent in commit.ParentHashes)
            {
                if (visited.Add(parent))
                {
                    queue.Enqueue(parent);
                }
            }
        }

        return CommonAncestorResult.NoCommonRootReached;
    }
}
