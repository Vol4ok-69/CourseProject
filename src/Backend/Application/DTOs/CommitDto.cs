namespace Application.DTOs;

public class CommitDto
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string CommitHash { get; set; } = null!;
    public DateTime CommitDate { get; set; }
}