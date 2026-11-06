namespace Application.Interfaces;

/// <summary>
/// Предоставляет операции для безопасной загрузки, проверки квот и распаковки исходного кода репозитория GitHub.
/// </summary>
public interface IRepositorySourceService
{
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
    /// Безопасно удаляет временные файлы и рабочий каталог анализа.
    /// </summary>
    Task CleanupAsync(int analysisId, CancellationToken cancellationToken = default);
}
