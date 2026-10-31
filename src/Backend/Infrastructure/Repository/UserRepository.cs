using System.Data;
using Application.Interfaces.Repositories;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Infrastructure.Repository;

public class UserRepository(AppDbContext context) : IUserRepository
{
    private static readonly SemaphoreSlim _adminLock = new(1, 1);

    public async Task<User?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await context.Users
            .Include(user => user.Role)
            .FirstOrDefaultAsync(user => user.Id == id, cancellationToken);
    }

    public async Task<User?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        return await context.Users
            .Include(user => user.Role)
            .FirstOrDefaultAsync(user => user.Username == username, cancellationToken);
    }

    public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await context.Users
            .AsNoTracking()
            .Include(user => user.Role)
            .OrderBy(user => user.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        await context.Users.AddAsync(user, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(User user, CancellationToken cancellationToken = default)
    {
        context.Users.Update(user);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(User user, CancellationToken cancellationToken = default)
    {
        context.Users.Remove(user);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> HasProjectsAsync(int userId, CancellationToken cancellationToken = default)
    {
        return await context.Projects.AnyAsync(project => project.OwnerId == userId, cancellationToken);
    }

    public async Task<int> CountAdminsAsync(CancellationToken cancellationToken = default)
    {
        return await context.Users
            .CountAsync(user => user.Role.Name == "Admin", cancellationToken);
    }

    public async Task<IUserRepositoryTransaction> BeginAdminProtectionTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (context.Database.IsNpgsql())
        {
            var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            // Консультативная транзакционная блокировка PostgreSQL:
            // сериализует операции удаления и понижения роли администраторов, предотвращая состояние гонки.
            // Блокировка автоматически освобождается при COMMIT или ROLLBACK транзакции.
            await context.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(hashtext('admin_role_protection'));",
                cancellationToken);

            return new NpgsqlUserRepositoryTransaction(transaction, context);
        }
        else
        {
            // Потокобезопасная синхронизация для тестовых сред (SQLite / InMemory)
            await _adminLock.WaitAsync(cancellationToken);

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
                // Поставщики без поддержки реляционных транзакций (InMemory)
            }

            return new InMemoryUserRepositoryTransaction(_adminLock, transaction, context);
        }
    }

    private sealed class NpgsqlUserRepositoryTransaction(IDbContextTransaction transaction, AppDbContext context) : IUserRepositoryTransaction
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

    private sealed class InMemoryUserRepositoryTransaction(SemaphoreSlim semaphore, IDbContextTransaction? transaction, AppDbContext context) : IUserRepositoryTransaction
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
