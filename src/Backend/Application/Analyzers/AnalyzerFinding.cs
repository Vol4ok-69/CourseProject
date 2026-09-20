using Domain.Enums;

namespace Application.Analyzers;

public class AnalyzerFinding
{
    public string FilePath { get; init; } = null!;
    public int LineNumber { get; init; }
    public string Rule { get; init; } = null!;
    public string Message { get; init; } = null!;
    public Severity Severity { get; init; }
    public string? Recommendation { get; init; }
}