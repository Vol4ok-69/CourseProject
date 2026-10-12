
using Application.Analyzers;
using CSharp.Rules;
using Domain.Enums;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharp;

public sealed class CSharpAnalyzer : IAnalyzer
{
    private static readonly HashSet<string> IgnoredDirectoryNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            ".vs",
            ".idea",
            ".vscode",
            "bin",
            "obj",
            "packages",
            "node_modules",
            "TestResults",
            "artifacts"
        };

    private readonly LargeClassRule _largeClassRule = new();
    private readonly LongMethodRule _longMethodRule = new();

    public AnalyzerType Type => AnalyzerType.CSharp;

    public async Task<IReadOnlyList<AnalyzerFinding>> AnalyzeAsync(
        AnalyzerContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.RepositoryPath))
        {
            throw new ArgumentException(
                "Repository path must not be empty.",
                nameof(context));
        }

        var repositoryPath = Path.GetFullPath(context.RepositoryPath);

        if (!Directory.Exists(repositoryPath))
        {
            throw new DirectoryNotFoundException(
                $"Repository path not found: {repositoryPath}");
        }

        var findings = new List<AnalyzerFinding>();

        foreach (var file in EnumerateSourceFiles(
                     repositoryPath,
                     cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceCode = await File.ReadAllTextAsync(
                file,
                cancellationToken);

            var tree = CSharpSyntaxTree.ParseText(
                sourceCode,
                cancellationToken: cancellationToken);

            var root = await tree.GetRootAsync(cancellationToken);

            var relativePath = Path.GetRelativePath(repositoryPath, file);

            findings.AddRange(_largeClassRule.Analyze(root, relativePath));
            findings.AddRange(_longMethodRule.Analyze(root, relativePath));
        }

        return findings;
    }

    private static IEnumerable<string> EnumerateSourceFiles(
        string directory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var file in Directory.EnumerateFiles(
                     directory,
                     "*.cs",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }

        foreach (var subdirectory in Directory.EnumerateDirectories(
                     directory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directoryName = Path.GetFileName(subdirectory);

            if (IgnoredDirectoryNames.Contains(directoryName))
            {
                continue;
            }

            var attributes = File.GetAttributes(subdirectory);

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            foreach (var file in EnumerateSourceFiles(
                         subdirectory,
                         cancellationToken))
            {
                yield return file;
            }
        }
    }
}
