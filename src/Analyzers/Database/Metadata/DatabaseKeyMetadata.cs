namespace DatabaseAnalyzer.Metadata;

public sealed class DatabaseKeyMetadata
{
    public string Name { get; init; } = null!;

    public IReadOnlyList<string> PropertyNames { get; init; } = [];

    public bool IsPrimaryKey { get; init; }
}