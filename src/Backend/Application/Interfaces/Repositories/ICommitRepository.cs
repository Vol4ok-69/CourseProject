using Domain.Entities;

namespace Application.Interfaces.Repositories;

public interface ICommitRepository
{
    Task<Commit?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<Commit?> GetByHashAsync(int projectId, string commitHash, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Commit>> GetByProjectIdAsync(int projectId, CancellationToken cancellationToken = default);

    Task AddAsync(Commit commit, CancellationToken cancellationToken = default);
}