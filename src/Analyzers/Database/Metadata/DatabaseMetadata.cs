namespace DatabaseAnalyzer.Metadata;

public sealed class DatabaseMetadata
{
    public IReadOnlyList<DatabaseEntityMetadata> Entities { get; init; } = [];
}