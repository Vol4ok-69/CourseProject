using Domain.Entities;

namespace Application.Interfaces.Repositories;

public interface IAnalysisRepository
{
    Task<Analysis?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Analysis>> GetByCommitIdAsync(int commitId, CancellationToken cancellationToken = default);

    Task AddAsync(Analysis analysis, CancellationToken cancellationToken = default);

    Task UpdateAsync(Analysis analysis, CancellationToken cancellationToken = default);

    /// <summary>
    /// Атомарно захватывает следующую ожидающую (Pending) или зависшую (Running с истекшей арендой) задачу.
    /// Переводит её в статус Running, фиксирует StartedAt, новый срок аренды и уникальный ExecutionToken.
    /// </summary>
    Task<Analysis?> TryAcquireNextPendingOrStaleAsync(
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Продлевает срок аренды выполняющейся задачи при условии совпадения ExecutionToken.
    /// </summary>
    Task<bool> RenewLeaseAsync(
        int analysisId,
        Guid executionToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Атомарно фиксирует успешное завершение анализа с сохранением результатов,
    /// только если текущий статус Running и совпадает ExecutionToken.
    /// </summary>
    Task<bool> CompleteAsync(
        int analysisId,
        Guid executionToken,
        IReadOnlyList<AnalysisResult> results,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Атомарно переводит анализ в статус Failed,
    /// только если текущий статус Running и совпадает ExecutionToken.
    /// </summary>
    Task<bool> FailAsync(
        int analysisId,
        Guid executionToken,
        CancellationToken cancellationToken = default);
}
