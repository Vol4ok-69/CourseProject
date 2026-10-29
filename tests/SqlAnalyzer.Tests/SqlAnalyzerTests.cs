using Application.Analyzers;
using Domain.Enums;
using Xunit;

namespace SqlAnalyzer.Tests;

public sealed class SqlAnalyzerTests
{
    [Fact]
    public void Type_ShouldBeSql()
    {
        var analyzer = new SqlAnalyzer();
        Assert.Equal(AnalyzerType.Sql, analyzer.Type);
    }

    [Fact]
    public async Task AnalyzeAsync_NullContext_ThrowsArgumentNullException()
    {
        var analyzer = new SqlAnalyzer();
        await Assert.ThrowsAsync<ArgumentNullException>(() => analyzer.AnalyzeAsync(null!));
    }

    [Fact]
    public async Task AnalyzeAsync_EmptyRepositoryPath_ThrowsArgumentException()
    {
        var analyzer = new SqlAnalyzer();
        var context = new AnalyzerContext { RepositoryPath = "   ", CommitHash = "test" };

        await Assert.ThrowsAsync<ArgumentException>(() => analyzer.AnalyzeAsync(context));
    }

    [Fact]
    public async Task AnalyzeAsync_NonExistentDirectory_ThrowsDirectoryNotFoundException()
    {
        var analyzer = new SqlAnalyzer();
        var context = new AnalyzerContext
        {
            RepositoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()),
            CommitHash = "test"
        };

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => analyzer.AnalyzeAsync(context));
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldIgnoreGeneratedDirectories()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var srcDir = Path.Combine(repositoryPath, "src");
        var objDir = Path.Combine(repositoryPath, "obj");

        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(objDir);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(srcDir, "ValidService.cs"),
                """
                public class ValidService
                {
                    public void Run(AppDbContext db, string name)
                    {
                        var users = db.Users.FromSqlRaw($"SELECT * FROM Users WHERE Name = '{name}'").ToList();
                    }
                }
                """);

            await File.WriteAllTextAsync(
                Path.Combine(objDir, "GeneratedService.cs"),
                """
                public class GeneratedService
                {
                    public void Run(AppDbContext db, string name)
                    {
                        var users = db.Users.FromSqlRaw($"SELECT * FROM Users WHERE Name = '{name}'").ToList();
                    }
                }
                """);

            var analyzer = new SqlAnalyzer();
            var context = new AnalyzerContext { RepositoryPath = repositoryPath, CommitHash = "test" };

            var findings = await analyzer.AnalyzeAsync(context);

            Assert.All(findings, f => Assert.StartsWith("src", f.FilePath));
            Assert.Contains(findings, f => f.Rule == "SQL001");
            Assert.Contains(findings, f => f.Rule == "SQL002");
        }
        finally
        {
            if (Directory.Exists(repositoryPath))
            {
                Directory.Delete(repositoryPath, true);
            }
        }
    }

    [Fact]
    public async Task AnalyzeAsync_WithSyntaxErrorsInFile_DoesNotCrashAndAnalyzesRemainingFiles()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var srcDir = Path.Combine(repositoryPath, "src");

        Directory.CreateDirectory(srcDir);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(srcDir, "BrokenSyntax.cs"),
                """
                public class BrokenSyntax {
                    public void Incomplete(
                """);

            await File.WriteAllTextAsync(
                Path.Combine(srcDir, "GoodService.cs"),
                """
                public class GoodService
                {
                    public void Run(AppDbContext db)
                    {
                        var users = db.Users.FromSqlRaw("SELECT * FROM Users").ToList();
                    }
                }
                """);

            var analyzer = new SqlAnalyzer();
            var context = new AnalyzerContext { RepositoryPath = repositoryPath, CommitHash = "test" };

            var findings = await analyzer.AnalyzeAsync(context);

            Assert.Single(findings);
            Assert.Equal("SQL002", findings[0].Rule);
            Assert.Equal(Path.Combine("src", "GoodService.cs"), findings[0].FilePath);
        }
        finally
        {
            if (Directory.Exists(repositoryPath))
            {
                Directory.Delete(repositoryPath, true);
            }
        }
    }
}
