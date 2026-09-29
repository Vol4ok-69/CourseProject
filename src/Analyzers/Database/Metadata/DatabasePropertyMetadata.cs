namespace DatabaseAnalyzer.Metadata;

public sealed class DatabasePropertyMetadata
{
    public string PropertyName { get; init; } = null!;

    public string ColumnName { get; init; } = null!;

    public string? StoreType { get; init; }

    public bool IsNullable { get; init; }

    public bool IsPrimaryKey { get; init; }

    public bool IsUnique { get; init; }

    public bool IsGenerated { get; init; }

    public string? DefaultValueSql { get; init; }
}