using System.Text.Json.Serialization;

namespace NetScope.DatabaseAnalyzer.Runner.Contracts;

public sealed class AnalysisInputContract
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0";

    [JsonPropertyName("analysisId")]
    public int AnalysisId { get; set; }

    [JsonPropertyName("executionToken")]
    public Guid ExecutionToken { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [JsonPropertyName("metadataCapabilities")]
    public List<string> MetadataCapabilities { get; set; } = [];

    [JsonPropertyName("configuration")]
    public ConfigurationDto Configuration { get; set; } = new();

    [JsonPropertyName("metadata")]
    public MetadataDto Metadata { get; set; } = new();

    [JsonPropertyName("diagnostics")]
    public List<string>? Diagnostics { get; set; }
}

public sealed class ConfigurationDto
{
    [JsonPropertyName("enabledRules")]
    public List<string> EnabledRules { get; set; } = ["DB01", "DB02", "DB03", "DB04", "DB05"];

    [JsonPropertyName("maxFindings")]
    public int MaxFindings { get; set; } = 1000;
}

public sealed class MetadataDto
{
    [JsonPropertyName("dbContexts")]
    public List<DbContextDto> DbContexts { get; set; } = [];
}

public sealed class DbContextDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("contextId")]
    public string? ContextId { get; set; }

    [JsonPropertyName("primaryLocation")]
    public SourceLocationDto? PrimaryLocation { get; set; }

    [JsonPropertyName("unsupportedFeatures")]
    public List<string>? UnsupportedFeatures { get; set; }

    [JsonPropertyName("entities")]
    public List<EntityDto> Entities { get; set; } = [];
}

public sealed class EntityDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("entityId")]
    public string? EntityId { get; set; }

    [JsonPropertyName("clrTypeName")]
    public string? ClrTypeName { get; set; }

    [JsonPropertyName("tableName")]
    public string? TableName { get; set; }

    [JsonPropertyName("schemaName")]
    public string? SchemaName { get; set; }

    [JsonPropertyName("mappingType")]
    public string MappingType { get; set; } = "Table";

    [JsonPropertyName("keyType")]
    public string KeyType { get; set; } = "PrimaryKey";

    [JsonPropertyName("viewName")]
    public string? ViewName { get; set; }

    [JsonPropertyName("sqlQuery")]
    public string? SqlQuery { get; set; }

    [JsonPropertyName("functionName")]
    public string? FunctionName { get; set; }

    [JsonPropertyName("isSharedType")]
    public bool IsSharedType { get; set; }

    [JsonPropertyName("isDerivedEntity")]
    public bool IsDerivedEntity { get; set; }

    [JsonPropertyName("baseEntityName")]
    public string? BaseEntityName { get; set; }

    [JsonPropertyName("hasIncompleteMetadata")]
    public bool HasIncompleteMetadata { get; set; }

    [JsonPropertyName("hasIncompleteKeys")]
    public bool HasIncompleteKeys { get; set; }

    [JsonPropertyName("hasIncompleteForeignKeys")]
    public bool HasIncompleteForeignKeys { get; set; }

    [JsonPropertyName("hasIncompleteIndexes")]
    public bool HasIncompleteIndexes { get; set; }

    [JsonPropertyName("hasIncompleteNavigations")]
    public bool HasIncompleteNavigations { get; set; }

    [JsonPropertyName("hasIncompleteSkipNavigations")]
    public bool HasIncompleteSkipNavigations { get; set; }

    [JsonPropertyName("primaryLocation")]
    public SourceLocationDto? PrimaryLocation { get; set; }

    [JsonPropertyName("unsupportedFeatures")]
    public List<string>? UnsupportedFeatures { get; set; }

    [JsonPropertyName("properties")]
    public List<PropertyDto> Properties { get; set; } = [];

    [JsonPropertyName("keys")]
    public List<KeyDto> Keys { get; set; } = [];

    [JsonPropertyName("foreignKeys")]
    public List<ForeignKeyDto> ForeignKeys { get; set; } = [];

    [JsonPropertyName("indexes")]
    public List<IndexDto> Indexes { get; set; } = [];

    [JsonPropertyName("skipNavigations")]
    public List<SkipNavigationDto> SkipNavigations { get; set; } = [];
}

