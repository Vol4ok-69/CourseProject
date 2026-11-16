using DatabaseAnalyzer.Roslyn;

namespace DatabaseAnalyzer.Metadata;

public sealed class DatabaseEntityMetadata
{
    public string EntityName { get; init; } = null!;

    public string TableName { get; init; } = null!;

    public string? SchemaName { get; init; }

    public EntityMappingType MappingType { get; init; } = EntityMappingType.Table;

    public EntityKeyType KeyType { get; init; } = EntityKeyType.PrimaryKey;

    public string? ViewName { get; init; }

    public string? SqlQuery { get; init; }

    public string? FunctionName { get; init; }

    public bool IsSharedType { get; init; }

    public string? ClrTypeName { get; init; }

    public bool IsDerivedEntity { get; init; }

    public string? BaseEntityName { get; init; }

    public bool HasIncompleteMetadata { get; init; }
    public bool HasIncompleteKeys { get; init; }
    public bool HasIncompleteForeignKeys { get; init; }
    public bool HasIncompleteIndexes { get; init; }
    public bool HasIncompleteNavigations { get; init; }
    public bool HasIncompleteSkipNavigations { get; init; }

    public IReadOnlyList<string> UnsupportedFeatures { get; init; } = [];

    public IReadOnlyList<DatabaseKeyMetadata> AlternateKeys { get; init; } = [];

    public SourceLocation? PrimaryLocation { get; init; }

    public IReadOnlyList<SourceLocation> Declarations { get; init; } = [];

    public IReadOnlyList<SourceLocation> Configurations { get; init; } = [];

    public IReadOnlyList<DatabasePropertyMetadata> Properties { get; init; } = [];

    public IReadOnlyList<DatabaseKeyMetadata> Keys { get; init; } = [];

    public IReadOnlyList<DatabaseForeignKeyMetadata> ForeignKeys { get; init; } = [];

    public IReadOnlyList<DatabaseIndexMetadata> Indexes { get; init; } = [];

    public IReadOnlyList<DatabaseRelationshipMetadata> Relationships { get; init; } = [];

    public IReadOnlyList<DatabaseSkipNavigationMetadata> SkipNavigations { get; init; } = [];
}
