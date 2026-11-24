using System.Net;

namespace Application.Exceptions;

/// <summary>
/// Категория ограничения запросов GitHub API (Rate Limit).
/// </summary>
public enum GitHubRateLimitCategory
{
    None = 0,

    /// <summary>
    /// Исчерпана часовая квота запросов (Primary Rate Limit: x-ratelimit-remaining == 0).
    /// </summary>
    PrimaryQuotaExceeded = 1,

    /// <summary>
    /// Сработало вторичное ограничение по частоте или параллелизму (Secondary Rate Limit: burst/concurrency/abuse).
    /// </summary>
    SecondaryAbuseTriggered = 2
}

/// <summary>
/// Категория ошибки интеграции с внешним API GitHub.
/// </summary>
public enum GitHubErrorKind
{
    /// <summary>Некорректный формат URL репозитория.</summary>
    InvalidRepoUrl,

    /// <summary>Репозиторий не найден или является приватным.</summary>
    RepoNotFound,

    /// <summary>Превышен лимит запросов к GitHub API (Rate Limit).</summary>
    RateLimited,

    /// <summary>Доступ к репозиторию запрещен (SSO, ограничения организации).</summary>
    AccessDenied,

    /// <summary>Сервис GitHub недоступен, сбой сети или таймаут.</summary>
    ServiceUnavailable,

    /// <summary>Персональный токен GitHub недействителен или отозван.</summary>
    TokenExpired,

    /// <summary>У владельца проекта не настроен персональный токен доступа GitHub (PAT).</summary>
    TokenRequired
}

/// <summary>
/// Исключение, возникающее при ошибках взаимодействия с внешним сервисом GitHub.
/// </summary>
public class GitHubIntegrationException : Exception
{
    public GitHubErrorKind Kind { get; }

    public HttpStatusCode? StatusCode { get; init; }

    public GitHubRateLimitCategory RateLimitCategory { get; init; } = GitHubRateLimitCategory.None;

    public TimeSpan? RetryAfterDelay { get; init; }

    public DateTimeOffset? ResetTimeUtc { get; init; }

    public DateTimeOffset? ServerDateUtc { get; init; }

    public bool HasInvalidHeaders { get; init; }

    /// <summary>
    /// Безопасные диагностические заголовки ответа GitHub API (allow-list).
    /// Исключает любые токены, авторизацию и персональные данные.
    /// </summary>
    public IReadOnlyDictionary<string, string>? HeaderDiagnostics { get; init; }

    /// <summary>
    /// Признак того, что исключение вызвано ограничением запросов (Rate Limit).
    /// </summary>
    public bool IsRateLimit => Kind == GitHubErrorKind.RateLimited || RateLimitCategory != GitHubRateLimitCategory.None;

    public GitHubIntegrationException(GitHubErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public GitHubIntegrationException(string message, HttpStatusCode? statusCode = null, Exception? innerException = null)
        : this(GitHubErrorKind.ServiceUnavailable, message, innerException)
    {
        StatusCode = statusCode;
    }
}
