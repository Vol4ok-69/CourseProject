using System.Collections.Concurrent;

namespace DatabaseAnalyzer.Runner;

/// <summary>
/// Реализация поставщика lease guard для тестовых сред и сценариев без PostgreSQL (TD-003).
/// Обеспечивает потокобезопасную взаимную блокировку по AnalysisId через SemaphoreSlim.
/// </summary>
public sealed class InMemoryAnalysisLeaseGuardProvider : IAnalysisLeaseGuardProvider
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();
    private readonly Func<int, Guid, Task<bool>>? _ownershipValidator;

    public InMemoryAnalysisLeaseGuardProvider(Func<int, Guid, Task<bool>>? ownershipValidator = null)
    {
        _ownershipValidator = ownershipValidator;
    }

    /// <inheritdoc />
    public async Task<IAnalysisLeaseGuard> AcquireGuardAsync(
        int analysisId,
        Guid executionToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(analysisId);
        if (executionToken == Guid.Empty)
        {
            throw new ArgumentException("Не указан токен выполнения анализа.", nameof(executionToken));
        }

        var semaphore = _locks.GetOrAdd(analysisId, _ => new SemaphoreSlim(1, 1));
        var acquired = await semaphore.WaitAsync(TimeSpan.Zero, cancellationToken);
        if (!acquired)
        {
            return new InMemoryAnalysisLeaseGuard(analysisId, executionToken, isAcquired: false, null, null);
        }

        return new InMemoryAnalysisLeaseGuard(
            analysisId,
            executionToken,
            isAcquired: true,
            semaphore,
            _ownershipValidator);
    }

    private sealed class InMemoryAnalysisLeaseGuard : IAnalysisLeaseGuard
    {
        private readonly SemaphoreSlim? _semaphore;
        private readonly Func<int, Guid, Task<bool>>? _ownershipValidator;
        private int _disposed;

        public InMemoryAnalysisLeaseGuard(
            int analysisId,
            Guid executionToken,
            bool isAcquired,
            SemaphoreSlim? semaphore,
            Func<int, Guid, Task<bool>>? ownershipValidator)
        {
            AnalysisId = analysisId;
            ExecutionToken = executionToken;
            IsAcquired = isAcquired;
            _semaphore = semaphore;
            _ownershipValidator = ownershipValidator;
        }

        public int AnalysisId { get; }
        public Guid ExecutionToken { get; }
        public bool IsAcquired { get; }

        public async Task<bool> ValidateOwnershipAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed != 0 || !IsAcquired)
            {
                return false;
            }

            if (_ownershipValidator != null)
            {
                return await _ownershipValidator(AnalysisId, ExecutionToken);
            }

            return true;
        }

        public Task<bool> ValidateOwnershipAsync(int[] allowedStatuses, CancellationToken cancellationToken = default)
            => ValidateOwnershipAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                if (IsAcquired && _semaphore != null)
                {
                    _semaphore.Release();
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
