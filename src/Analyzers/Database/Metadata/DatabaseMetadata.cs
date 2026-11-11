namespace DatabaseAnalyzer.Metadata;

public sealed class DatabaseMetadata
{
    public string? ContextName { get; init; }

    public IReadOnlyList<DatabaseEntityMetadata> Entities { get; init; } = [];
}
