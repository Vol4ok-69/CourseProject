using Application.Analyzers;
using CSharp.Rules;
using Domain.Enums;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharp;

public class CSharpAnalyzer : IAnalyzer
{
    private readonly LargeClassRule _largeClassRule = new();
    private readonly LongMethodRule _longMethodRule = new();

    public AnalyzerType Type => AnalyzerType.CSharp;

    public async Task<IReadOnlyList<AnalyzerFinding>> AnalyzeAsync(AnalyzerContext context, CancellationToken cancellationToken = default)
    {
        var findings = new List<AnalyzerFinding>();

        if (!Directory.Exists(context.RepositoryPath))
        {
            throw new DirectoryNotFoundException($"Repository path not found: {context.RepositoryPath}");
        }

        var files = Directory.EnumerateFiles(context.RepositoryPath, "*.cs", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceCode = await File.ReadAllTextAsync(file, cancellationToken);
            var tree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: cancellationToken);
            var root = await tree.GetRootAsync(cancellationToken);

            findings.AddRange(_largeClassRule.Analyze(root, file));
            findings.AddRange(_longMethodRule.Analyze(root, file));
        }

        return findings;
    }
}