namespace Database.Metadata;

public sealed class DatabaseForeignKeyMetadata
{
    public string Name { get; init; } = null!;

    public IReadOnlyList<string> PropertyNames { get; init; } = [];

    public string PrincipalEntityName { get; init; } = null!;

    public IReadOnlyList<string> PrincipalPropertyNames { get; init; } = [];

    public string? DeleteBehavior { get; init; }
}