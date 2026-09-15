using Application.Interfaces.Repositories;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repository;

public class AnalysisRepository(AppDbContext context) : IAnalysisRepository
{
    public async Task<Analysis?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await context.Analyses
            .Include(analysis => analysis.Results)
            .Include(analysis => analysis.Commit)
            .FirstOrDefaultAsync(analysis => analysis.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Analysis>> GetByCommitIdAsync(int commitId, CancellationToken cancellationToken = default)
    {
        return await context.Analyses
            .AsNoTracking()
            .Include(analysis => analysis.Results)
            .Where(analysis => analysis.CommitId == commitId)
            .OrderByDescending(analysis => analysis.StartedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Analysis analysis, CancellationToken cancellationToken = default)
    {
        await context.Analyses.AddAsync(analysis, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Analysis analysis, CancellationToken cancellationToken = default)
    {
        context.Analyses.Update(analysis);
        await context.SaveChangesAsync(cancellationToken);
    }
}