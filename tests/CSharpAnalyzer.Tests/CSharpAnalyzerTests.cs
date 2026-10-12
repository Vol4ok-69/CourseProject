using Application.Analyzers;
using Xunit;

namespace CSharpAnalyzer.Tests;

public class CSharpAnalyzerTests
{

    [Fact]
    public async Task AnalyzeAsync_ShouldIgnoreGeneratedDirectories()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        var sourceDirectory = Path.Combine(repositoryPath, "src");
        var generatedDirectory = Path.Combine(repositoryPath, "obj");

        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(generatedDirectory);

        try
        {
            await File.WriteAllTextAsync
            (
                Path.Combine(sourceDirectory, "NormalClass.cs"),
                """
                public class NormalClass
                {
                    public void Execute()
                    {
                        var value = 1;
                    }
                }
                """
            );

            var generatedMethodBody = string.Join(
                Environment.NewLine,
                Enumerable.Repeat("        var value = 1;", 51));

            await File.WriteAllTextAsync(
                Path.Combine(generatedDirectory, "GeneratedClass.cs"),
                $$"""
            public class GeneratedClass
            {
                public void Execute()
                {
            {{generatedMethodBody}}
                }
            }
            """);

            var analyzer = new CSharp.CSharpAnalyzer();

            var context = new AnalyzerContext
            {
                RepositoryPath = repositoryPath,
                CommitHash = "test-commit"
            };

            var findings = await analyzer.AnalyzeAsync(context);

            Assert.DoesNotContain(
                findings,
                finding => finding.FilePath.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase));

            Assert.Empty(findings);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }


    [Fact]
    public async Task AnalyzeAsync_ShouldReturnRelativeFilePaths()
    {
        var repositoryPath = Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString());

        var sourceDirectory = Path.Combine(repositoryPath, "src");
        Directory.CreateDirectory(sourceDirectory);

        try
        {
            var methodBody = string.Join(
                Environment.NewLine,
                Enumerable.Repeat("        var value = 1;", 51));

            await File.WriteAllTextAsync
            (
                Path.Combine(sourceDirectory, "LongMethod.cs"),
                $$"""
                    public class LongMethod
                    {
                        public void Execute()
                        {
                    {{methodBody}}
                        }
                    }
                 """
            );

            var analyzer = new CSharp.CSharpAnalyzer();

            var context = new AnalyzerContext
            {
                RepositoryPath = repositoryPath,
                CommitHash = "test-commit"
            };

            var findings = await analyzer.AnalyzeAsync(context);

            var finding = Assert.Single(findings);

            Assert.Equal("CSHARP002", finding.Rule);
            Assert.Equal(
                Path.Combine("src", "LongMethod.cs"),
                finding.FilePath);

            Assert.False(Path.IsPathRooted(finding.FilePath));
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }
}
