using Application.Analyzers;
using DatabaseAnalyzer.Providers;

namespace DatabaseAnalyzer.Tests.Providers;

public sealed class EfCoreDatabaseProjectResolverTests
{
    [Fact]
    public async Task ResolveAsync_ShouldReturnSpecifiedProject()
    {
        var repositoryPath = CreateRepository();
        var projectPath = Path.Combine(repositoryPath, "TestProject.csproj");

        await File.WriteAllTextAsync(projectPath, "<Project />");

        var resolver = new EfCoreDatabaseProjectResolver();

        var context = new AnalyzerContext
        {
            RepositoryPath = repositoryPath,
            CommitHash = "test",
            ProjectPath = "TestProject.csproj"
        };

        var result = await resolver.ResolveAsync(context);

        Assert.Equal(
            Path.GetFullPath(projectPath),
            result);
    }

    [Fact]
    public async Task ResolveAsync_ShouldFindSingleProjectAutomatically()
    {
        var repositoryPath = CreateRepository();
        var projectPath = Path.Combine(repositoryPath, "TestProject.csproj");

        await File.WriteAllTextAsync(projectPath, "<Project />");

        var resolver = new EfCoreDatabaseProjectResolver();

        var context = new AnalyzerContext
        {
            RepositoryPath = repositoryPath,
            CommitHash = "test"
        };

        var result = await resolver.ResolveAsync(context);

        Assert.Equal(
            Path.GetFullPath(projectPath),
            result);
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnNull_WhenNoProjectsFound()
    {
        var repositoryPath = CreateRepository();

        var resolver = new EfCoreDatabaseProjectResolver();

        var context = new AnalyzerContext
        {
            RepositoryPath = repositoryPath,
            CommitHash = "test"
        };

        var result = await resolver.ResolveAsync(context);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrow_WhenMultipleProjectsFound()
    {
        var repositoryPath = CreateRepository();

        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "Project1.csproj"),
            "<Project />");

        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "Project2.csproj"),
            "<Project />");

        var resolver = new EfCoreDatabaseProjectResolver();

        var context = new AnalyzerContext
        {
            RepositoryPath = repositoryPath,
            CommitHash = "test"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(context));
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrow_WhenSpecifiedProjectDoesNotExist()
    {
        var repositoryPath = CreateRepository();

        var resolver = new EfCoreDatabaseProjectResolver();

        var context = new AnalyzerContext
        {
            RepositoryPath = repositoryPath,
            CommitHash = "test",
            ProjectPath = "Missing.csproj"
        };

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => resolver.ResolveAsync(context));
    }

    private static string CreateRepository()
    {
        return Directory.CreateTempSubdirectory("database-analyzer-").FullName;
    }
}