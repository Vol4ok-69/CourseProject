using Domain.Entities;

namespace Application.Interfaces.Repositories;

public interface IAnalysisRepository
{
    Task<Analysis?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<Analysis> Items, int TotalCount)> GetByCommitIdAsync(int commitId, int page, int pageSize, CancellationToken cancellationToken = default);

    Task AddAsync(Analysis analysis, CancellationToken cancellationToken = default);

    Task UpdateAsync(Analysis analysis, CancellationToken cancellationToken = default);

    /// <summary>
    /// Проверяет, существует ли активная задача анализа (со статусом Pending или Running) для указанного коммита.
    /// </summary>
    Task<bool> HasActiveAnalysisAsync(int commitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Открывает транзакционную область блокировки для конкурентобезопасной постановки анализа в очередь.
    /// В PostgreSQL использует advisory lock по идентификатору коммита.
    /// </summary>
    Task<IAnalysisRepositoryTransaction> BeginEnqueueTransactionAsync(int commitId, CancellationToken cancellationToken = default);

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
    /// только если текущий статус Running, совпадает ExecutionToken, аренда не истекла
    /// и удерживается соответствующий advisory lock guard.
    /// В PostgreSQL запись выполняется непосредственно через физическое соединение guard,
    /// удерживающее блокировку сессии, что гарантирует отсутствие TOCTOU.
    /// </summary>
    Task<bool> CompleteAsync(
        int analysisId,
        Guid executionToken,
        IReadOnlyList<AnalysisResult> results,
        IAnalysisLeaseGuard guard,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Атомарно переводит анализ в статус Failed с сохранением причины ошибки,
    /// только если текущий статус Running, совпадает ExecutionToken, аренда не истекла
    /// и удерживается соответствующий advisory lock guard.
    /// В PostgreSQL запись выполняется непосредственно через физическое соединение guard,
    /// удерживающее блокировку сессии, что гарантирует отсутствие TOCTOU.
    /// </summary>
    Task<bool> FailAsync(
        int analysisId,
        Guid executionToken,
        string? errorMessage,
        IAnalysisLeaseGuard guard,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Атомарно планирует повторную попытку выполнения анализа (перевод в Pending с отложенным NextAttemptAt),
    /// сбрасывая ExecutionToken и LeaseExpiration, только если текущий статус Running,
    /// совпадает ExecutionToken, аренда не истекла и удерживается соответствующий advisory lock guard.
    /// В PostgreSQL запись выполняется непосредственно через физическое соединение guard,
    /// удерживающее блокировку сессии, что гарантирует отсутствие TOCTOU.
    /// </summary>
    Task<bool> ScheduleRetryAsync(
        int analysisId,
        Guid executionToken,
        DateTime nextAttemptAt,
        string errorMessage,
        IAnalysisLeaseGuard guard,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Возвращает идентификатор серверного процесса PostgreSQL (backend PID) базового соединения репозитория, если применимо.
    /// </summary>
    int? GetConnectionProcessId() => null;

    /// <summary>
    /// Возвращает состояние задачи анализа вместе с backend PID соединения PostgreSQL,
    /// на котором был выполнен запрос, если используется провайдер Npgsql.
    /// </summary>
    /// <remarks>
    /// Для Npgsql значение ProcessId считывается из того же физического соединения,
    /// через которое загружается Analysis и связанные Results.
    /// Для других провайдеров ProcessId может быть null.
    /// Возвращаемое значение ProcessId предназначено для диагностики и проверки
    /// идентичности PostgreSQL backend, а не является самостоятельным подтверждением
    /// владения задачей.
    /// </remarks>
    /// <param name="id">Идентификатор анализа.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Состояние анализа и backend PID, если он доступен.</returns>
    Task<(Analysis? Analysis, int? ProcessId)> GetAnalysisWithProcessIdAsync(
        int id,
        CancellationToken cancellationToken = default);
}
