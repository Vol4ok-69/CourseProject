namespace Database.Metadata;

public sealed class DatabaseEntityMetadata
{
    public string EntityName { get; init; } = null!;

    public string TableName { get; init; } = null!;

    public string? SchemaName { get; init; }

    public IReadOnlyList<DatabasePropertyMetadata> Properties { get; init; } = [];

    public IReadOnlyList<DatabaseKeyMetadata> Keys { get; init; } = [];

    public IReadOnlyList<DatabaseForeignKeyMetadata> ForeignKeys { get; init; } = [];

    public IReadOnlyList<DatabaseIndexMetadata> Indexes { get; init; } = [];

    public IReadOnlyList<DatabaseRelationshipMetadata> Relationships { get; init; } = [];
}