using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Application.Exceptions;

namespace Application.Services;

/// <summary>
/// Парсер заголовков ограничений частоты запросов GitHub REST API и RFC 9110.
/// Безопасно извлекает метаданные квот без утечки секретов и токенов.
/// </summary>
public static class GitHubRateLimitParser
{
    private static readonly HashSet<string> AllowedDiagnosticHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "retry-after",
        "x-ratelimit-limit",
        "x-ratelimit-remaining",
        "x-ratelimit-reset",
        "x-ratelimit-used",
        "x-ratelimit-resource",
        "date"
    };

    private static readonly string[] HttpDateFormats =
    [
        "r",                                    // RFC 1123 / IMF-fixdate: "Sun, 06 Nov 1994 08:49:37 GMT"
        "dddd, dd-MMM-yy HH:mm:ss 'GMT'",       // RFC 850: "Sunday, 06-Nov-94 08:49:37 GMT"
        "ddd MMM  d HH:mm:ss yyyy",             // ANSI C asctime (однозначный день): "Sun Nov  6 08:49:37 1994"
        "ddd MMM dd HH:mm:ss yyyy"              // ANSI C asctime (двузначный день): "Sun Nov 06 08:49:37 1994"
    ];

    /// <summary>
    /// Парсит заголовки HTTP-ответа и формирует GitHubIntegrationException с метаданными.
    /// </summary>
    public static GitHubIntegrationException ParseRateLimitException(
        HttpResponseMessage response,
        string responseBody,
        DateTimeOffset receptionTime,
        int consecutiveSecondaryViolations = 1)
    {
        var headers = response.Headers;
        var statusCode = response.StatusCode;

        var headerDiagnostics = ExtractSafeHeaderDiagnostics(headers);
        var hasInvalidHeaders = false;

        // 1. Парсинг даты сервера (Date)
        DateTimeOffset? serverDate = null;
        if (headers.Date.HasValue)
        {
            serverDate = headers.Date.Value.ToUniversalTime();
        }

        // 2. Парсинг первичного лимита: X-RateLimit-Remaining и X-RateLimit-Reset
        int? remainingQuota = null;
        if (TryGetSingleHeaderValue(headers, "X-RateLimit-Remaining", out var remainingStr))
        {
            if (int.TryParse(remainingStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rem) && rem >= 0)
            {
                remainingQuota = rem;
            }
            else
            {
                hasInvalidHeaders = true;
            }
        }

        DateTimeOffset? resetTimeUtc = null;
        if (TryGetSingleHeaderValue(headers, "X-RateLimit-Reset", out var resetStr))
        {
            if (long.TryParse(resetStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resetEpoch) && resetEpoch >= 0)
            {
                try
                {
                    resetTimeUtc = DateTimeOffset.FromUnixTimeSeconds(resetEpoch);
                }
                catch (ArgumentOutOfRangeException)
                {
                    hasInvalidHeaders = true;
                }
            }
            else
            {
                hasInvalidHeaders = true;
            }
        }

        // 3. Парсинг заголовка Retry-After (RFC 9110)
        TimeSpan? parsedRetryAfterDelay = null;
        var retryAfterHeaderPresent = TryGetSingleHeaderValue(headers, "Retry-After", out var retryAfterRaw);

        if (retryAfterHeaderPresent)
        {
            if (string.IsNullOrWhiteSpace(retryAfterRaw))
            {
                // Заголовок присутствует, но пустой -> нарушение синтаксиса RFC 9110
                hasInvalidHeaders = true;
            }
            else if (long.TryParse(retryAfterRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var deltaSeconds))
            {
                if (deltaSeconds >= 0)
                {
                    // RFC 9110 Section 10.2.3: delta-seconds отсчитывается с момента получения клиентом
                    parsedRetryAfterDelay = TimeSpan.FromSeconds(deltaSeconds);
                }
                else
                {
                    // Отрицательное число секунд недопустимо
                    hasInvalidHeaders = true;
                }
            }
            else if (TryParseHttpDate(retryAfterRaw, out var httpDateUtc))
            {
                var referenceTime = serverDate ?? receptionTime;
                var calculated = httpDateUtc - referenceTime;
                parsedRetryAfterDelay = calculated > TimeSpan.Zero ? calculated : TimeSpan.Zero;
            }
            else
            {
                // Нечисловой и не распознанный как HTTP-date заголовок
                hasInvalidHeaders = true;
            }
        }

        // 4. Определение категории ограничения
        var isPrimaryExhausted = remainingQuota.HasValue && remainingQuota.Value == 0;
        var isStatusCodeRateLimit = (int)statusCode == 429;
        var isBodyRateLimited = !string.IsNullOrEmpty(responseBody) &&
            (responseBody.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
             responseBody.Contains("API rate limit exceeded", StringComparison.OrdinalIgnoreCase) ||
             responseBody.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase));

        GitHubRateLimitCategory category;
        if (isPrimaryExhausted)
        {
            category = GitHubRateLimitCategory.PrimaryQuotaExceeded;
        }
        else if (isStatusCodeRateLimit || retryAfterHeaderPresent || isBodyRateLimited)
        {
            category = GitHubRateLimitCategory.SecondaryAbuseTriggered;
        }
        else
        {
            category = GitHubRateLimitCategory.None;
        }

        // 5. Расчёт задержки повтора
        TimeSpan? calculatedDelay = null;

        if (category == GitHubRateLimitCategory.PrimaryQuotaExceeded)
        {
            TimeSpan? primaryDelay = null;
            if (resetTimeUtc.HasValue)
            {
                var referenceTime = serverDate ?? receptionTime;
                var diff = resetTimeUtc.Value - referenceTime;
                primaryDelay = diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
            }

            // Коллизия: исчерпан Primary limit И сработал Secondary limit с валидным Retry-After
            if (parsedRetryAfterDelay.HasValue && primaryDelay.HasValue)
            {
                calculatedDelay = primaryDelay.Value >= parsedRetryAfterDelay.Value
                    ? primaryDelay.Value
                    : parsedRetryAfterDelay.Value;
            }
            else
            {
                calculatedDelay = primaryDelay;
            }
        }
        else if (category == GitHubRateLimitCategory.SecondaryAbuseTriggered)
        {
            if (parsedRetryAfterDelay.HasValue)
            {
                calculatedDelay = parsedRetryAfterDelay.Value;
            }
            else if (!retryAfterHeaderPresent)
            {
                // Заголовок действительно отсутствует при Secondary Limit -> GitHub-правило 60с с экспоненциальным ростом
                var multiplier = Math.Max(1, Math.Min(64, (int)Math.Pow(2, Math.Max(0, consecutiveSecondaryViolations - 1))));
                calculatedDelay = TimeSpan.FromSeconds(60 * multiplier);
            }
            else
            {
                // Заголовок присутствовал, но был невалиден (пуст или поврежден) -> задержка неопределима
                calculatedDelay = null;
            }
        }

        return new GitHubIntegrationException(
            GitHubErrorKind.RateLimited,
            BuildRateLimitMessage(category, calculatedDelay, hasInvalidHeaders))
        {
            StatusCode = statusCode,
            RateLimitCategory = category,
            RetryAfterDelay = calculatedDelay,
            ResetTimeUtc = resetTimeUtc,
            ServerDateUtc = serverDate,
            HasInvalidHeaders = hasInvalidHeaders,
            HeaderDiagnostics = headerDiagnostics
        };
    }

    private static bool TryParseHttpDate(string raw, out DateTimeOffset dateUtc)
    {
        dateUtc = default;
        var cleanRaw = raw.Trim();

        if (DateTimeOffset.TryParseExact(
            cleanRaw,
            HttpDateFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out dateUtc))
        {
            return true;
        }

        // Fallback на общий парсер дат .NET с инвариантной культурой
        if (DateTimeOffset.TryParse(
            cleanRaw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out dateUtc))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetSingleHeaderValue(HttpResponseHeaders headers, string headerName, out string? value)
    {
        value = null;
        if (!headers.TryGetValues(headerName, out var values))
        {
            return false;
        }

        value = string.Join(", ", values).Trim();
        return true;
    }

    private static IReadOnlyDictionary<string, string> ExtractSafeHeaderDiagnostics(HttpResponseHeaders headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in headers)
        {
            if (AllowedDiagnosticHeaders.Contains(header.Key))
            {
                result[header.Key] = string.Join(", ", header.Value);
            }
        }

        return result;
    }

    private static string BuildRateLimitMessage(GitHubRateLimitCategory category, TimeSpan? delay, bool hasInvalidHeaders)
    {
        var categoryName = category switch
        {
            GitHubRateLimitCategory.PrimaryQuotaExceeded => "Первичное ограничение квоты (Primary Rate Limit)",
            GitHubRateLimitCategory.SecondaryAbuseTriggered => "Вторичное ограничение частоты (Secondary Rate Limit)",
            _ => "Ограничение частоты запросов GitHub API"
        };

        var delayText = delay.HasValue
            ? $"Рекомендуемое время ожидания: {(int)Math.Ceiling(delay.Value.TotalSeconds)} сек."
            : "Время сброса ограничения не указано или заголовок повреждён.";

        var invalidText = hasInvalidHeaders ? " (Обнаружены повреждённые заголовки ответа)." : string.Empty;

        return $"{categoryName}. {delayText}{invalidText}";
    }
}
