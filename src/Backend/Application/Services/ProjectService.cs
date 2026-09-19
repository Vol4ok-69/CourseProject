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

    public async Task<Project?> UpdateAsync(int id, UpdateProjectDto dto, CancellationToken cancellationToken = default)
    {
        var project = await projectRepository.GetByIdAsync(id, cancellationToken);

        if (project is null)
        {
            return null;
        }

        project.Name = dto.Name;
        project.RepoUrl = dto.RepoUrl;

        await projectRepository.UpdateAsync(project, cancellationToken);

        return project;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var project = await projectRepository.GetByIdAsync(id, cancellationToken);

        if (project is null)
        {
            return false;
        }

        await projectRepository.DeleteAsync(project, cancellationToken);

        return true;
    }
}