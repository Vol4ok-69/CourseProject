using Application.Interfaces;
using Application.Interfaces.Repositories;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using System.Data;

namespace Infrastructure.Repository;

public class AnalysisRepository(AppDbContext context) : IAnalysisRepository
{
    private static readonly SemaphoreSlim _syncLock = new(1, 1);

    public int? GetConnectionProcessId()
    {
        try
        {
            if (context.Database.IsNpgsql() && context.Database.GetDbConnection() is NpgsqlConnection conn)
            {
                if (conn.State == ConnectionState.Closed)
                {
                    conn.Open();
                    var pid = conn.ProcessID;
                    conn.Close();
                    return pid > 0 ? pid : null;
                }
                return conn.ProcessID > 0 ? conn.ProcessID : null;
            }
        }
        catch
        {
        }
        return null;
    }

    public async Task<(Analysis? Analysis, int? ProcessId)> GetAnalysisWithProcessIdAsync(int id, CancellationToken cancellationToken = default)
    {
        if (context.Database.IsNpgsql())
        {
            var conn = context.Database.GetDbConnection();
            bool wasClosed = conn.State == ConnectionState.Closed;
            if (wasClosed)
            {
                await conn.OpenAsync(cancellationToken);
            }
            try
            {
                int? pid = (conn as NpgsqlConnection)?.ProcessID;
                var analysis = await context.Analyses
                    .AsNoTracking()
                    .Include(a => a.Results)
                    .Include(a => a.Commit)
                        .ThenInclude(c => c.Project)
                    .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
                return (analysis, pid > 0 ? pid : null);
            }
            finally
            {
                if (wasClosed && conn.State == ConnectionState.Open)
                {
                    await conn.CloseAsync();
                }
            }
        }

        var fallback = await GetByIdAsync(id, cancellationToken);
        return (fallback, null);
    }

    public async Task<Analysis?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await context.Analyses
            .AsNoTracking()
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

    private sealed class AnalysisCandidateRow
    {
        public int Id { get; set; }
        public int Status { get; set; }
        public int AttemptCount { get; set; }
        public int MaxAttempts { get; set; }
        public DateTime? LeaseExpiration { get; set; }
    }

