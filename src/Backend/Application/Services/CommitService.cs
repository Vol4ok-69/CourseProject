using Application.DTOs;
using Application.Interfaces.Repositories;
using Domain.Entities;

namespace Application.Services;

public class CommitService(ICommitRepository commitRepository, IProjectRepository projectRepository)
{
    public async Task<Commit?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await commitRepository.GetByIdAsync(id, cancellationToken);
    }

    public async Task<(IReadOnlyList<Commit> Items, int TotalCount)> GetByProjectIdAsync(
        int projectId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        return await commitRepository.GetByProjectIdAsync(projectId, page, pageSize, cancellationToken);
    }

    public async Task<Commit?> CreateAsync(
        int projectId,
        CreateCommitDto dto,
        CancellationToken cancellationToken = default)
    {
        var project = await projectRepository.GetByIdAsync(projectId, cancellationToken);

        if (project is null)
        {
            return null;
        }

        if (dto.CommitDate is null)
        {
            throw new ArgumentException("Дата коммита обязательна для заполнения.", nameof(dto));
        }

        if (dto.CommitDate.Value.Kind == DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "Дата коммита должна содержать часовой пояс (например, суффикс 'Z' или смещение '+00:00').",
                nameof(dto));
        }

        var normalizedCommitHash = dto.CommitHash.ToLowerInvariant();
        var normalizedCommitDate = dto.CommitDate.Value.ToUniversalTime();

        var existingCommit = await commitRepository.GetByHashAsync(
            projectId,
            normalizedCommitHash,
            cancellationToken);

        if (existingCommit is not null)
        {
            throw new InvalidOperationException("Коммит с таким хэшем уже существует в проекте.");
        }

        var commit = new Commit
        {
            ProjectId = projectId,
            CommitHash = normalizedCommitHash,
            CommitDate = normalizedCommitDate
        };

        await commitRepository.AddAsync(commit, cancellationToken);

        return commit;
    }
}
