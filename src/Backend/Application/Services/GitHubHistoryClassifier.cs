using Application.DTOs;
using Application.Enums;
using Application.Interfaces.Repositories;

namespace Application.Services;

public static class GitHubHistoryClassifier
{
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
        CancellationToken cancellationToken = default)
    {
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

        // 6. Проверяем, является ли prevHead предком хотя бы одной достигнутой проверенной границы
        var isAncestorOfBoundary = await IsAncestorAsync(projectId, prevHead, verifiedBoundaries, commitRepository, cancellationToken);
        if (isAncestorOfBoundary == AncestorResult.IsAncestor)
        {
            return HistoryClassification.FastForward;
        }
        if (isAncestorOfBoundary == AncestorResult.IncompleteGraph)
        {
            return HistoryClassification.InsufficientEvidence;
        }

        // 7. Если prevHead не предок, ищем общее проверенное пересечение
        var currentAncestry = new HashSet<string>(fetchedHashes, StringComparer.OrdinalIgnoreCase);
        foreach (var b in verifiedBoundaries)
        {
            currentAncestry.Add(b);
        }

        var commonResult = await FindCommonAncestorAsync(projectId, prevHead, currentAncestry, commitRepository, cancellationToken);
        return commonResult switch
        {
            CommonAncestorResult.FoundCommon => HistoryClassification.NonFastForwardHistory,
            CommonAncestorResult.NoCommonRootReached => HistoryClassification.IndependentHistory,
            _ => HistoryClassification.InsufficientEvidence
        };
    }

    private static async Task<AncestorResult> IsAncestorAsync(
        int projectId,
        string targetAncestor,
        IReadOnlyCollection<string> startNodes,
        ICommitRepository commitRepository,
        CancellationToken cancellationToken)
    {
        if (startNodes.Count == 0)
        {
            return AncestorResult.NotAncestor;
        }

        var queue = new Queue<string>(startNodes);
        var visited = new HashSet<string>(startNodes, StringComparer.OrdinalIgnoreCase);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = queue.Dequeue();

            if (string.Equals(current, targetAncestor, StringComparison.OrdinalIgnoreCase))
            {
                return AncestorResult.IsAncestor;
            }

            var commit = await commitRepository.GetByHashAsync(projectId, current, cancellationToken);
            if (commit is null || !commit.IsVerified)
            {
                return AncestorResult.IncompleteGraph;
            }

            foreach (var parent in commit.ParentHashes)
            {
                if (visited.Add(parent))
                {
                    queue.Enqueue(parent);
                }
            }
        }

        return AncestorResult.NotAncestor;
    }

    private static async Task<CommonAncestorResult> FindCommonAncestorAsync(
        int projectId,
        string prevHead,
        IReadOnlySet<string> currentAncestry,
        ICommitRepository commitRepository,
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

            var commit = await commitRepository.GetByHashAsync(projectId, current, cancellationToken);
            if (commit is null || !commit.IsVerified)
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
