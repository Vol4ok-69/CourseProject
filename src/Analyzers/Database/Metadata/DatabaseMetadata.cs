namespace Database.Metadata;

public sealed class DatabaseMetadata
{
    public IReadOnlyList<DatabaseEntityMetadata> Entities { get; init; } = [];
}