using Application.Interfaces.Repositories;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;

namespace Infrastructure.Repository;

public class AnalysisRepository(AppDbContext context) : IAnalysisRepository
{
    private static readonly SemaphoreSlim _syncLock = new(1, 1);

    public async Task<Analysis?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await context.Analyses
            .Include(analysis => analysis.Results)
            .Include(analysis => analysis.Commit)
                .ThenInclude(commit => commit.Project)
            .FirstOrDefaultAsync(analysis => analysis.Id == id, cancellationToken);
    }

    public async Task<(IReadOnlyList<Analysis> Items, int TotalCount)> GetByCommitIdAsync(
        int commitId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var query = context.Analyses
            .AsNoTracking()
            .Where(analysis => analysis.CommitId == commitId);

        var totalCount = await query.CountAsync(cancellationToken);

        var offset = ((long)page - 1) * pageSize;
        if (offset >= totalCount || offset > int.MaxValue)
        {
            return ([], totalCount);
        }

        var items = await query
            .Include(analysis => analysis.Results)
            .OrderByDescending(analysis => analysis.CreatedAt)
            .ThenByDescending(analysis => analysis.Id)
            .Skip((int)offset)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
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

    public async Task<bool> HasActiveAnalysisAsync(int commitId, CancellationToken cancellationToken = default)
    {
        return await context.Analyses
            .AnyAsync(a => a.CommitId == commitId &&
                           (a.Status == AnalysisStatus.Pending || a.Status == AnalysisStatus.Running),
                      cancellationToken);
    }

    public async Task<IAnalysisRepositoryTransaction> BeginEnqueueTransactionAsync(int commitId, CancellationToken cancellationToken = default)
    {
        if (context.Database.IsNpgsql())
        {
            var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            await context.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(hashtext('analysis_enqueue_' || {0}::text));",
                new object[] { commitId },
                cancellationToken);

            return new NpgsqlAnalysisRepositoryTransaction(transaction, context);
        }
        else
        {
            await _syncLock.WaitAsync(cancellationToken);

            IDbContextTransaction? transaction = null;
            try
            {
                if (context.Database.CurrentTransaction == null && context.Database.IsRelational())
                {
                    transaction = await context.Database.BeginTransactionAsync(cancellationToken);
                }
            }
            catch
            {
            }

            return new InMemoryAnalysisRepositoryTransaction(_syncLock, transaction, context);
        }
    }

    public async Task<Analysis?> TryAcquireNextPendingOrStaleAsync(
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var executionToken = Guid.NewGuid();

        if (context.Database.IsNpgsql())
        {
            await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

            var candidateIds = await context.Database
                .SqlQueryRaw<int>(
                    """
                    SELECT "Id"
                    FROM "Analyses"
                    WHERE "Status" = 0
                       OR ("Status" = 1 AND ("LeaseExpiration" IS NULL OR "LeaseExpiration" < {0}))
                    ORDER BY "CreatedAt" ASC
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                    """,
                    now)
                .ToListAsync(cancellationToken);

            if (candidateIds.Count == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            var candidateId = candidateIds[0];

            var analysis = await context.Analyses
                .Include(a => a.Commit)
                    .ThenInclude(c => c.Project)
                .FirstOrDefaultAsync(a => a.Id == candidateId, cancellationToken);

            if (analysis == null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            analysis.Status = AnalysisStatus.Running;
            analysis.StartedAt ??= now;
            analysis.LeaseExpiration = now.Add(leaseDuration);
            analysis.ExecutionToken = executionToken;

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return analysis;
        }
        else
        {
            // Потокобезопасная синхронизация для тестовых сред (SQLite / InMemory)
            await _syncLock.WaitAsync(cancellationToken);
            try
            {
                var analysis = await context.Analyses
                    .Include(a => a.Commit)
                        .ThenInclude(c => c.Project)
                    .Where(a => a.Status == AnalysisStatus.Pending ||
                                (a.Status == AnalysisStatus.Running && (a.LeaseExpiration == null || a.LeaseExpiration < now)))
                    .OrderBy(a => a.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);

                if (analysis == null)
                {
                    return null;
                }

                analysis.Status = AnalysisStatus.Running;
                analysis.StartedAt ??= now;
                analysis.LeaseExpiration = now.Add(leaseDuration);
                analysis.ExecutionToken = executionToken;

                await context.SaveChangesAsync(cancellationToken);
                return analysis;
            }
            finally
            {
                _syncLock.Release();
            }
        }
    }

    public async Task<bool> RenewLeaseAsync(
        int analysisId,
        Guid executionToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var newExpiration = now.Add(leaseDuration);

        var affected = await context.Analyses
            .Where(a => a.Id == analysisId &&
                        a.ExecutionToken == executionToken &&
                        a.Status == AnalysisStatus.Running)
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(a => a.LeaseExpiration, newExpiration),
                cancellationToken);

        if (affected == 0)
        {
            return false;
        }

        var local = context.Analyses.Local.FirstOrDefault(a => a.Id == analysisId);
        if (local != null)
        {
            local.LeaseExpiration = newExpiration;
        }

        return true;
    }

    public async Task<bool> CompleteAsync(
        int analysisId,
        Guid executionToken,
        IReadOnlyList<AnalysisResult> results,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var affected = await context.Analyses
            .Where(a => a.Id == analysisId &&
                        a.ExecutionToken == executionToken &&
                        a.Status == AnalysisStatus.Running)
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(a => a.Status, AnalysisStatus.Completed)
                .SetProperty(a => a.CompletedAt, now)
                .SetProperty(a => a.LeaseExpiration, (DateTime?)null),
                cancellationToken);

        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        if (results.Count > 0)
        {
            foreach (var result in results)
            {
                result.AnalysisId = analysisId;
                context.AnalysisResults.Add(result);
            }

            await context.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        var local = context.Analyses.Local.FirstOrDefault(a => a.Id == analysisId);
        if (local != null)
        {
            local.Status = AnalysisStatus.Completed;
            local.CompletedAt = now;
            local.LeaseExpiration = null;
        }

        return true;
    }

    public async Task<bool> FailAsync(
        int analysisId,
        Guid executionToken,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var affected = await context.Analyses
            .Where(a => a.Id == analysisId &&
                        a.ExecutionToken == executionToken &&
                        a.Status == AnalysisStatus.Running)
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(a => a.Status, AnalysisStatus.Failed)
                .SetProperty(a => a.CompletedAt, now)
                .SetProperty(a => a.LeaseExpiration, (DateTime?)null)
                .SetProperty(a => a.ErrorMessage, errorMessage),
                cancellationToken);

        if (affected == 0)
        {
            return false;
        }

        var local = context.Analyses.Local.FirstOrDefault(a => a.Id == analysisId);
        if (local != null)
        {
            local.Status = AnalysisStatus.Failed;
            local.CompletedAt = now;
            local.LeaseExpiration = null;
            local.ErrorMessage = errorMessage;
        }

        return true;
    }

    public Task<bool> FailAsync(
        int analysisId,
        Guid executionToken,
        CancellationToken cancellationToken = default)
    {
        return FailAsync(analysisId, executionToken, null, cancellationToken);
    }

    private sealed class NpgsqlAnalysisRepositoryTransaction(IDbContextTransaction transaction, AppDbContext context) : IAnalysisRepositoryTransaction
    {
        private bool _completed;

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await transaction.CommitAsync(cancellationToken);
            _completed = true;
        }

        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            await transaction.RollbackAsync(cancellationToken);
            context.ChangeTracker.Clear();
            _completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_completed)
            {
                try
                {
                    await transaction.RollbackAsync();
                    context.ChangeTracker.Clear();
                }
                catch
                {
                }
            }

            await transaction.DisposeAsync();
        }
    }

    private sealed class InMemoryAnalysisRepositoryTransaction(SemaphoreSlim semaphore, IDbContextTransaction? transaction, AppDbContext context) : IAnalysisRepositoryTransaction
    {
        private bool _completed;
        private int _disposed;

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            _completed = true;
        }

        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            context.ChangeTracker.Clear();
            _completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                if (!_completed)
                {
                    if (transaction != null)
                    {
                        try
                        {
                            await transaction.RollbackAsync();
                        }
                        catch
                        {
                        }
                    }

                    context.ChangeTracker.Clear();
                }

                if (transaction != null)
                {
                    await transaction.DisposeAsync();
                }

                semaphore.Release();
            }
        }
    }
}
