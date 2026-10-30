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
        var existingProjects = await projectRepository.GetByOwnerIdAsync(ownerId, cancellationToken);

        if (existingProjects.Any(project => string.Equals(project.Name, dto.Name, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Проект с таким именем уже существует.");
        }

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

        var existingProjects = await projectRepository.GetByOwnerIdAsync(project.OwnerId, cancellationToken);

        if (existingProjects.Any(p => p.Id != id && string.Equals(p.Name, dto.Name, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Проект с таким именем уже существует.");
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
