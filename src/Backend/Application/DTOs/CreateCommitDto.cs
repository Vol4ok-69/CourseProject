namespace Application.DTOs;

public class CreateCommitDto
{
    public string CommitHash { get; set; } = null!;
    public DateTime CommitDate { get; set; }
}