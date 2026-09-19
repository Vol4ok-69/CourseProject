using Domain.Entities;

public interface IProjectRepository
{
    Task<Project?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Project>> GetByOwnerIdAsync(int ownerId, CancellationToken cancellationToken = default);

    Task AddAsync(Project project, CancellationToken cancellationToken = default);

    Task UpdateAsync(Project project, CancellationToken cancellationToken = default);

    Task DeleteAsync(Project project, CancellationToken cancellationToken = default);
}