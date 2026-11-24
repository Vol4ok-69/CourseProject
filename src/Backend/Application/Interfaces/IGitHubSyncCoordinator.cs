namespace Application.Interfaces;

/// <summary>
/// Координатор синхронизации проектов, предотвращающий параллельные запуски для одного и того же проекта в рамках процесса.
/// </summary>
public interface IGitHubSyncCoordinator
{
    /// <summary>
    /// Пытается захватить блокировку синхронизации для указанного проекта.
    /// </summary>
    /// <param name="projectId">Идентификатор проекта.</param>
    /// <returns>Объект IDisposable для освобождения блокировки, либо null, если синхронизация уже выполняется.</returns>
    IDisposable? TryAcquire(int projectId);
}
