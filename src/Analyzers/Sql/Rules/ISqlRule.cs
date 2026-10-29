using Application.Analyzers;
using Microsoft.CodeAnalysis;

namespace SqlAnalyzer.Rules;

public interface ISqlRule
{
    IReadOnlyList<AnalyzerFinding> Analyze(SyntaxNode root, string filePath);
}