    public async Task<Analysis?> TryAcquireNextPendingOrStaleAsync(
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var executionToken = Guid.NewGuid();

        if (context.Database.IsNpgsql())
        {
            var skippedCandidateIds = new List<int>();
            const int maxCandidateSkips = 50;

            while (skippedCandidateIds.Count < maxCandidateSkips)
            {
                await using var transaction = await context.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted,
                    cancellationToken);

                var skippedParam = new NpgsqlParameter("skippedCandidateIds", NpgsqlDbType.Array | NpgsqlDbType.Integer)
                {
                    Value = skippedCandidateIds.ToArray()
                };
                var skippedCountParam = new NpgsqlParameter("skippedCount", NpgsqlDbType.Integer)
                {
                    Value = skippedCandidateIds.Count
                };

                var candidates = await context.Database
                    .SqlQueryRaw<AnalysisCandidateRow>(
                        """
                        SELECT "Id", "Status", "AttemptCount", "MaxAttempts", "LeaseExpiration"
                        FROM "Analyses"
                        WHERE ("Status" = 0
                           OR ("Status" = 1 AND ("LeaseExpiration" IS NULL OR "LeaseExpiration" < clock_timestamp())))
                          AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= clock_timestamp())
                          AND (@skippedCount = 0 OR "Id" != ALL(@skippedCandidateIds))
                          AND NOT EXISTS (
                              SELECT 1 FROM pg_locks
                              WHERE locktype = 'advisory'
                                AND classid = hashtext('netscope_analysis_workspace')
                                AND objid = "Analyses"."Id"
                                AND objsubid = 2
                                AND granted = true
                          )
                        ORDER BY "CreatedAt" ASC
                        LIMIT 1
                        FOR UPDATE SKIP LOCKED
                        """,
                        skippedCountParam,
                        skippedParam)
                    .ToListAsync(cancellationToken);

                if (candidates.Count == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return null;
                }

                var candidate = candidates[0];

                // Poison Pill: если задача Running, срок аренды истёк, и исчерпан лимит попыток
                if (candidate.Status == (int)AnalysisStatus.Running &&
                    candidate.LeaseExpiration.HasValue &&
                    candidate.AttemptCount >= candidate.MaxAttempts)
                {
                    var pFailId = new NpgsqlParameter("id", NpgsqlDbType.Integer) { Value = candidate.Id };
                    await context.Database.ExecuteSqlRawAsync(
                        """
                        UPDATE "Analyses"
                        SET "Status" = 3,
                            "CompletedAt" = clock_timestamp(),
                            "LeaseExpiration" = NULL,
                            "ErrorMessage" = 'Превышен лимит попыток выполнения анализа (' || "AttemptCount"::text || '/' || "MaxAttempts"::text || '). Задача переведена в статус сбоя (Poison Pill).'
                        WHERE "Id" = @id
                          AND "Status" = 1
                          AND "LeaseExpiration" < clock_timestamp()
                          AND "AttemptCount" >= "MaxAttempts"
                        """,
                        [pFailId],
                        cancellationToken);

                    try
            {
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                context.ChangeTracker.Clear();
                throw;
            }
                    skippedCandidateIds.Add(candidate.Id);
                    continue;
                }

                // Атомарно захватываем transaction-level advisory lock в едином пространстве ключей netscope_analysis_workspace.
                var pLockId = new NpgsqlParameter("id", NpgsqlDbType.Integer) { Value = candidate.Id };
                var isLockAcquired = await context.Database
                    .SqlQueryRaw<bool>(
                        """
                        SELECT pg_try_advisory_xact_lock(hashtext('netscope_analysis_workspace'), @id) AS "Value"
                        """,
                        pLockId)
                    .SingleAsync(cancellationToken);

                if (!isLockAcquired)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    skippedCandidateIds.Add(candidate.Id);
                    continue;
                }

                var pAcquireId = new NpgsqlParameter("id", NpgsqlDbType.Integer) { Value = candidate.Id };
                var pLeaseSec = new NpgsqlParameter("leaseSeconds", NpgsqlDbType.Double) { Value = leaseDuration.TotalSeconds };
                var pToken = new NpgsqlParameter("token", NpgsqlDbType.Uuid) { Value = executionToken };

                var updated = await context.Database.ExecuteSqlRawAsync(
                    """
                    UPDATE "Analyses"
                    SET "Status" = 1,
                        "StartedAt" = COALESCE("StartedAt", clock_timestamp()),
                        "LeaseExpiration" = clock_timestamp() + make_interval(secs => @leaseSeconds),
                        "ExecutionToken" = @token,
                        "AttemptCount" = "AttemptCount" + 1,
                        "NextAttemptAt" = NULL
                    WHERE "Id" = @id
                    """,
                    [pAcquireId, pLeaseSec, pToken],
                    cancellationToken);

                if (updated == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    skippedCandidateIds.Add(candidate.Id);
                    continue;
                }

                var analysis = await context.Analyses
                    .Include(a => a.Commit)
                        .ThenInclude(c => c.Project)
                    .FirstOrDefaultAsync(a => a.Id == candidate.Id, cancellationToken);

                if (analysis == null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return null;
                }

                try
            {
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                context.ChangeTracker.Clear();
                throw;
            }
                return analysis;
            }

