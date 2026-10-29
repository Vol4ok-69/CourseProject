using Application.Analyzers;
using Domain.Enums;
using Xunit;

namespace SecurityAnalyzer.Tests;

public sealed class SecurityAnalyzerTests
{
    [Fact]
    public void Type_ShouldBeSecurity()
    {
        var analyzer = new SecurityAnalyzer();
        Assert.Equal(AnalyzerType.Security, analyzer.Type);
    }

    [Fact]
    public async Task AnalyzeAsync_NullContext_ThrowsArgumentNullException()
    {
        var analyzer = new SecurityAnalyzer();
        await Assert.ThrowsAsync<ArgumentNullException>(() => analyzer.AnalyzeAsync(null!));
    }

    [Fact]
    public async Task AnalyzeAsync_EmptyRepositoryPath_ThrowsArgumentException()
    {
        var analyzer = new SecurityAnalyzer();
        var context = new AnalyzerContext { RepositoryPath = "   ", CommitHash = "test" };

        await Assert.ThrowsAsync<ArgumentException>(() => analyzer.AnalyzeAsync(context));
    }

    [Fact]
    public async Task AnalyzeAsync_NonExistentDirectory_ThrowsDirectoryNotFoundException()
    {
        var analyzer = new SecurityAnalyzer();
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
        var binDir = Path.Combine(repositoryPath, "bin");

        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(binDir);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(srcDir, "ValidService.cs"),
                """
                public class ValidService
                {
                    private string _apiKey = "sk_live_1234567890abcdef";
                }
                """);

            await File.WriteAllTextAsync(
                Path.Combine(binDir, "IgnoredService.cs"),
                """
                public class IgnoredService
                {
                    private string _apiKey = "sk_live_9999999999abcdef";
                }
                """);

            var analyzer = new SecurityAnalyzer();
            var context = new AnalyzerContext { RepositoryPath = repositoryPath, CommitHash = "test" };

            var findings = await analyzer.AnalyzeAsync(context);

            Assert.Single(findings);
            Assert.Equal(Path.Combine("src", "ValidService.cs"), findings[0].FilePath);
            Assert.Equal("SEC001", findings[0].Rule);
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
                    public void Compute()
                    {
                        using var md5 = MD5.Create();
                    }
                }
                """);

            var analyzer = new SecurityAnalyzer();
            var context = new AnalyzerContext { RepositoryPath = repositoryPath, CommitHash = "test" };

            var findings = await analyzer.AnalyzeAsync(context);

            Assert.Single(findings);
            Assert.Equal("SEC004", findings[0].Rule);
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

    [Fact]
    public async Task AnalyzeAsync_WithBothCSharpAndJsonSecrets_FindsBoth()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var srcDir = Path.Combine(repositoryPath, "src");

        Directory.CreateDirectory(srcDir);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(srcDir, "Program.cs"),
                """
                public class Program
                {
                    public static void Main()
                    {
                        var handler = new HttpClientHandler
                        {
                            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                        };
                    }
                }
                """);

            await File.WriteAllTextAsync(
                Path.Combine(repositoryPath, "appsettings.json"),
                """
                {
                  "ConnectionStrings": {
                    "DefaultConnection": "Host=prod.db;Database=app;Username=admin;Password=RealProductionPassword123!;"
                  }
                }
                """);

            var analyzer = new SecurityAnalyzer();
            var context = new AnalyzerContext { RepositoryPath = repositoryPath, CommitHash = "test" };

            var findings = await analyzer.AnalyzeAsync(context);

            Assert.Equal(2, findings.Count);
            Assert.Contains(findings, f => f.Rule == "SEC005");
            Assert.Contains(findings, f => f.Rule == "SEC002");
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
    public async Task AnalyzeAsync_WithCancellationToken_CancelsPromptly()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(repositoryPath);

        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var analyzer = new SecurityAnalyzer();
            var context = new AnalyzerContext { RepositoryPath = repositoryPath, CommitHash = "test" };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analyzer.AnalyzeAsync(context, cts.Token));
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
