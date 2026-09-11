using Domain.Enums;

namespace Domain.Entities;

public class Analysis
{
    public int Id { get; set; }

    public int CommitId { get; set; }

    public AnalysisStatus Status { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public Commit Commit { get; set; } = null!;

    public ICollection<AnalysisResult> Results { get; set; } = new List<AnalysisResult>();
}