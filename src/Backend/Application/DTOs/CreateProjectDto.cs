namespace Application.DTOs;

public class CreateProjectDto
{
    public string Name { get; set; } = null!;
    public string RepoUrl { get; set; } = null!;
    public int OwnerId { get; set; }
}