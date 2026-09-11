using Domain.Enums;

namespace Domain.Entities;

public class AnalysisResult
{
    public int Id { get; set; }

    public int AnalysisId { get; set; }

    public string FilePath { get; set; } = null!;

    public int LineNumber { get; set; }

    public string Rule { get; set; } = null!;

    public string Message { get; set; } = null!;

    public Severity Severity { get; set; }

    public string? Recommendation { get; set; }

    public AnalyzerType AnalyzerType { get; set; }

    public Analysis Analysis { get; set; } = null!;
}