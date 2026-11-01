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

    public async Task<(IReadOnlyList<Commit> Items, int TotalCount)> GetByProjectIdAsync(
        int projectId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var query = context.Commits
            .AsNoTracking()
            .Where(commit => commit.ProjectId == projectId);

        var totalCount = await query.CountAsync(cancellationToken);

        var offset = ((long)page - 1) * pageSize;
        if (offset >= totalCount || offset > int.MaxValue)
        {
            return ([], totalCount);
        }

        var items = await query
            .OrderByDescending(commit => commit.CommitDate)
            .ThenByDescending(commit => commit.Id)
            .Skip((int)offset)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task AddAsync(Commit commit, CancellationToken cancellationToken = default)
    {
        await context.Commits.AddAsync(commit, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }
}
