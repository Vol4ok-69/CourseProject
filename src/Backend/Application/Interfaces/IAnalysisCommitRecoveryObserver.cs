using Domain.Enums;

namespace Application.Interfaces;

/// <summary>
/// Наблюдатель диагностических событий для проверки восстановления при потере подтверждения COMMIT (Q-04).
/// </summary>
public interface IAnalysisCommitRecoveryObserver
{
    /// <summary>
    /// Вызывается перед началом вызова CompleteAsync.
    /// </summary>
    void OnCommitStarted(int analysisId, Guid executionToken);

    /// <summary>
    /// Вызывается при возникновении исключения во время CompleteAsync.
    /// </summary>
    void OnCommitFailed(int analysisId, Guid executionToken, Exception exception);

    /// <summary>
    /// Вызывается перед началом выполнения независимого запроса состояния задачи.
    /// </summary>
    void OnRecoveryStarted(int analysisId, Guid executionToken);

    /// <summary>
    /// Вызывается после получения фактического состояния задачи из независимого соединения.
    /// Явно разделяет идентификаторы серверных процессов: соединение guard (удерживающее session lock)
    /// и независимое соединение recovery.
    /// </summary>
    void OnRecoveryStateInspected(
        int analysisId,
        Guid executionToken,
        AnalysisStatus? status,
        Guid? token,
        int? guardProcessId,
        int? recoveryProcessId);

    /// <summary>
    /// Для обратной совместимости вызовов с единым PID.
    /// </summary>
    void OnRecoveryStateInspected(
        int analysisId,
        Guid executionToken,
        AnalysisStatus? status,
        Guid? token,
        int? connectionProcessId)
        => OnRecoveryStateInspected(analysisId, executionToken, status, token, null, connectionProcessId);

    /// <summary>
    /// Вызывается при завершении ветки восстановления с результатом успеха или неудачи.
    /// </summary>
    void OnRecoveryCompleted(int analysisId, Guid executionToken, bool success);
}