public sealed class PropertyDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("columnName")]
    public string ColumnName { get; set; } = string.Empty;

    [JsonPropertyName("clrTypeName")]
    public string? ClrTypeName { get; set; }

    [JsonPropertyName("isPrimaryKey")]
    public bool IsPrimaryKey { get; set; }

    [JsonPropertyName("isForeignKey")]
    public bool IsForeignKey { get; set; }

    [JsonPropertyName("isShadowProperty")]
    public bool IsShadowProperty { get; set; }

    [JsonPropertyName("isConcurrencyToken")]
    public bool IsConcurrencyToken { get; set; }

    [JsonPropertyName("isRequired")]
    public bool IsRequired { get; set; }

    [JsonPropertyName("location")]
    public SourceLocationDto? Location { get; set; }
}

public sealed class KeyDto
{
    [JsonPropertyName("properties")]
    public List<string> Properties { get; set; } = [];

    [JsonPropertyName("isPrimaryKey")]
    public bool IsPrimaryKey { get; set; }

    [JsonPropertyName("isExplicitName")]
    public bool IsExplicitName { get; set; }

    [JsonPropertyName("location")]
    public SourceLocationDto? Location { get; set; }
}

public sealed class ForeignKeyDto
{
    [JsonPropertyName("principalEntityName")]
    public string PrincipalEntityName { get; set; } = string.Empty;

    [JsonPropertyName("properties")]
    public List<string> Properties { get; set; } = [];

    [JsonPropertyName("principalProperties")]
    public List<string> PrincipalProperties { get; set; } = [];

    [JsonPropertyName("navigationPropertyName")]
    public string? NavigationPropertyName { get; set; }

    [JsonPropertyName("navigationToPrincipal")]
    public string? NavigationToPrincipal { get; set; }

    [JsonPropertyName("navigationToDependent")]
    public string? NavigationToDependent { get; set; }

    [JsonPropertyName("deleteBehavior")]
    public string? DeleteBehavior { get; set; }

    [JsonPropertyName("isExplicitName")]
    public bool IsExplicitName { get; set; }

    [JsonPropertyName("isNavigationResolved")]
    public bool IsNavigationResolved { get; set; }

    [JsonPropertyName("location")]
    public SourceLocationDto? Location { get; set; }
}

public sealed class IndexDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("properties")]
    public List<string> Properties { get; set; } = [];

    [JsonPropertyName("isUnique")]
    public bool IsUnique { get; set; }

    [JsonPropertyName("filter")]
    public string? Filter { get; set; }

    [JsonPropertyName("isExplicitName")]
    public bool IsExplicitName { get; set; }

    [JsonPropertyName("location")]
    public SourceLocationDto? Location { get; set; }
}

public sealed class SkipNavigationDto
{
    [JsonPropertyName("navigationPropertyName")]
    public string NavigationPropertyName { get; set; } = string.Empty;

    [JsonPropertyName("targetEntityName")]
    public string TargetEntityName { get; set; } = string.Empty;

    [JsonPropertyName("targetEntityId")]
    public string? TargetEntityId { get; set; }

    [JsonPropertyName("joinEntityName")]
    public string? JoinEntityName { get; set; }

    [JsonPropertyName("joinEntityId")]
    public string? JoinEntityId { get; set; }

    [JsonPropertyName("inverseNavigationPropertyName")]
    public string? InverseNavigationPropertyName { get; set; }

    [JsonPropertyName("location")]
    public SourceLocationDto? Location { get; set; }
}

public sealed class SourceLocationDto
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("startLine")]
    public int StartLine { get; set; }

    [JsonPropertyName("startColumn")]
    public int StartColumn { get; set; }

    [JsonPropertyName("endLine")]
    public int EndLine { get; set; }

    [JsonPropertyName("endColumn")]
    public int EndColumn { get; set; }
}
