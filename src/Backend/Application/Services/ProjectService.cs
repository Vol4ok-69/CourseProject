using Application.DTOs;
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

    public async Task<Project> CreateAsync(CreateProjectDto dto, int ownerId, CancellationToken cancellationToken = default)
    {
        var project = new Project
        {
            Name = dto.Name,
            RepoUrl = dto.RepoUrl,
            OwnerId = ownerId
        };

        await projectRepository.AddAsync(project, cancellationToken);

        return project;
    }
}