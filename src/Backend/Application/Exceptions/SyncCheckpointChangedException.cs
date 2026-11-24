namespace Application.Exceptions;

/// <summary>
/// Исключение, возникающее при изменении контрольной точки проекта параллельным процессом во время синхронизации.
/// </summary>
public class SyncCheckpointChangedException : InvalidOperationException
{
    public const string DefaultMessage = "Контрольная точка проекта была изменена другим процессом во время синхронизации.";

    public SyncCheckpointChangedException() : base(DefaultMessage)
    {
    }

    public SyncCheckpointChangedException(string message) : base(message)
    {
    }

    public SyncCheckpointChangedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
