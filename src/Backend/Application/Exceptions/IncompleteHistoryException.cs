namespace Application.Exceptions;

public enum IncompleteHistoryKind
{
    HeadNotFetched,
    UnresolvedParents
}

/// <summary>
/// Исключение, сигнализирующее о том, что обход истории коммитов завершился с неполным графом.
/// </summary>
public class IncompleteHistoryException : InvalidOperationException
{
    public IncompleteHistoryKind Kind { get; }
    public IReadOnlySet<string> UnresolvedParents { get; }

    public IncompleteHistoryException(IncompleteHistoryKind kind, string message, IReadOnlySet<string>? unresolvedParents = null)
        : base(message)
    {
        Kind = kind;
        UnresolvedParents = unresolvedParents ?? new HashSet<string>();
    }
}
