namespace Application.Exceptions;

/// <summary>
/// Исключение, возникающее при попытке запустить синхронизацию проекта, для которого уже выполняется синхронизация.
/// </summary>
public class SyncAlreadyInProgressException : InvalidOperationException
{
    public const string DefaultMessage = "Синхронизация данного проекта с GitHub уже выполняется.";

    public SyncAlreadyInProgressException() : base(DefaultMessage)
    {
    }

    public SyncAlreadyInProgressException(string message) : base(message)
    {
    }

    public SyncAlreadyInProgressException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
