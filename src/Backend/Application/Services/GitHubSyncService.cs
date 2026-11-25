using Application.DTOs;
using Application.Enums;
using Application.Exceptions;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Domain.Entities;

namespace Application.Services;

/// <summary>
/// Сервис синхронизации коммитов проекта с GitHub с инкрементальным обходом графа (Streaming DAG).
/// </summary>
public class GitHubSyncService
{
    private readonly IGitHubService _gitHubService;
    private readonly IProjectRepository _projectRepository;
    private readonly ICommitRepository _commitRepository;
    private readonly IGitHubCredentialService _gitHubCredentialService;
    private readonly IGitHubSyncCoordinator _syncCoordinator;

    public GitHubSyncService(
        IGitHubService gitHubService,
        IProjectRepository projectRepository,
        ICommitRepository commitRepository,
        IGitHubCredentialService gitHubCredentialService,
        IGitHubSyncCoordinator? syncCoordinator = null)
    {
        _gitHubService = gitHubService ?? throw new ArgumentNullException(nameof(gitHubService));
        _projectRepository = projectRepository ?? throw new ArgumentNullException(nameof(projectRepository));
        _commitRepository = commitRepository ?? throw new ArgumentNullException(nameof(commitRepository));
        _gitHubCredentialService = gitHubCredentialService ?? throw new ArgumentNullException(nameof(gitHubCredentialService));
        _syncCoordinator = syncCoordinator ?? new DefaultSyncCoordinator();
    }

