using DatabaseAnalyzer.Metadata;

namespace DatabaseAnalyzer.Roslyn;

public sealed class DiscoveredDbContext
{
    public string Name { get; init; } = null!;

    public string? Namespace { get; init; }

    public string? FilePath { get; init; }

    public string? ProjectId { get; init; }

    public SourceLocation? Location { get; init; }

    public IReadOnlyList<DiscoveredEntity> Entities { get; init; } = [];

    public IReadOnlyList<string> UnsupportedFeatures { get; init; } = [];

    public DatabaseMetadata ToDatabaseMetadata()
    {
        return new DatabaseMetadata
        {
            ContextName = Name,
            Entities = Entities.Select(entity => entity.ToDatabaseEntityMetadata()).ToList()
        };
    }
}
