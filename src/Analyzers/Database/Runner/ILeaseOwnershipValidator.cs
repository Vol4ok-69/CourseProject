namespace DatabaseAnalyzer.Runner;

/// <summary>
/// Интерфейс валидации актуального владения задачей анализа (TD-003 lease check).
/// Защищает от гонок воркеров: проверяет, что задача не была перехвачена другим воркером
/// и её ExecutionToken в PostgreSQL по-прежнему совпадает с токеном текущего исполнителя.
/// </summary>
public interface ILeaseOwnershipValidator
{
    Task<bool> ValidateOwnershipAsync(int analysisId, Guid executionToken, CancellationToken cancellationToken = default);
}
