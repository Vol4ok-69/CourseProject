namespace Application.Interfaces;

/// <summary>
/// Сервис безопасной очистки устаревших и сиротских рабочих каталогов анализов (TD-003).
/// </summary>
public interface IAnalysisCleanupService
{
    /// <summary>
    /// Выполняет один цикл поиска и безопасного удаления устаревших рабочих каталогов
    /// под защитой session-level advisory locks PostgreSQL.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Количество успешно удалённых устаревших каталогов.</returns>
    Task<int> CleanupStaleDirectoriesAsync(CancellationToken cancellationToken = default);
}
