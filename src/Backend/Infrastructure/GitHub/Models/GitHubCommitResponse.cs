namespace Infrastructure.GitHub.Models;

public class GitHubCommitResponse
{
    public string Sha { get; set; } = null!;
    public GitHubCommitDetails? Commit { get; set; }
}

public class GitHubCommitDetails
{
    public GitHubCommitAuthor? Author { get; set; }
}

public class GitHubCommitAuthor
{
    public DateTime? Date { get; set; }
}