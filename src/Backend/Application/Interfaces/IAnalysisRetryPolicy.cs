namespace Application.Interfaces;

/// <summary>
/// Результат оценки возможности повторного выполнения упавшей задачи.
/// </summary>
/// <param name="ShouldRetry">Флаг необходимости повторной попытки.</param>
/// <param name="Delay">Задержка перед следующей попыткой (null, если повтор не требуется).</param>
/// <param name="Reason">Причина решения (для логирования и сохранения в ошибку анализа).</param>
/// <param name="IsPoisonPill">Признак перевода задачи в статус сбоя из-за исчерпания попыток (Poison Pill).</param>
public sealed record AnalysisRetryDecision(
    bool ShouldRetry,
    TimeSpan? Delay,
    string Reason,
    bool IsPoisonPill);

/// <summary>
/// Политика классификации ошибок анализа и расчёта задержки повтора (Exponential Backoff, Rate Limits, Poison Pill).
/// </summary>
public interface IAnalysisRetryPolicy
{
    /// <summary>
    /// Максимальное допустимое время ожидания сброса Rate Limit (по умолчанию 15 минут).
    /// </summary>
    TimeSpan MaxRateLimitWait { get; }

    /// <summary>
    /// Оценивает исключение и определяет, следует ли повторить задачу, либо признать фатальный сбой / Poison Pill.
    /// </summary>
    /// <param name="exception">Возникшее исключение.</param>
    /// <param name="currentAttemptCount">Текущее количество совершённых попыток.</param>
    /// <param name="maxAttempts">Максимально допустимое количество попыток.</param>
    /// <param name="utcNow">Текущее UTC время (опционально, для детерминированного тестирования).</param>
    /// <returns>Решение о повторе выполнения задачи.</returns>
    AnalysisRetryDecision Evaluate(Exception exception, int currentAttemptCount, int maxAttempts, DateTime? utcNow = null);
}
