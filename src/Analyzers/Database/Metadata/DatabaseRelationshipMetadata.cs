namespace Database.Metadata;

public sealed class DatabaseRelationshipMetadata
{
    public string PrincipalEntityName { get; init; } = null!;

    public string DependentEntityName { get; init; } = null!;

    public string RelationshipType { get; init; } = null!;

    public string? NavigationToPrincipal { get; init; }

    public string? NavigationToDependent { get; init; }
}