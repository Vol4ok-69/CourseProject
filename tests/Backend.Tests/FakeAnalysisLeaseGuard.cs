using Application.Interfaces;

namespace Backend.Tests;

public sealed class FakeAnalysisLeaseGuard : IAnalysisLeaseGuard
{
    public FakeAnalysisLeaseGuard(
        int analysisId = 1,
        Guid? executionToken = null,
        bool isAcquired = true)
    {
        AnalysisId = analysisId;
        ExecutionToken = executionToken ?? Guid.NewGuid();
        IsAcquired = isAcquired;
    }

    public int AnalysisId { get; }

    public Guid ExecutionToken { get; }

    public bool IsAcquired { get; set; }

    public bool RequiresSessionAdvisoryLock => false;

    public System.Data.Common.DbConnection? Connection => null;

    public int? ConnectionProcessId => null;

    public Task<bool> ValidateOwnershipAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(IsAcquired);

    public Task<bool> ValidateOwnershipAsync(int[] allowedStatuses, CancellationToken cancellationToken = default)
        => Task.FromResult(IsAcquired);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
