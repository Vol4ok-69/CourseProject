using Application.DTOs;
using Application.Exceptions;
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

        commit.CommitHash = NormalizeHash(commit.CommitHash);
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

    public async Task<IReadOnlyDictionary<string, bool>> GetHashesVerificationStatusAsync(
        int projectId,
        IReadOnlyCollection<string> commitHashes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commitHashes);

        if (commitHashes.Count == 0)
        {
            return new Dictionary<string, bool>();
        }

        var distinctHashes = commitHashes.Distinct().ToList();

        var rows = await context.Commits
            .AsNoTracking()
            .Where(commit => commit.ProjectId == projectId && distinctHashes.Contains(commit.CommitHash))
            .Select(commit => new { commit.CommitHash, commit.IsVerified })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.CommitHash, r => r.IsVerified);
    }

    public async Task<SyncBatchResult> SaveSyncBatchAsync(
        int projectId,
        IReadOnlyList<Commit> newCommits,
        IReadOnlyList<CommitUpdateDto> existingToUpdate,
        ProjectCheckpointDto checkpoint,
        ProjectCheckpointSnapshotDto expectedCheckpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newCommits);
        ArgumentNullException.ThrowIfNull(existingToUpdate);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(expectedCheckpoint);

        cancellationToken.ThrowIfCancellationRequested();

        var createdTransaction = false;
        var transaction = context.Database.CurrentTransaction;
        if (transaction == null)
        {
            transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            createdTransaction = true;
        }

        try
        {
            // 1. Блокировка строки проекта в PostgreSQL (если провайдер Npgsql)
            if (context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT \"Id\" FROM \"Projects\" WHERE \"Id\" = {projectId} FOR NO KEY UPDATE",
                    cancellationToken);
            }

            var project = await context.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
                ?? throw new InvalidOperationException($"Проект с идентификатором {projectId} не найден.");

            // 2. Проверка, что checkpoint не изменился за время сетевого обхода
            if (project.LastSyncedCommitHash != expectedCheckpoint.LastSyncedCommitHash ||
                project.LastSyncedRepositoryHost != expectedCheckpoint.LastSyncedRepositoryHost ||
                project.LastSyncedRepositoryId != expectedCheckpoint.LastSyncedRepositoryId ||
                project.LastSyncedBranch != expectedCheckpoint.LastSyncedBranch)
            {
                throw new SyncCheckpointChangedException("Контрольная точка проекта была изменена другим процессом во время синхронизации.");
            }

            // 3. Собираем все хэши, которые мы хотим вставить или обновить
            var hashesToLookup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in newCommits)
            {
                c.CommitHash = NormalizeHash(c.CommitHash);
                hashesToLookup.Add(c.CommitHash);
            }

            var normalizedUpdates = new List<CommitUpdateDto>(existingToUpdate.Count);
            foreach (var u in existingToUpdate)
            {
                var normalizedUpdate = u with { CommitHash = NormalizeHash(u.CommitHash) };
                normalizedUpdates.Add(normalizedUpdate);
                hashesToLookup.Add(normalizedUpdate.CommitHash);
            }

            var existingRows = await context.Commits
                .Where(c => c.ProjectId == projectId && hashesToLookup.Contains(c.CommitHash))
                .ToListAsync(cancellationToken);

            var existingMap = existingRows.ToDictionary(c => c.CommitHash, StringComparer.OrdinalIgnoreCase);

            var insertedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var updatedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unchangedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Обрабатываем новые коммиты
            foreach (var commit in newCommits)
            {
                if (existingMap.TryGetValue(commit.CommitHash, out var existingRow))
                {
                    if (existingRow.IsVerified)
                    {
                        unchangedHashes.Add(commit.CommitHash);
                    }
                    else
                    {
                        existingRow.ParentHashes = commit.ParentHashes;
                        existingRow.IsVerified = true;
                        updatedHashes.Add(commit.CommitHash);
                    }
                }
                else
                {
                    commit.ProjectId = projectId;
                    commit.IsVerified = true;
                    await context.Commits.AddAsync(commit, cancellationToken);
                    insertedHashes.Add(commit.CommitHash);
                }
            }

            // Обрабатываем существующие коммиты, требовавшие обновления
            foreach (var update in normalizedUpdates)
            {
                if (insertedHashes.Contains(update.CommitHash) || updatedHashes.Contains(update.CommitHash) || unchangedHashes.Contains(update.CommitHash))
                {
                    continue;
                }

                if (existingMap.TryGetValue(update.CommitHash, out var existingRow))
                {
                    if (existingRow.IsVerified)
                    {
                        unchangedHashes.Add(update.CommitHash);
                    }
                    else
                    {
                        existingRow.ParentHashes = update.ParentHashes.ToArray();
                        existingRow.IsVerified = true;
                        updatedHashes.Add(update.CommitHash);
                    }
                }
                else
                {
                    var newCommit = new Commit
                    {
                        ProjectId = projectId,
                        CommitHash = NormalizeHash(update.CommitHash),
                        ParentHashes = update.ParentHashes.ToArray(),
                        IsVerified = true,
                        CommitDate = DateTime.UtcNow
                    };
                    await context.Commits.AddAsync(newCommit, cancellationToken);
                    insertedHashes.Add(update.CommitHash);
                }
            }

            // 4. Обновляем checkpoint проекта
            project.LastSyncedCommitHash = checkpoint.LastSyncedCommitHash;
            project.LastSyncedBranch = checkpoint.LastSyncedBranch;
            project.LastSyncedRepositoryId = checkpoint.LastSyncedRepositoryId;
            project.LastSyncedRepositoryHost = checkpoint.LastSyncedRepositoryHost;
            project.LastSyncedAt = checkpoint.LastSyncedAt;

            // 5. Сохраняем изменения с поддержкой точек сохранения при параллельных 23505
            const string batchSavepoint = "BeforeSyncBatchSave";
            var supportsSavepoints = transaction.SupportsSavepoints;
            if (supportsSavepoints)
            {
                await transaction.CreateSavepointAsync(batchSavepoint, cancellationToken);
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (!cancellationToken.IsCancellationRequested && IsCommitUniqueConstraintViolation(ex))
            {
                if (supportsSavepoints)
                {
                    await transaction.RollbackToSavepointAsync(batchSavepoint, cancellationToken);
                }

                // Отсоединяем добавленные сущности коммитов из ChangeTracker, чтобы очистить контекст
                foreach (var entry in context.ChangeTracker.Entries<Commit>().ToList())
                {
                    entry.State = EntityState.Detached;
                }

                // Повторно запрашиваем актуальное состояние коммитов из БД
                var reloadedRows = await context.Commits
                    .Where(c => c.ProjectId == projectId && hashesToLookup.Contains(c.CommitHash))
                    .ToListAsync(cancellationToken);

                var reloadedMap = reloadedRows.ToDictionary(c => c.CommitHash, StringComparer.OrdinalIgnoreCase);

                insertedHashes.Clear();
                updatedHashes.Clear();
                unchangedHashes.Clear();

                foreach (var commit in newCommits)
                {
                    if (reloadedMap.TryGetValue(commit.CommitHash, out var existingRow))
                    {
                        if (existingRow.IsVerified)
                        {
                            unchangedHashes.Add(commit.CommitHash);
                        }
                        else
                        {
                            existingRow.ParentHashes = commit.ParentHashes;
                            existingRow.IsVerified = true;
                            updatedHashes.Add(commit.CommitHash);
                        }
                    }
                    else
                    {
                        commit.ProjectId = projectId;
                        commit.IsVerified = true;
                        await context.Commits.AddAsync(commit, cancellationToken);
                        insertedHashes.Add(commit.CommitHash);
                    }
                }

                foreach (var update in normalizedUpdates)
                {
                    if (insertedHashes.Contains(update.CommitHash) || updatedHashes.Contains(update.CommitHash) || unchangedHashes.Contains(update.CommitHash))
                    {
                        continue;
                    }

                    if (reloadedMap.TryGetValue(update.CommitHash, out var existingRow))
                    {
                        if (existingRow.IsVerified)
                        {
                            unchangedHashes.Add(update.CommitHash);
                        }
                        else
                        {
                            existingRow.ParentHashes = update.ParentHashes.ToArray();
                            existingRow.IsVerified = true;
                            updatedHashes.Add(update.CommitHash);
                        }
                    }
                    else
                    {
                        var newCommit = new Commit
                        {
                            ProjectId = projectId,
                            CommitHash = NormalizeHash(update.CommitHash),
                            ParentHashes = update.ParentHashes.ToArray(),
                            IsVerified = true,
                            CommitDate = DateTime.UtcNow
                        };
                        await context.Commits.AddAsync(newCommit, cancellationToken);
                        insertedHashes.Add(update.CommitHash);
                    }
                }

                var trackedProject = context.ChangeTracker.Entries<Project>().FirstOrDefault(e => e.Entity.Id == projectId)?.Entity;
                if (trackedProject != null)
                {
                    trackedProject.LastSyncedCommitHash = checkpoint.LastSyncedCommitHash;
                    trackedProject.LastSyncedBranch = checkpoint.LastSyncedBranch;
                    trackedProject.LastSyncedRepositoryId = checkpoint.LastSyncedRepositoryId;
                    trackedProject.LastSyncedRepositoryHost = checkpoint.LastSyncedRepositoryHost;
                    trackedProject.LastSyncedAt = checkpoint.LastSyncedAt;
                }
                else
                {
                    project.LastSyncedCommitHash = checkpoint.LastSyncedCommitHash;
                    project.LastSyncedBranch = checkpoint.LastSyncedBranch;
                    project.LastSyncedRepositoryId = checkpoint.LastSyncedRepositoryId;
                    project.LastSyncedRepositoryHost = checkpoint.LastSyncedRepositoryHost;
                    project.LastSyncedAt = checkpoint.LastSyncedAt;
                    context.Projects.Update(project);
                }

                await context.SaveChangesAsync(cancellationToken);
            }

            if (createdTransaction)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return new SyncBatchResult(insertedHashes, updatedHashes, unchangedHashes);
        }
        catch
        {
            if (createdTransaction)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            throw;
        }
        finally
        {
            if (createdTransaction)
            {
                await transaction.DisposeAsync();
            }
        }
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

        foreach (var commit in commits)
        {
            commit.CommitHash = NormalizeHash(commit.CommitHash);
        }

        try
        {
            await context.Commits.AddRangeAsync(commits, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            return commits.Count;
        }
        catch (DbUpdateException exception) when (!cancellationToken.IsCancellationRequested && IsCommitUniqueConstraintViolation(exception))
        {
            foreach (var commit in commits)
            {
                var entry = context.Entry(commit);
                if (entry.State != EntityState.Detached)
                {
                    entry.State = EntityState.Detached;
                }
            }

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
                   (string.IsNullOrEmpty(postgresException.ConstraintName) ||
                    postgresException.ConstraintName.Contains("Commits", StringComparison.OrdinalIgnoreCase) ||
                    postgresException.ConstraintName.Contains("CommitHash", StringComparison.OrdinalIgnoreCase));
        }

        var inner = exception.InnerException;
        if (inner != null && (inner.GetType().FullName == "Microsoft.Data.Sqlite.SqliteException" || inner.GetType().Name == "SqliteException"))
        {
            var message = inner.Message;
            return message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) &&
                   (message.Contains("Commits", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("CommitHash", StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static string NormalizeHash(string hash) => hash.ToLowerInvariant();
}
