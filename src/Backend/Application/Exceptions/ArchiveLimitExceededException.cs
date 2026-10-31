namespace Application.Exceptions;

/// <summary>
/// Исключение, возникающее при превышении допустимых квот размера архива или извлекаемых файлов.
/// </summary>
public class ArchiveLimitExceededException : Exception
{
    public const string DefaultMessage = "Превышен лимит размера или количества файлов архива репозитория.";

    public ArchiveLimitExceededException() : base(DefaultMessage)
    {
    }

    public ArchiveLimitExceededException(string message) : base(message)
    {
    }

    public ArchiveLimitExceededException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
