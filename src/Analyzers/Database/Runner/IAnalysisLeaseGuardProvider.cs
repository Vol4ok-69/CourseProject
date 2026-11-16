namespace DatabaseAnalyzer.Runner;

/// <summary>
/// Фабрика защитных областей владения задачей анализа на базе PostgreSQL advisory locks.
/// Наследует общий контракт из Application.Interfaces.
/// </summary>
public interface IAnalysisLeaseGuardProvider : Application.Interfaces.IAnalysisLeaseGuardProvider
{
    /// <summary>
    /// Захватывает эксклюзивную блокировку для указанного анализа и токена выполнения.
    /// </summary>
    new Task<IAnalysisLeaseGuard> AcquireGuardAsync(
        int analysisId,
        Guid executionToken,
        CancellationToken cancellationToken = default);

    async Task<Application.Interfaces.IAnalysisLeaseGuard> Application.Interfaces.IAnalysisLeaseGuardProvider.AcquireGuardAsync(
        int analysisId,
        Guid executionToken,
        CancellationToken cancellationToken)
    {
        return await AcquireGuardAsync(analysisId, executionToken, cancellationToken);
    }
}
