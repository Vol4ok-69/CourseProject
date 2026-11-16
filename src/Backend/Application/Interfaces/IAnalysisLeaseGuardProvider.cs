namespace Application.Interfaces;

/// <summary>
/// Фабрика защитных областей владения задачей анализа на базе PostgreSQL advisory locks (TD-003).
/// </summary>
public interface IAnalysisLeaseGuardProvider
{
    /// <summary>
    /// Захватывает эксклюзивную блокировку для указанного анализа и токена выполнения.
    /// </summary>
    Task<IAnalysisLeaseGuard> AcquireGuardAsync(
        int analysisId,
        Guid executionToken,
        CancellationToken cancellationToken = default);
}
