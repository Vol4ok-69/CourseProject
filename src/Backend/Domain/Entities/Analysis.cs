using Domain.Enums;

namespace Domain.Entities;

/// <summary>
/// Сущность задачи анализа коммита в очереди задач.
/// </summary>
public class Analysis
{
    public int Id { get; set; }

    public int CommitId { get; set; }

    public AnalysisStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime? LeaseExpiration { get; set; }

    public Guid? ExecutionToken { get; set; }

    /// <summary>
    /// Сообщение об ошибке при сбое выполнения анализа. Равно null при успешном завершении.
    /// </summary>
    public string? ErrorMessage { get; set; }

    public Commit Commit { get; set; } = null!;

    public ICollection<AnalysisResult> Results { get; set; } = new List<AnalysisResult>();
}
