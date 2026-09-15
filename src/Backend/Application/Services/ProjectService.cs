using Application.Interfaces.Repositories;
using Domain.Entities;

namespace Application.Services;

public class ProjectService(IProjectRepository projectRepository)
{
    public async Task<Project?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await projectRepository.GetByIdAsync(id, cancellationToken);
    }

    public async Task<IReadOnlyList<Project>> GetByOwnerIdAsync(int ownerId, CancellationToken cancellationToken = default)
    {
        return await projectRepository.GetByOwnerIdAsync(ownerId, cancellationToken);
    }

    public async Task AddAsync(Project project, CancellationToken cancellationToken = default)
    {
        await projectRepository.AddAsync(project, cancellationToken);
    }
}