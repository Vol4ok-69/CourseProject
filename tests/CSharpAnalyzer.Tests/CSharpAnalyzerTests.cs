using Application.Analyzers;
using Xunit;

namespace CSharpAnalyzer.Tests;

public class CSharpAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_ShouldAnalyzeRepositoryFiles()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        Directory.CreateDirectory(repositoryPath);

        try
        {
            var source = """
            public class TestClass
            {
                public void TestMethod()
                {
                    var value = 1;
                }
            }
            """;

            await File.WriteAllTextAsync(
                Path.Combine(repositoryPath, "TestClass.cs"),
                source);

            var analyzer = new CSharp.CSharpAnalyzer();

            var context = new AnalyzerContext
            {
                RepositoryPath = repositoryPath,
                CommitHash = "test-commit"
            };

            var findings = await analyzer.AnalyzeAsync(context);

            Assert.Empty(findings);
        }
        finally
        {
            Directory.Delete(repositoryPath, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldReturnFindingsFromRepository()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        Directory.CreateDirectory(repositoryPath);

        try
        {
            var body = string.Join(
                Environment.NewLine,
                Enumerable.Repeat("        var value = 1;", 51));

            var source = $$"""
            public class TestClass
            {
                public void TestMethod()
                {
            {{body}}
                }
            }
            """;

            await File.WriteAllTextAsync(
                Path.Combine(repositoryPath, "TestClass.cs"),
                source);

            var analyzer = new CSharp.CSharpAnalyzer();

            var context = new AnalyzerContext
            {
                RepositoryPath = repositoryPath,
                CommitHash = "test-commit"
            };

            var findings = await analyzer.AnalyzeAsync(context);

            Assert.Single(findings);
            Assert.Equal("CSHARP002", findings[0].Rule);
            Assert.Equal("TestClass.cs", Path.GetFileName(findings[0].FilePath));
        }
        finally
        {
            Directory.Delete(repositoryPath, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldThrowWhenRepositoryDoesNotExist()
    {
        var analyzer = new CSharp.CSharpAnalyzer();

        var context = new AnalyzerContext
        {
            RepositoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()),
            CommitHash = "test-commit"
        };

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => analyzer.AnalyzeAsync(context));
    }
}