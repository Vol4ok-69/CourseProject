using Application.Interfaces.Repositories;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repository;

public class CommitRepository(AppDbContext context) : ICommitRepository
{
    public async Task<Commit?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await context.Commits
            .Include(commit => commit.Project)
            .FirstOrDefaultAsync(commit => commit.Id == id, cancellationToken);
    }

    public async Task<Commit?> GetByHashAsync(int projectId, string commitHash, CancellationToken cancellationToken = default)
    {
        return await context.Commits
            .AsNoTracking()
            .FirstOrDefaultAsync(commit => commit.ProjectId == projectId && commit.CommitHash == commitHash, cancellationToken);
    }

    public async Task<IReadOnlyList<Commit>> GetByProjectIdAsync(int projectId, CancellationToken cancellationToken = default)
    {
        return await context.Commits
            .AsNoTracking()
            .Where(commit => commit.ProjectId == projectId)
            .OrderByDescending(commit => commit.CommitDate)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Commit commit, CancellationToken cancellationToken = default)
    {
        await context.Commits.AddAsync(commit, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }
}