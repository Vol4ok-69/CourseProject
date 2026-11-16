using DatabaseAnalyzer.Metadata;

namespace DatabaseAnalyzer.Roslyn;

public sealed class RoslynModelExtractionResult
{
    public IReadOnlyList<DiscoveredDbContext> Contexts { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public IReadOnlyList<string> MetadataCapabilities { get; init; } = [];

    public IReadOnlyList<string> UnsupportedFeatures { get; init; } = [];

    public DatabaseMetadata? SingleContextMetadata =>
        Contexts.Count == 1 ? Contexts[0].ToDatabaseMetadata() : null;

    public IReadOnlyList<DatabaseMetadata> ToAllDatabaseMetadata() =>
        Contexts.Select(context => context.ToDatabaseMetadata()).ToList();
}
