namespace Application.Exceptions;

/// <summary>
/// Исключение, возникающее при попытке повторно запустить анализ коммита, для которого уже есть активная задача.
/// </summary>
public class AnalysisAlreadyQueuedException : InvalidOperationException
{
    public const string DefaultMessage = "Анализ для данного коммита уже находится в очереди или выполняется.";

    public AnalysisAlreadyQueuedException() : base(DefaultMessage)
    {
    }

    public AnalysisAlreadyQueuedException(string message) : base(message)
    {
    }

    public AnalysisAlreadyQueuedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
