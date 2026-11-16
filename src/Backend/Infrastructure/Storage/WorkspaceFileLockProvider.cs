using Application.Interfaces;

namespace Infrastructure.Storage;

/// <summary>
/// Реализация межпроцессных блокировок рабочего пространства через FileShare.
/// Файловые дескрипторы удерживаются вызывающим кодом на протяжении защищённых операций.
/// </summary>
public sealed class WorkspaceFileLockProvider : IWorkspaceFileLockProvider
{
    private readonly string _lockDirectory;

    public WorkspaceFileLockProvider(string? baseStoragePath)
    {
        var resolvedBasePath = !string.IsNullOrWhiteSpace(baseStoragePath)
            ? baseStoragePath
            : Path.Combine(
                Path.GetTempPath(),
                "CourseProject",
                "analyses");

        _lockDirectory = Path.Combine(
            Path.GetFullPath(resolvedBasePath),
            ".locks");
    }

    public FileStream? TryAcquireShared(int analysisId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(analysisId);

        return TryOpen(
            analysisId,
            FileAccess.Read,
            FileShare.Read);
    }

    public FileStream? TryAcquireExclusive(int analysisId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(analysisId);

        return TryOpen(
            analysisId,
            FileAccess.ReadWrite,
            FileShare.None);
    }

    private FileStream? TryOpen(
        int analysisId,
        FileAccess access,
        FileShare share)
    {
        try
        {
            Directory.CreateDirectory(_lockDirectory);

            var lockPath = Path.Combine(
                _lockDirectory,
                $"{analysisId}.lock");

            return new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                access,
                share,
                bufferSize: 1,
                options: FileOptions.None);
        }
        catch (IOException)
        {
            // Конфликт FileShare или другая ошибка ввода-вывода.
            // Fail-closed: при невозможности получить дескриптор
            // вызывающий код не должен выполнять защищённые операции.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // Недостаточно прав на рабочее пространство.
            // Fail-closed.
            return null;
        }
    }
}