    /// <summary>
    /// Выполняет синхронизацию коммитов проекта с GitHub с использованием персонального токена владельца проекта.
    /// </summary>
    /// <param name="projectId">Идентификатор синхронизируемого проекта.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Результат синхронизации либо <c>null</c>, если проект не найден.</returns>
    public async Task<GitHubSyncResultDto?> SyncAsync(int projectId, CancellationToken cancellationToken = default)
    {
        // 1. Захватываем process-local блокировку для защиты от конкурентной синхронизации одного проекта
        using var syncLock = _syncCoordinator.TryAcquire(projectId);
        if (syncLock is null)
        {
            throw new SyncAlreadyInProgressException();
        }

        // 2. Загружаем проект и фиксируем начальный снимок контрольной точки
        var project = await _projectRepository.GetByIdAsync(projectId, cancellationToken);
        if (project is null)
        {
            return null;
        }

        var projectSnapshot = new ProjectCheckpointSnapshotDto(
            project.LastSyncedCommitHash,
            project.LastSyncedBranch,
            project.LastSyncedRepositoryId,
            project.LastSyncedRepositoryHost);

        // 3. Получаем персональный токен доступа владельца проекта
        var personalAccessToken = await _gitHubCredentialService.GetUserTokenAsync(project.OwnerId, cancellationToken);
        if (string.IsNullOrWhiteSpace(personalAccessToken))
        {
            throw new GitHubIntegrationException(
                GitHubErrorKind.TokenRequired,
                "Для синхронизации проекта требуется подключение персонального токена GitHub владельца проекта.");
        }

        // 4. Получаем метаданные репозитория GitHub и актуальный HEAD ветки по умолчанию
        var repoInfo = await _gitHubService.GetRepositoryInfoAsync(project.RepoUrl, personalAccessToken, cancellationToken);
        if (repoInfo is null || string.IsNullOrWhiteSpace(repoInfo.DefaultBranchHeadSha))
        {
            throw new GitHubIntegrationException(
                GitHubErrorKind.ServiceUnavailable,
                "Не удалось получить метаданные репозитория GitHub.");
        }

        var headSha = repoInfo.DefaultBranchHeadSha;

        // 5. Проверка быстрого пути (AlreadyCurrent)
        var checkpointMatches =
            string.Equals(projectSnapshot.LastSyncedRepositoryHost, repoInfo.ApiHost, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(projectSnapshot.LastSyncedRepositoryId, repoInfo.RepositoryId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(projectSnapshot.LastSyncedBranch, repoInfo.DefaultBranch, StringComparison.OrdinalIgnoreCase);

        if (checkpointMatches && string.Equals(projectSnapshot.LastSyncedCommitHash, headSha, StringComparison.OrdinalIgnoreCase))
        {
            var headVerification = await _commitRepository.GetHashesVerificationStatusAsync(projectId, [headSha], cancellationToken);
            if (headVerification.TryGetValue(headSha, out var isVerified) && isVerified)
            {
                return new GitHubSyncResultDto
                {
                    Added = 0,
                    Skipped = 0,
                    AlreadyCurrent = true,
                    HistoryClassification = null
                };
            }
        }

        // 6. Инициализация множеств обхода DAG
        var vFetched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var vExpanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var vVerified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parentsByHash = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var newCandidates = new Dictionary<string, Commit>(StringComparer.OrdinalIgnoreCase);
        var existingUnverifiedUpdates = new Dictionary<string, CommitUpdateDto>(StringComparer.OrdinalIgnoreCase);
        var fixedHeadFetched = false;
        var uniqueFetchedCount = 0;
        var unresolvedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 7. Постраничный потоковый обход графа от фиксированного HEAD
        await foreach (var page in _gitHubService.GetCommitPagesAsync(project.RepoUrl, headSha, personalAccessToken, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageUniqueHashes = page.Select(c => c.Hash).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var pageDbStatuses = await _commitRepository.GetHashesVerificationStatusAsync(projectId, pageUniqueHashes, cancellationToken);

            foreach (var commit in page)
            {
                if (vFetched.Contains(commit.Hash))
                {
                    continue;
                }

                vFetched.Add(commit.Hash);
                uniqueFetchedCount++;

                if (string.Equals(commit.Hash, headSha, StringComparison.OrdinalIgnoreCase))
                {
                    fixedHeadFetched = true;
                }

                if (pageDbStatuses.TryGetValue(commit.Hash, out var isVerified) && isVerified)
                {
                    vVerified.Add(commit.Hash);
                    continue;
                }

                var parents = commit.ParentHashes ?? Array.Empty<string>();
                parentsByHash[commit.Hash] = parents;
                vExpanded.Add(commit.Hash);

                var normalizedHash = commit.Hash.ToLowerInvariant();
                if (!pageDbStatuses.ContainsKey(commit.Hash))
                {
                    newCandidates[normalizedHash] = new Commit
                    {
                        ProjectId = projectId,
                        CommitHash = normalizedHash,
                        CommitDate = commit.Date ?? DateTime.UtcNow,
                        ParentHashes = parents.ToArray(),
                        IsVerified = true
                    };
                }
                else
                {
                    existingUnverifiedUpdates[normalizedHash] = new CommitUpdateDto(normalizedHash, parents);
                }
            }

            var allExpandedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in vExpanded)
            {
                if (parentsByHash.TryGetValue(h, out var pList))
                {
                    foreach (var p in pList)
                    {
                        allExpandedParents.Add(p);
                    }
                }
            }

            var uncheckedParents = allExpandedParents
                .Where(p => !vFetched.Contains(p) && !vVerified.Contains(p))
                .ToList();

            if (uncheckedParents.Count > 0)
            {
                var parentDbStatuses = await _commitRepository.GetHashesVerificationStatusAsync(projectId, uncheckedParents, cancellationToken);
                foreach (var (pHash, pVerified) in parentDbStatuses)
                {
                    if (pVerified)
                    {
                        vVerified.Add(pHash);
                    }
                }
            }

            unresolvedParents = new HashSet<string>(
                allExpandedParents.Where(p => !vFetched.Contains(p) && !vVerified.Contains(p)),
                StringComparer.OrdinalIgnoreCase);

            if (fixedHeadFetched && unresolvedParents.Count == 0)
            {
                break;
            }
        }

        // 8. Проверка условий успешного завершения
        if (!fixedHeadFetched)
        {
            throw new IncompleteHistoryException(
                IncompleteHistoryKind.HeadNotFetched,
                $"Синхронизация не смогла получить зафиксированный HEAD-коммит '{headSha}'.");
        }

        if (unresolvedParents.Count > 0)
        {
            throw new IncompleteHistoryException(
                IncompleteHistoryKind.UnresolvedParents,
                $"Синхронизация завершилась с неразрешенными родительскими связями ({unresolvedParents.Count} коммитов).",
                unresolvedParents);
        }

        // 9. Классификация истории коммитов
        var classification = await GitHubHistoryClassifier.ClassifyAsync(
            projectId,
            projectSnapshot,
            repoInfo,
            headSha,
            vFetched,
            vVerified,
            _commitRepository,
            cancellationToken);

        // 10. Атомарное сохранение графа и контрольной точки в короткой транзакции
        var checkpoint = new ProjectCheckpointDto(
            headSha,
            repoInfo.DefaultBranch,
            repoInfo.RepositoryId,
            repoInfo.ApiHost,
            DateTime.UtcNow);

        var syncResult = await _commitRepository.SaveSyncBatchAsync(
            projectId,
            newCandidates.Values.ToList(),
            existingUnverifiedUpdates.Values.ToList(),
            checkpoint,
            projectSnapshot,
            cancellationToken);

        var added = syncResult.InsertedHashes.Count;
        var skipped = Math.Max(0, uniqueFetchedCount - added);

        return new GitHubSyncResultDto
        {
            Added = added,
            Skipped = skipped,
            HistoryClassification = classification
        };
    }

    private sealed class DefaultSyncCoordinator : IGitHubSyncCoordinator
    {
        public IDisposable? TryAcquire(int projectId) => EmptyDisposable.Instance;
        private sealed class EmptyDisposable : IDisposable
        {
            public static readonly EmptyDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
