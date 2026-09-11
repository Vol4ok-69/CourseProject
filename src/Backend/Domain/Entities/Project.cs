namespace Domain.Entities;

public class Project
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public string RepoUrl { get; set; } = null!;

    public int OwnerId { get; set; }

    public User Owner { get; set; } = null!;

    public ICollection<Commit> Commits { get; set; } = new List<Commit>();
}