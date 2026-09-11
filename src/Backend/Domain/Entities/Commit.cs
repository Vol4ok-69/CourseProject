namespace Domain.Entities;

public class Commit
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public string CommitHash { get; set; } = null!;

    public DateTime CommitDate { get; set; }

    public Project Project { get; set; } = null!;

    public ICollection<Analysis> Analyses { get; set; } = new List<Analysis>();
}