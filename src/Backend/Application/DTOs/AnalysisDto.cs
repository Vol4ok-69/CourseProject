using Domain.Enums;

namespace Application.DTOs;

public class AnalysisDto
{
    public int Id { get; set; }
    public int CommitId { get; set; }
    public AnalysisStatus Status { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public IReadOnlyList<AnalysisResultDto> Results { get; set; } = [];
}

public class AnalysisResultDto
{
    public int Id { get; set; }
    public string FilePath { get; set; } = null!;
    public int LineNumber { get; set; }
    public string Rule { get; set; } = null!;
    public string Message { get; set; } = null!;
    public Severity Severity { get; set; }
    public string? Recommendation { get; set; }
    public AnalyzerType AnalyzerType { get; set; }
}