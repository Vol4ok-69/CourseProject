using Application.DTOs;
using Domain.Entities;

namespace Application.Interfaces.Repositories;

public interface ICommitRepository
{
    Task<Commit?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<Commit?> GetByHashAsync(int projectId, string commitHash, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<Commit> Items, int TotalCount)> GetByProjectIdAsync(int projectId, int page, int pageSize, CancellationToken cancellationToken = default);

    Task AddAsync(Commit commit, CancellationToken cancellationToken = default);

    Task<IReadOnlySet<string>> GetExistingHashesAsync(int projectId, IReadOnlyCollection<string> commitHashes, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, bool>> GetHashesVerificationStatusAsync(int projectId, IReadOnlyCollection<string> commitHashes, CancellationToken cancellationToken = default);

    Task<SyncBatchResult> SaveSyncBatchAsync(
        int projectId,
        IReadOnlyList<Commit> newCommits,
        IReadOnlyList<CommitUpdateDto> existingToUpdate,
        ProjectCheckpointDto checkpoint,
        ProjectCheckpointSnapshotDto expectedCheckpoint,
        CancellationToken cancellationToken = default);

    Task<int> AddRangeAsync(IReadOnlyList<Commit> commits, CancellationToken cancellationToken = default);
}
