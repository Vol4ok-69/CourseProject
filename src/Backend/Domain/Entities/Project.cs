namespace Domain.Entities;

public class Project
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public string RepoUrl { get; set; } = null!;

    public int OwnerId { get; set; }

    public string? LastSyncedCommitHash { get; set; }

    public string? LastSyncedBranch { get; set; }

    public string? LastSyncedRepositoryId { get; set; }

    public string? LastSyncedRepositoryHost { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    public User Owner { get; set; } = null!;

    public ICollection<Commit> Commits { get; set; } = new List<Commit>();
}
