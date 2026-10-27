namespace DatabaseAnalyzer.Metadata;

public sealed class DatabaseSkipNavigationMetadata
{
    public string NavigationName { get; init; } = null!;

    public string TargetEntityName { get; init; } = null!;

    public string JoinEntityTableName { get; init; } = null!;

    public string? JoinEntityName { get; init; }

    public bool IsImplicitJoinEntity { get; init; }

    public string? InverseNavigationName { get; init; }
}
