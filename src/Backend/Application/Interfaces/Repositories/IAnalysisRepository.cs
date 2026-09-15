using Domain.Entities;

namespace Application.Interfaces.Repositories;

public interface IAnalysisRepository
{
    Task<Analysis?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Analysis>> GetByCommitIdAsync(int commitId, CancellationToken cancellationToken = default);

    Task AddAsync(Analysis analysis, CancellationToken cancellationToken = default);

    Task UpdateAsync(Analysis analysis, CancellationToken cancellationToken = default);
}