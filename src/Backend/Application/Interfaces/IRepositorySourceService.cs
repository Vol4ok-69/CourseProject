namespace Application.Interfaces;

/// <summary>
/// Предоставляет операции для безопасной загрузки, проверки квот и распаковки исходного кода репозитория GitHub.
/// </summary>
public interface IRepositorySourceService
{
    /// <summary>
    /// Загружает ZIP-архив репозитория по URL и хэшу коммита с использованием персонального токена доступа (PAT)
    /// под защитой переданного активного lease guard конкретного исполнения (TD-003).
    /// </summary>
    Task<string> DownloadAndExtractAsync(
        string repositoryUrl,
        string commitHash,
        int analysisId,
        Guid executionToken,
        string personalAccessToken,
        IAnalysisLeaseGuard guard,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Загружает ZIP-архив репозитория по URL и хэшу коммита с использованием персонального токена доступа (PAT)
    /// и выполняет контролируемую распаковку в изолированный каталог анализа конкретного исполнения (TD-003).
    /// </summary>
    Task<string> DownloadAndExtractAsync(
        string repositoryUrl,
        string commitHash,
        int analysisId,
        Guid executionToken,
        string personalAccessToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Загружает ZIP-архив репозитория по URL и хэшу коммита с использованием персонального токена доступа (PAT)
    /// и выполняет контролируемую распаковку в изолированный каталог анализа.
    /// </summary>
    Task<string> DownloadAndExtractAsync(
        string repositoryUrl,
        string commitHash,
        int analysisId,
        string personalAccessToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Устаревшая перегрузка без передачи PAT. Гарантированно завершается исключением, исключая анонимные вызовы.
    /// </summary>
    Task<string> DownloadAndExtractAsync(
        string repositoryUrl,
        string commitHash,
        int analysisId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Безопасно удаляет временные файлы и рабочий каталог конкретного исполнения анализа под защитой активного lease guard (TD-003).
    /// Использует уже захваченный вызывающим кодом advisory lock без создания конкурирующего физического соединения.
    /// </summary>
    Task CleanupAsync(
        int analysisId,
        Guid executionToken,
        IAnalysisLeaseGuard activeGuard,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Безопасно удаляет временные файлы и рабочий каталог конкретного исполнения анализа (TD-003).
    /// Выполняет проверку владения задачей в PostgreSQL под защитой advisory lock.
    /// </summary>
    Task CleanupAsync(int analysisId, Guid executionToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Безопасно удаляет временные файлы и рабочий каталог анализа.
    /// </summary>
    Task CleanupAsync(int analysisId, CancellationToken cancellationToken = default);
}
