namespace DatabaseAnalyzer.Metadata;

public enum SourceKind
{
    ClassDeclaration,
    PartialClassDeclaration,
    PropertyDeclaration,
    DataAnnotation,
    OnModelCreating,
    EntityTypeConfiguration,
    Convention,
    Implicit
}

public sealed record SourceLocation(
    string FilePath,
    int? LineNumber = null,
    int? ColumnNumber = null,
    SourceKind SourceKind = SourceKind.Implicit);
