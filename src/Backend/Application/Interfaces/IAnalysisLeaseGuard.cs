namespace Application.Interfaces;

/// <summary>
/// Представляет активную защитную область владения задачей анализа и её рабочим каталогом.
/// В PostgreSQL удерживает эксклюзивную session-level advisory lock на выделенном физическом соединении (TD-003).
/// </summary>
public interface IAnalysisLeaseGuard : IAsyncDisposable
{
    /// <summary>
    /// Идентификатор защищаемого анализа.
    /// </summary>
    int AnalysisId { get; }

    /// <summary>
    /// Токен выполнения, для которого захвачено владение.
    /// </summary>
    Guid ExecutionToken { get; }

    /// <summary>
    /// Признак успешного захвата эксклюзивной блокировки.
    /// </summary>
    bool IsAcquired { get; }

    /// <summary>
    /// Проверяет, что физическое соединение с PostgreSQL активно, сессия не разорвана,
    /// блокировка по-прежнему принадлежит текущей сессии и ExecutionToken соответствует текущему в базе данных.
    /// Выполняет реальный запрос к PostgreSQL через физическое соединение.
    /// </summary>
    Task<bool> ValidateOwnershipAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Проверяет владение задачей с явным списком допустимых статусов (например, Running=1, Completed=2, Failed=3).
    /// По умолчанию делегирует базовой проверке ValidateOwnershipAsync.
    /// </summary>
    Task<bool> ValidateOwnershipAsync(int[] allowedStatuses, CancellationToken cancellationToken = default)
        => ValidateOwnershipAsync(cancellationToken);
}
