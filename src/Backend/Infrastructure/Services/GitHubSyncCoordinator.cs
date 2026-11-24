using Application.Interfaces;
using System.Collections.Concurrent;

namespace Infrastructure.Services;

/// <summary>
/// Потокобезопасная реализация координатора синхронизации проектов в памяти процесса.
/// </summary>
public sealed class GitHubSyncCoordinator : IGitHubSyncCoordinator
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    public IDisposable? TryAcquire(int projectId)
    {
        var semaphore = _locks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        if (!semaphore.Wait(0))
        {
            return null;
        }

        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
