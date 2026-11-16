namespace Application.Interfaces;

/// <summary>
/// Предоставляет межпроцессные файловые блокировки рабочего пространства анализа.
/// </summary>
public interface IWorkspaceFileLockProvider
{
    /// <summary>
    /// Пытается получить совместную файловую блокировку рабочего пространства.
    /// Несколько Worker могут одновременно удерживать такую блокировку.
    /// </summary>
    FileStream? TryAcquireShared(int analysisId);

    /// <summary>
    /// Пытается получить эксклюзивную файловую блокировку рабочего пространства.
    /// Получить её можно только при отсутствии совместных блокировок.
    /// </summary>
    FileStream? TryAcquireExclusive(int analysisId);
}
