using Application.Analyzers;
using Domain.Enums;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SqlAnalyzer.Rules;

namespace SqlAnalyzer;

public sealed class SqlAnalyzer : IAnalyzer
{
    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
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

    private readonly ISqlRule[] _rules =
    [
        new Sql001UnsafeQueryRule(),
        new Sql002SelectStarRule()
    ];

    public AnalyzerType Type => AnalyzerType.Sql;

    public async Task<IReadOnlyList<AnalyzerFinding>> AnalyzeAsync(
        AnalyzerContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.RepositoryPath))
        {
            throw new ArgumentException("Repository path must not be empty.", nameof(context));
        }

        var repositoryPath = Path.GetFullPath(context.RepositoryPath);

        if (!Directory.Exists(repositoryPath))
        {
            throw new DirectoryNotFoundException($"Repository path not found: {repositoryPath}");
        }

        var findings = new List<AnalyzerFinding>();

        foreach (var file in EnumerateSourceFiles(repositoryPath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceCode = await File.ReadAllTextAsync(file, cancellationToken);
            var relativePath = Path.GetRelativePath(repositoryPath, file);

            try
            {
                var tree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: cancellationToken);
                var root = await tree.GetRootAsync(cancellationToken);

                foreach (var rule in _rules)
                {
                    findings.AddRange(rule.Analyze(root, relativePath));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Не останавливаем анализ всего репозитория из-за сбоя разбора отдельного файла
            }
        }

        return findings;
    }

    private static IEnumerable<string> EnumerateSourceFiles(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }

        foreach (var subdirectory in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
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

            foreach (var file in EnumerateSourceFiles(subdirectory, cancellationToken))
            {
                yield return file;
            }
        }
    }
}
