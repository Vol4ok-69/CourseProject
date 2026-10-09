using Application.Analyzers;
using DatabaseAnalyzer.Providers;

namespace DatabaseAnalyzer.Tests.Providers;

public sealed class EfCoreDatabaseProjectResolverTests
{
    [Fact]
    public async Task ResolveAsync_ShouldReturnSpecifiedProject()
    {
        var repositoryPath = CreateRepository();

        var infrastructureDirectory = Path.Combine(
            repositoryPath, "src", "Infrastructure");
        var apiDirectory = Path.Combine(
            repositoryPath, "src", "Api");

        Directory.CreateDirectory(infrastructureDirectory);
        Directory.CreateDirectory(apiDirectory);

        var infrastructureProject = Path.Combine(
            infrastructureDirectory, "Infrastructure.csproj");
        var apiProject = Path.Combine(apiDirectory, "Api.csproj");

        await File.WriteAllTextAsync(
            infrastructureProject, "<Project />");

        await File.WriteAllTextAsync
        (
            apiProject,
            """
            <Project>
              <ItemGroup>
                <ProjectReference Include="../Infrastructure/Infrastructure.csproj" />
              </ItemGroup>
            </Project>
            """
        );

        await File.WriteAllTextAsync(
            Path.Combine(infrastructureDirectory, "AppDbContext.cs"),
            "public class AppDbContext : Microsoft.EntityFrameworkCore.DbContext { }");

        var resolver = new EfCoreDatabaseProjectResolver();

        var context = new AnalyzerContext
        {
            RepositoryPath = repositoryPath,
            CommitHash = "test",
            ProjectPath = Path.GetRelativePath(repositoryPath, apiProject)
        };

        var result = await resolver.ResolveAsync(context);

        Assert.NotNull(result);
        Assert.Equal(
            Path.GetFullPath(infrastructureProject),
            result.ContextProjectPath);
        Assert.Equal(
            Path.GetFullPath(apiProject),
            result.StartupProjectPath);
    }

    [Fact]
    public async Task ResolveAsync_ShouldFindContextAndStartupProjectsAutomatically()
    {
        var repositoryPath = CreateRepository();

        var infrastructureDirectory = Path.Combine(
            repositoryPath, "src", "Infrastructure");
        var apiDirectory = Path.Combine(
            repositoryPath, "src", "Api");

        Directory.CreateDirectory(infrastructureDirectory);
        Directory.CreateDirectory(apiDirectory);

        var infrastructureProject = Path.Combine(
            infrastructureDirectory, "Infrastructure.csproj");
        var apiProject = Path.Combine(
            apiDirectory, "Api.csproj");

        await File.WriteAllTextAsync(
            infrastructureProject,
            "<Project />");

        await File.WriteAllTextAsync
        (
            apiProject,
            """
            <Project>
              <ItemGroup>
                <ProjectReference Include="../Infrastructure/Infrastructure.csproj" />
              </ItemGroup>
            </Project>
            """
        );

        await File.WriteAllTextAsync
        (
            Path.Combine(infrastructureDirectory, "AppDbContext.cs"),
            """
            public class AppDbContext
                : Microsoft.EntityFrameworkCore.DbContext
            {
            }
            """
        );

        var resolver = new EfCoreDatabaseProjectResolver();

        var context = new AnalyzerContext
        {
            RepositoryPath = repositoryPath,
            CommitHash = "test"
        };

        var result = await resolver.ResolveAsync(context);

        Assert.NotNull(result);

        Assert.Equal(
            Path.GetFullPath(infrastructureProject),
            result.ContextProjectPath);

        Assert.Equal(
            Path.GetFullPath(apiProject),
            result.StartupProjectPath);
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
    public async Task ResolveAsync_ShouldSelectProjectDeclaringDbContext()
    {
        var repositoryPath = CreateRepository();

        var infrastructureDirectory = Path.Combine(repositoryPath, "src", "Infrastructure");
        var apiDirectory = Path.Combine(repositoryPath, "src", "Api");
        var testsDirectory = Path.Combine(repositoryPath, "tests");

        Directory.CreateDirectory(infrastructureDirectory);
        Directory.CreateDirectory(apiDirectory);
        Directory.CreateDirectory(testsDirectory);

        var infrastructureProject = Path.Combine(
            infrastructureDirectory,
            "Infrastructure.csproj");

        var apiProject = Path.Combine(apiDirectory, "Api.csproj");

        var testsProject = Path.Combine(
            testsDirectory,
            "DatabaseAnalyzer.Tests.csproj");

        await File.WriteAllTextAsync(infrastructureProject, "<Project />");
        await File.WriteAllTextAsync
        (
            apiProject,
            """
            <Project>
              <ItemGroup>
                <ProjectReference Include="../Infrastructure/Infrastructure.csproj" />
              </ItemGroup>
            </Project>
            """
        );
        await File.WriteAllTextAsync(testsProject, "<Project />");

        await File.WriteAllTextAsync(
            Path.Combine(infrastructureDirectory, "AppDbContext.cs"),
            "public class AppDbContext : Microsoft.EntityFrameworkCore.DbContext { }");

        await File.WriteAllTextAsync(
            Path.Combine(testsDirectory, "TestDbContext.cs"),
            "public class TestDbContext : Microsoft.EntityFrameworkCore.DbContext { }");

        var resolver = new EfCoreDatabaseProjectResolver();

        var context = new AnalyzerContext
        {
            RepositoryPath = repositoryPath,
            CommitHash = "test"
        };

        var result = await resolver.ResolveAsync(context);

        Assert.NotNull(result);
        Assert.Equal(
            Path.GetFullPath(infrastructureProject),
            result.ContextProjectPath);
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrow_WhenMultipleProductionProjectsDeclareDbContext()
    {
        var repositoryPath = CreateRepository();

        var firstDirectory = Path.Combine(repositoryPath, "src", "First");
        var secondDirectory = Path.Combine(repositoryPath, "src", "Second");

        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);

        var firstProject = Path.Combine(firstDirectory, "First.csproj");
        var secondProject = Path.Combine(secondDirectory, "Second.csproj");

        await File.WriteAllTextAsync(firstProject, "<Project />");
        await File.WriteAllTextAsync(secondProject, "<Project />");

        await File.WriteAllTextAsync(
            Path.Combine(firstDirectory, "FirstDbContext.cs"),
            "public class FirstDbContext : Microsoft.EntityFrameworkCore.DbContext { }");

        await File.WriteAllTextAsync(
            Path.Combine(secondDirectory, "SecondDbContext.cs"),
            "public class SecondDbContext : Microsoft.EntityFrameworkCore.DbContext { }");

        var resolver = new EfCoreDatabaseProjectResolver();

        var context = new AnalyzerContext
        {
            RepositoryPath = repositoryPath,
            CommitHash = "test"
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(context));

        Assert.Contains(
            "Multiple production projects declaring DbContext",
            exception.Message);

        Assert.Contains("First.csproj", exception.Message);
        Assert.Contains("Second.csproj", exception.Message);
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

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(context));
    }

    private static string CreateRepository()
    {
        return Directory.CreateTempSubdirectory("database-analyzer-").FullName;
    }
}
