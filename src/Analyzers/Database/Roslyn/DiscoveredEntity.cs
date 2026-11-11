using DatabaseAnalyzer.Metadata;

namespace DatabaseAnalyzer.Roslyn;

public sealed class DiscoveredEntity
{
    private readonly List<DatabasePropertyMetadata> _properties = [];
    private readonly List<DatabaseKeyMetadata> _keys = [];
    private readonly List<DatabaseForeignKeyMetadata> _foreignKeys = [];
    private readonly List<DatabaseIndexMetadata> _indexes = [];
    private readonly List<DatabaseRelationshipMetadata> _relationships = [];
    private readonly List<DatabaseSkipNavigationMetadata> _skipNavigations = [];

    public string EntityName { get; init; } = null!;

    public string? DbSetPropertyName { get; set; }

    public string? FullEntityTypeName { get; init; }

    public string? TableName { get; set; }

    public string? SchemaName { get; set; }

    public bool IsTableNameConfiguredByFluentApi { get; set; }

    public bool IsSchemaNameConfiguredByFluentApi { get; set; }

    public bool IsPrimaryKeyConfiguredByFluentApi { get; set; }

    public IReadOnlyList<DatabasePropertyMetadata> Properties => _properties;

    public IReadOnlyList<DatabaseKeyMetadata> Keys => _keys;

    public IReadOnlyList<DatabaseForeignKeyMetadata> ForeignKeys => _foreignKeys;

    public IReadOnlyList<DatabaseIndexMetadata> Indexes => _indexes;

    public IReadOnlyList<DatabaseRelationshipMetadata> Relationships => _relationships;

    public IReadOnlyList<DatabaseSkipNavigationMetadata> SkipNavigations => _skipNavigations;

    public void AddKey(DatabaseKeyMetadata key)
    {
        if (key.IsPrimaryKey && IsPrimaryKeyConfiguredByFluentApi)
        {
            // Fluent API имеет наивысший приоритет над Data Annotations
            return;
        }

        if (!_keys.Any(k => k.IsPrimaryKey == key.IsPrimaryKey &&
                            k.PropertyNames.SequenceEqual(key.PropertyNames, StringComparer.Ordinal)))
        {
            _keys.Add(key);
        }
    }

    public void SetPrimaryKeyFromFluentApi(DatabaseKeyMetadata key)
    {
        _keys.RemoveAll(k => k.IsPrimaryKey);
        _keys.Add(key);
        IsPrimaryKeyConfiguredByFluentApi = true;
    }

    public void AddSkipNavigation(DatabaseSkipNavigationMetadata skipNav)
    {
        if (!_skipNavigations.Any(s => string.Equals(s.NavigationName, skipNav.NavigationName, StringComparison.Ordinal) &&
                                       string.Equals(s.TargetEntityName, skipNav.TargetEntityName, StringComparison.Ordinal)))
        {
            _skipNavigations.Add(skipNav);
        }
    }

    public void AddIndex(DatabaseIndexMetadata index)
    {
        if (!_indexes.Any(i => i.IsUnique == index.IsUnique &&
                               string.Equals(i.Filter, index.Filter, StringComparison.Ordinal) &&
                               i.PropertyNames.SequenceEqual(index.PropertyNames, StringComparer.Ordinal)))
        {
            _indexes.Add(index);
        }
    }

    public void AddForeignKey(DatabaseForeignKeyMetadata foreignKey)
    {
        if (!_foreignKeys.Any(f => string.Equals(f.PrincipalEntityName, foreignKey.PrincipalEntityName, StringComparison.Ordinal) &&
                                   f.PropertyNames.SequenceEqual(foreignKey.PropertyNames, StringComparer.Ordinal)))
        {
            _foreignKeys.Add(foreignKey);
        }
    }

    public void AddOrUpdateProperty(DatabasePropertyMetadata property)
    {
        var existingIndex = _properties.FindIndex(p => string.Equals(p.PropertyName, property.PropertyName, StringComparison.Ordinal));
        if (existingIndex >= 0)
        {
            _properties[existingIndex] = property;
        }
        else
        {
            _properties.Add(property);
        }
    }

    public DatabaseEntityMetadata ToDatabaseEntityMetadata()
    {
        var resolvedTableName = !string.IsNullOrWhiteSpace(TableName)
            ? TableName
            : (!string.IsNullOrWhiteSpace(DbSetPropertyName) ? DbSetPropertyName : EntityName);

        return new DatabaseEntityMetadata
        {
            EntityName = EntityName,
            TableName = resolvedTableName,
            SchemaName = SchemaName,
            Properties = _properties.ToList(),
            Keys = _keys.ToList(),
            ForeignKeys = _foreignKeys.ToList(),
            Indexes = _indexes.ToList(),
            Relationships = _relationships.ToList(),
            SkipNavigations = _skipNavigations.ToList()
        };
    }
}
