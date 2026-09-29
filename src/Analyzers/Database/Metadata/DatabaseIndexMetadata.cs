namespace DatabaseAnalyzer.Metadata;

public sealed class DatabaseIndexMetadata
{
    public string Name { get; init; } = null!;

    public IReadOnlyList<string> PropertyNames { get; init; } = [];

    public bool IsUnique { get; init; }
}