namespace Application.Analyzers;

/// <summary>
/// Идентификаторы правил и диагностических категорий DatabaseAnalyzer в соответствии с ADR-001.
/// </summary>
public static class DatabaseAnalyzerRules
{
    public const string ContainerUnavailable = "DB_CONTAINER_UNAVAILABLE";
    public const string MissingDbContext = "MISSING_DBCONTEXT";
    public const string RoslynWarning = "DB_ROSLYN_WARNING";
    public const string ExtractionError = "DB_EXTRACTION_ERROR";
    public const string ContainerError = "CONTAINER_ERROR";
    public const string ContainerTimeout = "CONTAINER_TIMEOUT";
    public const string InternalAnalyzerError = "INTERNAL_ANALYZER_ERROR";
    public const string UnsupportedFluentApi = "UNSUPPORTED_FLUENT_API";
    public const string SyntaxError = "SYNTAX_ERROR";
    public const string AmbiguousModel = "AMBIGUOUS_MODEL";
}
