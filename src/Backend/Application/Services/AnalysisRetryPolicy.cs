using System.Data.Common;
using System.Net;
using Application.Exceptions;
using Application.Interfaces;

namespace Application.Services;

/// <summary>
/// Реализация политики классификации ошибок анализа и расчёта задержки повтора.
/// Поддерживает RFC 9110, GitHub Rate Limit, Transient Network / SQL ошибки и Poison Pill.
/// </summary>
public sealed class AnalysisRetryPolicy : IAnalysisRetryPolicy
{
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly TimeSpan _maxRateLimitWait;
    private readonly Func<TimeSpan, TimeSpan>? _jitterProvider;

    private static readonly HashSet<string> TransientPostgresSqlStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "08000", // connection_exception
        "08003", // connection_does_not_exist
        "08006", // connection_failure
        "57P01", // admin_shutdown
        "40001", // serialization_failure
        "40P01", // deadlock_detected
    };

    public TimeSpan MaxRateLimitWait => _maxRateLimitWait;

    public AnalysisRetryPolicy(
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        TimeSpan? maxRateLimitWait = null,
        Func<TimeSpan, TimeSpan>? jitterProvider = null)
    {
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(10);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(300);
        _maxRateLimitWait = maxRateLimitWait ?? TimeSpan.FromMinutes(15);
        _jitterProvider = jitterProvider;
    }

    public AnalysisRetryDecision Evaluate(
        Exception exception,
        int currentAttemptCount,
        int maxAttempts,
        DateTime? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Разворачиваем AggregateException, если внутри одно исключение
        if (exception is AggregateException agg && agg.InnerExceptions.Count == 1)
        {
            exception = agg.InnerExceptions[0];
        }

        // 1. Проверка лимита попыток (Poison Pill)
        if (currentAttemptCount >= maxAttempts)
        {
            return new AnalysisRetryDecision(
                ShouldRetry: false,
                Delay: null,
                Reason: $"Достигнут лимит попыток выполнения ({maxAttempts}). Задача переведена в статус сбоя (Poison Pill).",
                IsPoisonPill: true);
        }

        // 2. Специфические исключения интеграции с GitHub
        if (exception is GitHubIntegrationException ghEx)
        {
            return EvaluateGitHubException(ghEx, currentAttemptCount);
        }

        // 3. Непоправимые ограничения архива и репозитория
        if (exception is ArchiveLimitExceededException)
        {
            return new AnalysisRetryDecision(
                ShouldRetry: false,
                Delay: null,
                Reason: $"Превышен лимит размера или количества файлов архива: {exception.Message}",
                IsPoisonPill: false);
        }

        // 4. Логические ошибки приложения (неповторимые)
        if (exception is ArgumentException or NotSupportedException)
        {
            return new AnalysisRetryDecision(
                ShouldRetry: false,
                Delay: null,
                Reason: $"Неповторимая ошибка аргументов: {exception.Message}",
                IsPoisonPill: false);
        }

        // 5. Ошибки HTTP (проверка 4xx vs 5xx / Network)
        if (exception is HttpRequestException httpEx)
        {
            if (httpEx.StatusCode.HasValue)
            {
                var statusCode = (int)httpEx.StatusCode.Value;
                if (statusCode >= 400 && statusCode < 500 && statusCode != (int)HttpStatusCode.RequestTimeout && statusCode != 429)
                {
                    return new AnalysisRetryDecision(
                        ShouldRetry: false,
                        Delay: null,
                        Reason: $"Неповторимая ошибка HTTP {statusCode}: {httpEx.Message}",
                        IsPoisonPill: false);
                }
            }
        }

        // 6. Ошибки базы данных (проверка транзиентных SQLSTATE)
        if (exception is DbException dbEx)
        {
            var sqlState = dbEx.SqlState ?? (exception.GetType().GetProperty("SqlState")?.GetValue(exception) as string);
            if (!string.IsNullOrEmpty(sqlState) && !TransientPostgresSqlStates.Contains(sqlState))
            {
                return new AnalysisRetryDecision(
                    ShouldRetry: false,
                    Delay: null,
                    Reason: $"Фатальная ошибка базы данных (SQLSTATE {sqlState}): {dbEx.Message}",
                    IsPoisonPill: false);
            }
        }

        // 7. Транзиентные сбои (Network, NpgsqlException, Timeout, 5xx HTTP и др.)
        var backoffDelay = CalculateExponentialBackoff(currentAttemptCount);
        return new AnalysisRetryDecision(
            ShouldRetry: true,
            Delay: backoffDelay,
            Reason: $"Временный сбой ({exception.GetType().Name}): {exception.Message}",
            IsPoisonPill: false);
    }

    private AnalysisRetryDecision EvaluateGitHubException(GitHubIntegrationException ghEx, int currentAttemptCount)
    {
        if (ghEx.Kind is GitHubErrorKind.InvalidRepoUrl
                      or GitHubErrorKind.RepoNotFound
                      or GitHubErrorKind.AccessDenied
                      or GitHubErrorKind.TokenExpired
                      or GitHubErrorKind.TokenRequired)
        {
            return new AnalysisRetryDecision(
                ShouldRetry: false,
                Delay: null,
                Reason: $"Неповторимая ошибка интеграции с GitHub ({ghEx.Kind}): {ghEx.Message}",
                IsPoisonPill: false);
        }

        if (ghEx.IsRateLimit)
        {
            if (ghEx.HasInvalidHeaders)
            {
                return new AnalysisRetryDecision(
                    ShouldRetry: false,
                    Delay: null,
                    Reason: "Обнаружены некорректные или повреждённые заголовки Rate Limit (RFC 9110 / GitHub Policy).",
                    IsPoisonPill: false);
            }

            TimeSpan delay;
            if (ghEx.RetryAfterDelay.HasValue)
            {
                delay = ghEx.RetryAfterDelay.Value;
            }
            else
            {
                // Secondary Rate Limit без Retry-After: fallback 60с * 2^(n-1)
                var multiplier = Math.Pow(2, Math.Max(0, currentAttemptCount - 1));
                delay = TimeSpan.FromSeconds(60 * multiplier);
            }

            if (delay > _maxRateLimitWait)
            {
                return new AnalysisRetryDecision(
                    ShouldRetry: false,
                    Delay: null,
                    Reason: $"Время ожидания сброса Rate Limit ({delay.TotalMinutes:F1} мин.) превышает допустимый максимум ({_maxRateLimitWait.TotalMinutes:F1} мин.).",
                    IsPoisonPill: false);
            }

            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            var adjustedDelay = ApplyJitter(delay);
            return new AnalysisRetryDecision(
                ShouldRetry: true,
                Delay: adjustedDelay,
                Reason: $"Превышен лимит запросов GitHub API. Ожидание {adjustedDelay.TotalSeconds:F0} сек.",
                IsPoisonPill: false);
        }

        if (ghEx.StatusCode.HasValue)
        {
            var code = (int)ghEx.StatusCode.Value;
            if (code is 400 or 401 or 403 or 404)
            {
                return new AnalysisRetryDecision(
                    ShouldRetry: false,
                    Delay: null,
                    Reason: $"Неповторимая ошибка интеграции с GitHub (HTTP {code}): {ghEx.Message}",
                    IsPoisonPill: false);
            }
        }

        // Иные ошибки GitHub (5xx, сетевые)
        var fallbackDelay = CalculateExponentialBackoff(currentAttemptCount);
        return new AnalysisRetryDecision(
            ShouldRetry: true,
            Delay: fallbackDelay,
            Reason: $"Временный сбой GitHub API (HTTP {ghEx.StatusCode}): {ghEx.Message}",
            IsPoisonPill: false);
    }

    private TimeSpan CalculateExponentialBackoff(int attemptCount)
    {
        // 2^(attempt-1)
        var power = Math.Max(0, attemptCount - 1);
        var factor = Math.Pow(2, power);
        var rawMilliseconds = _baseDelay.TotalMilliseconds * factor;

        var cappedDelay = TimeSpan.FromMilliseconds(Math.Min(rawMilliseconds, _maxDelay.TotalMilliseconds));
        return ApplyJitter(cappedDelay);
    }

    private TimeSpan ApplyJitter(TimeSpan delay)
    {
        if (_jitterProvider != null)
        {
            return _jitterProvider(delay);
        }

        // По умолчанию: детерминированный минимальный jitter до 1000мс
        var jitterMs = Random.Shared.Next(100, 1000);
        return delay.Add(TimeSpan.FromMilliseconds(jitterMs));
    }
}
