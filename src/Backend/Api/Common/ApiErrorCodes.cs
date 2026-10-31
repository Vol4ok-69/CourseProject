namespace Api.Common;

/// <summary>
/// Содержит стабильные машиночитаемые коды ошибок API в формате SCREAMING_SNAKE_CASE.
/// </summary>
public static class ApiErrorCodes
{
    /// <summary>Ошибка валидации входных данных.</summary>
    public const string ValidationFailed = "VALIDATION_FAILED";

    /// <summary>Некорректный URL репозитория GitHub.</summary>
    public const string InvalidRepoUrl = "INVALID_REPO_URL";

    /// <summary>Превышен лимит размера ZIP-архива репозитория.</summary>
    public const string ArchiveLimitExceeded = "ARCHIVE_LIMIT_EXCEEDED";

    /// <summary>Неверные учетные данные пользователя.</summary>
    public const string InvalidCredentials = "INVALID_CREDENTIALS";

    /// <summary>Неверный текущий пароль пользователя.</summary>
    public const string InvalidPassword = "INVALID_PASSWORD";

    /// <summary>Пользователь не аутентифицирован или токен недействителен.</summary>
    public const string Unauthorized = "UNAUTHORIZED";

    /// <summary>Доступ к запрашиваемому ресурсу запрещен.</summary>
    public const string Forbidden = "FORBIDDEN";

    /// <summary>Пользователь не найден.</summary>
    public const string UserNotFound = "USER_NOT_FOUND";

    /// <summary>Проект не найден.</summary>
    public const string ProjectNotFound = "PROJECT_NOT_FOUND";

    /// <summary>Коммит не найден.</summary>
    public const string CommitNotFound = "COMMIT_NOT_FOUND";

    /// <summary>Анализ не найден.</summary>
    public const string AnalysisNotFound = "ANALYSIS_NOT_FOUND";

    /// <summary>Роль пользователя не найдена.</summary>
    public const string RoleNotFound = "ROLE_NOT_FOUND";

    /// <summary>Репозиторий GitHub не найден.</summary>
    public const string GitHubRepoNotFound = "GITHUB_REPO_NOT_FOUND";

    /// <summary>Пользователь с таким именем уже существует.</summary>
    public const string UsernameAlreadyExists = "USERNAME_ALREADY_EXISTS";

    /// <summary>Проект с таким именем уже существует у пользователя.</summary>
    public const string ProjectNameDuplicate = "PROJECT_NAME_DUPLICATE";

    /// <summary>Коммит с таким хэшем уже существует в проекте.</summary>
    public const string CommitAlreadyExists = "COMMIT_ALREADY_EXISTS";

    /// <summary>Невозможно удалить пользователя, так как у него есть проекты.</summary>
    public const string UserHasProjects = "USER_HAS_PROJECTS";

    /// <summary>Запрещено изменение роли или удаление последнего администратора.</summary>
    public const string LastAdminProtected = "LAST_ADMIN_PROTECTED";

    /// <summary>Анализ для данного коммита уже находится в очереди или выполняется.</summary>
    public const string AnalysisAlreadyQueued = "ANALYSIS_ALREADY_QUEUED";

    /// <summary>Превышен лимит запросов к GitHub API (Rate Limit).</summary>
    public const string GitHubRateLimited = "GITHUB_RATE_LIMITED";

    /// <summary>Доступ к репозиторию GitHub запрещен.</summary>
    public const string GitHubAccessDenied = "GITHUB_ACCESS_DENIED";

    /// <summary>Сервис GitHub недоступен.</summary>
    public const string GitHubServiceUnavailable = "GITHUB_SERVICE_UNAVAILABLE";

    /// <summary>Внутренняя непредвиденная ошибка сервера.</summary>
    public const string InternalServerError = "INTERNAL_SERVER_ERROR";
}
