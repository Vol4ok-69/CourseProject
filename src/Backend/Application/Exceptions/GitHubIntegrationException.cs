namespace Application.Exceptions;

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

    public GitHubIntegrationException(GitHubErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }
}