            return null;
        }
        else
        {
            // Потокобезопасная синхронизация для тестовых сред (SQLite / InMemory)
            await _syncLock.WaitAsync(cancellationToken);
            try
            {
                var candidate = await context.Analyses
                    .Include(a => a.Commit)
                        .ThenInclude(c => c.Project)
                    .Where(a => (a.Status == AnalysisStatus.Pending ||
                                (a.Status == AnalysisStatus.Running && (a.LeaseExpiration == null || a.LeaseExpiration < now)))
                             && (a.NextAttemptAt == null || a.NextAttemptAt <= now))
                    .OrderBy(a => a.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);

                if (candidate == null)
                {
                    return null;
                }

                if (candidate.Status == AnalysisStatus.Running && candidate.AttemptCount >= candidate.MaxAttempts)
                {
                    candidate.Status = AnalysisStatus.Failed;
                    candidate.CompletedAt = now;
                    candidate.LeaseExpiration = null;
                    candidate.ErrorMessage = $"Превышен лимит попыток выполнения анализа ({candidate.AttemptCount}/{candidate.MaxAttempts}). Задача переведена в статус сбоя (Poison Pill).";
                    await context.SaveChangesAsync(cancellationToken);
                    return null;
                }

                candidate.Status = AnalysisStatus.Running;
                candidate.StartedAt ??= now;
                candidate.LeaseExpiration = now.Add(leaseDuration);
                candidate.ExecutionToken = executionToken;
                candidate.AttemptCount++;
                candidate.NextAttemptAt = null;

                await context.SaveChangesAsync(cancellationToken);
                return candidate;
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
        if (context.Database.IsNpgsql())
        {
            var pId = new NpgsqlParameter("id", NpgsqlDbType.Integer) { Value = analysisId };
            var pToken = new NpgsqlParameter("token", NpgsqlDbType.Uuid) { Value = executionToken };
            var pLeaseSec = new NpgsqlParameter("leaseSeconds", NpgsqlDbType.Double) { Value = leaseDuration.TotalSeconds };

            var affected = await context.Database.ExecuteSqlRawAsync(
                """
                UPDATE "Analyses"
                SET "LeaseExpiration" = clock_timestamp() + make_interval(secs => @leaseSeconds)
                WHERE "Id" = @id
                  AND "ExecutionToken" = @token
                  AND "Status" = 1
                  AND "LeaseExpiration" >= clock_timestamp()
                """,
                [pId, pToken, pLeaseSec],
                cancellationToken);

            return affected > 0;
        }
        else
        {
            var now = DateTime.UtcNow;
            var newExpiration = now.Add(leaseDuration);

            var affected = await context.Analyses
                .Where(a => a.Id == analysisId &&
                            a.ExecutionToken == executionToken &&
                            a.Status == AnalysisStatus.Running &&
                            a.LeaseExpiration != null && a.LeaseExpiration >= now)
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
    }

    private AppDbContext CreateGuardDbContext(NpgsqlConnection guardConnection)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(guardConnection);

        try
        {
            var coreExtension = context.GetService<IDbContextOptions>()?
                .FindExtension<CoreOptionsExtension>();

            if (coreExtension?.Interceptors != null)
            {
                builder.AddInterceptors(coreExtension.Interceptors);
            }
        }
        catch
        {
        }

        return new AppDbContext(builder.Options);
    }

    public async Task<bool> CompleteAsync(
        int analysisId,
        Guid executionToken,
        IReadOnlyList<AnalysisResult> results,
        IAnalysisLeaseGuard guard,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(guard);

        if (!guard.IsAcquired)
        {
            return false;
        }

        var now = DateTime.UtcNow;

        if (context.Database.IsNpgsql())
        {
            if (guard.Connection is not NpgsqlConnection guardConnection || guardConnection.State != ConnectionState.Open)
            {
                return false;
            }

            await using var guardContext = CreateGuardDbContext(guardConnection);
            await using var transaction = await guardContext.Database.BeginTransactionAsync(cancellationToken);

            var pId = new NpgsqlParameter("id", NpgsqlDbType.Integer) { Value = analysisId };
            var pToken = new NpgsqlParameter("token", NpgsqlDbType.Uuid) { Value = executionToken };

            const string sql = """
                UPDATE "Analyses"
                SET "Status" = 2,
                    "CompletedAt" = clock_timestamp(),
                    "LeaseExpiration" = NULL
                WHERE "Id" = @id
                  AND "ExecutionToken" = @token
                  AND "Status" = 1
                  AND "LeaseExpiration" >= clock_timestamp()
                  AND EXISTS (
                      SELECT 1 FROM pg_locks
                      WHERE locktype = 'advisory'
                        AND classid = hashtext('netscope_analysis_workspace')
                        AND objid = @id
                        AND objsubid = 2
                        AND pid = pg_backend_pid()
                        AND mode = 'ShareLock'
                        AND granted = true
                  )
                """;

            var affected = await guardContext.Database.ExecuteSqlRawAsync(sql, [pId, pToken], cancellationToken);
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
                    guardContext.AnalysisResults.Add(result);
                }

                await guardContext.SaveChangesAsync(cancellationToken);
            }

            try
            {
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                guardContext.ChangeTracker.Clear();
                context.ChangeTracker.Clear();
                throw;
            }

            var local = context.Analyses.Local.FirstOrDefault(a => a.Id == analysisId);
            if (local != null)
            {
                local.Status = AnalysisStatus.Completed;
                local.CompletedAt = now;
                local.LeaseExpiration = null;
            }

            return true;
        }
        else
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            var affected = await context.Analyses
                .Where(a => a.Id == analysisId &&
                            a.ExecutionToken == executionToken &&
                            a.Status == AnalysisStatus.Running &&
                            a.LeaseExpiration != null && a.LeaseExpiration >= now)
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

            try
            {
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                context.ChangeTracker.Clear();
                throw;
            }

            var local = context.Analyses.Local.FirstOrDefault(a => a.Id == analysisId);
            if (local != null)
            {
                local.Status = AnalysisStatus.Completed;
                local.CompletedAt = now;
                local.LeaseExpiration = null;
            }

            return true;
        }
    }

    public async Task<bool> FailAsync(
        int analysisId,
        Guid executionToken,
        string? errorMessage,
        IAnalysisLeaseGuard guard,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(guard);

        if (!guard.IsAcquired)
        {
            return false;
        }

        var now = DateTime.UtcNow;

        if (context.Database.IsNpgsql())
        {
            if (guard.Connection is not NpgsqlConnection guardConnection || guardConnection.State != ConnectionState.Open)
            {
                return false;
            }

            await using var guardContext = CreateGuardDbContext(guardConnection);

            var pId = new NpgsqlParameter("id", NpgsqlDbType.Integer) { Value = analysisId };
            var pToken = new NpgsqlParameter("token", NpgsqlDbType.Uuid) { Value = executionToken };
            var pError = new NpgsqlParameter("errorMessage", NpgsqlDbType.Text) { Value = (object?)errorMessage ?? DBNull.Value };

            const string sql = """
                UPDATE "Analyses"
                SET "Status" = 3,
                    "CompletedAt" = clock_timestamp(),
                    "LeaseExpiration" = NULL,
                    "ErrorMessage" = @errorMessage
                WHERE "Id" = @id
                  AND "ExecutionToken" = @token
                  AND "Status" = 1
                  AND "LeaseExpiration" >= clock_timestamp()
                  AND EXISTS (
                      SELECT 1 FROM pg_locks
                      WHERE locktype = 'advisory'
                        AND classid = hashtext('netscope_analysis_workspace')
                        AND objid = @id
                        AND objsubid = 2
                        AND pid = pg_backend_pid()
                        AND mode = 'ShareLock'
                        AND granted = true
                  )
                """;

            var affected = await guardContext.Database.ExecuteSqlRawAsync(sql, [pId, pToken, pError], cancellationToken);
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
        else
        {
            var affected = await context.Analyses
                .Where(a => a.Id == analysisId &&
                            a.ExecutionToken == executionToken &&
                            a.Status == AnalysisStatus.Running &&
                            a.LeaseExpiration != null && a.LeaseExpiration >= now)
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
    }

    public async Task<bool> ScheduleRetryAsync(
        int analysisId,
        Guid executionToken,
        DateTime nextAttemptAt,
        string errorMessage,
        IAnalysisLeaseGuard guard,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(guard);

        if (!guard.IsAcquired)
        {
            return false;
        }

        var now = DateTime.UtcNow;

        if (context.Database.IsNpgsql())
        {
            if (guard.Connection is not NpgsqlConnection guardConnection || guardConnection.State != ConnectionState.Open)
            {
                return false;
            }

            await using var guardContext = CreateGuardDbContext(guardConnection);

            var pId = new NpgsqlParameter("id", NpgsqlDbType.Integer) { Value = analysisId };
            var pToken = new NpgsqlParameter("token", NpgsqlDbType.Uuid) { Value = executionToken };
            var pNextAttempt = new NpgsqlParameter("nextAttemptAt", NpgsqlDbType.TimestampTz) { Value = nextAttemptAt };
            var pError = new NpgsqlParameter("errorMessage", NpgsqlDbType.Text) { Value = errorMessage };

            const string sql = """
                UPDATE "Analyses"
                SET "Status" = 0,
                    "ExecutionToken" = NULL,
                    "LeaseExpiration" = NULL,
                    "NextAttemptAt" = @nextAttemptAt,
                    "ErrorMessage" = @errorMessage
                WHERE "Id" = @id
                  AND "ExecutionToken" = @token
                  AND "Status" = 1
                  AND "LeaseExpiration" >= clock_timestamp()
                  AND EXISTS (
                      SELECT 1 FROM pg_locks
                      WHERE locktype = 'advisory'
                        AND classid = hashtext('netscope_analysis_workspace')
                        AND objid = @id
                        AND objsubid = 2
                        AND pid = pg_backend_pid()
                        AND mode = 'ShareLock'
                        AND granted = true
                  )
                """;

            var affected = await guardContext.Database.ExecuteSqlRawAsync(sql, [pId, pToken, pNextAttempt, pError], cancellationToken);
            if (affected == 0)
            {
                return false;
            }

            var local = context.Analyses.Local.FirstOrDefault(a => a.Id == analysisId);
            if (local != null)
            {
                local.Status = AnalysisStatus.Pending;
                local.ExecutionToken = null;
                local.LeaseExpiration = null;
                local.NextAttemptAt = nextAttemptAt;
                local.ErrorMessage = errorMessage;
            }

            return true;
        }
        else
        {
            var affected = await context.Analyses
                .Where(a => a.Id == analysisId &&
                            a.ExecutionToken == executionToken &&
                            a.Status == AnalysisStatus.Running &&
                            a.LeaseExpiration != null && a.LeaseExpiration >= now)
                .ExecuteUpdateAsync(setter => setter
                    .SetProperty(a => a.Status, AnalysisStatus.Pending)
                    .SetProperty(a => a.ExecutionToken, (Guid?)null)
                    .SetProperty(a => a.LeaseExpiration, (DateTime?)null)
                    .SetProperty(a => a.NextAttemptAt, nextAttemptAt)
                    .SetProperty(a => a.ErrorMessage, errorMessage),
                    cancellationToken);

            if (affected == 0)
            {
                return false;
            }

            var local = context.Analyses.Local.FirstOrDefault(a => a.Id == analysisId);
            if (local != null)
            {
                local.Status = AnalysisStatus.Pending;
                local.ExecutionToken = null;
                local.LeaseExpiration = null;
                local.NextAttemptAt = nextAttemptAt;
                local.ErrorMessage = errorMessage;
            }

            return true;
        }
    }

    private sealed class NpgsqlAnalysisRepositoryTransaction(IDbContextTransaction transaction, AppDbContext context) : IAnalysisRepositoryTransaction
    {
        private bool _completed;

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                context.ChangeTracker.Clear();
                throw;
            }
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
                try
            {
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                context.ChangeTracker.Clear();
                throw;
            }
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
