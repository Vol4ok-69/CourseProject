namespace Application.DTOs;

public class ProjectDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string RepoUrl { get; set; } = null!;
    public int OwnerId { get; set; }
}