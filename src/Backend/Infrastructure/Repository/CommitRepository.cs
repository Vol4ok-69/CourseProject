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
        ArgumentNullException.ThrowIfNull(commit);

        await context.Commits.AddAsync(commit, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<string>> GetExistingHashesAsync(
        int projectId,
        IReadOnlyCollection<string> commitHashes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commitHashes);

        if (commitHashes.Count == 0)
        {
            return new HashSet<string>();
        }

        var distinctHashes = commitHashes.Distinct().ToList();

        var existingHashes = await context.Commits
            .AsNoTracking()
            .Where(commit => commit.ProjectId == projectId && distinctHashes.Contains(commit.CommitHash))
            .Select(commit => commit.CommitHash)
            .ToListAsync(cancellationToken);

        return existingHashes.ToHashSet();
    }

    public async Task<int> AddRangeAsync(
        IReadOnlyList<Commit> commits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commits);

        if (commits.Count == 0)
        {
            return 0;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await context.Commits.AddRangeAsync(commits, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            return commits.Count;
        }
        catch (DbUpdateException exception) when (!cancellationToken.IsCancellationRequested && IsCommitUniqueConstraintViolation(exception))
        {
            // Отсоединяем только добавляемые коммиты, чтобы сбросить неудавшийся батч
            // и не потерять отслеживаемые изменения других сущностей в ChangeTracker.
            foreach (var commit in commits)
            {
                var entry = context.Entry(commit);
                if (entry.State != EntityState.Detached)
                {
                    entry.State = EntityState.Detached;
                }
            }

            // Поштучное сохранение неконфликтующих коммитов при конкурентной гонке.
            var insertedCount = 0;
            foreach (var commit in commits)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await context.Commits.AddAsync(commit, cancellationToken);
                    await context.SaveChangesAsync(cancellationToken);
                    insertedCount++;
                }
                catch (DbUpdateException singleException) when (!cancellationToken.IsCancellationRequested && IsCommitUniqueConstraintViolation(singleException))
                {
                    var entry = context.Entry(commit);
                    if (entry.State != EntityState.Detached)
                    {
                        entry.State = EntityState.Detached;
                    }
                }
            }

            return insertedCount;
        }
    }

    private static bool IsCommitUniqueConstraintViolation(DbUpdateException exception)
    {
        if (exception.InnerException is Npgsql.PostgresException postgresException)
        {
            return postgresException.SqlState == "23505" &&
                   (postgresException.ConstraintName == "IX_Commits_ProjectId_CommitHash" ||
                    postgresException.ConstraintName?.Contains("Commits_ProjectId_CommitHash", StringComparison.OrdinalIgnoreCase) == true);
        }

        var inner = exception.InnerException;
        if (inner != null && (inner.GetType().FullName == "Microsoft.Data.Sqlite.SqliteException" || inner.GetType().Name == "SqliteException"))
        {
            var message = inner.Message;
            return message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) &&
                   ((message.Contains("Commits.ProjectId", StringComparison.OrdinalIgnoreCase) &&
                     message.Contains("Commits.CommitHash", StringComparison.OrdinalIgnoreCase)) ||
                    message.Contains("IX_Commits_ProjectId_CommitHash", StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }
}
