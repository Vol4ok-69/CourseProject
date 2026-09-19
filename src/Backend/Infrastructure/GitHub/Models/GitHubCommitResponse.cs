namespace Infrastructure.GitHub.Models;

public class GitHubCommitResponse
{
    public string Sha { get; set; } = null!;
    public GitHubCommitDetails Commit { get; set; } = null!;
}

public class GitHubCommitDetails
{
    public GitHubCommitAuthor Author { get; set; } = null!;
}

public class GitHubCommitAuthor
{
    public DateTime? Date { get; set; }
}