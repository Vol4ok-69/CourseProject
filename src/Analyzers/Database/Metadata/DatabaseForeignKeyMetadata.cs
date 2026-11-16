namespace DatabaseAnalyzer.Metadata;

public sealed class DatabaseForeignKeyMetadata
{
    public string Name { get; init; } = null!;

    public IReadOnlyList<string> PropertyNames { get; init; } = [];

    public string PrincipalEntityName { get; init; } = null!;

    public IReadOnlyList<string> PrincipalPropertyNames { get; init; } = [];

    public string? DeleteBehavior { get; init; }

    public string? NavigationToPrincipal { get; init; }

    public string? NavigationToDependent { get; init; }

    public bool IsNavigationResolved { get; init; } = true;

    public bool IsExplicitName { get; init; }

    public SourceLocation? Location { get; init; }
}
