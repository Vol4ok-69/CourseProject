namespace Infrastructure.GitHub.Models;

public record GitHubRepositoryReference(string Owner, string Name)
{
    public static GitHubRepositoryReference Parse(string repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Некорректный URL GitHub-репозитория.");
        }

        var segments = uri.AbsolutePath
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length != 2)
        {
            throw new InvalidOperationException(
                "URL GitHub-репозитория должен иметь формат https://github.com/{owner}/{repo}.");
        }

        var repositoryName = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];

        return new GitHubRepositoryReference(segments[0], repositoryName);
    }
}