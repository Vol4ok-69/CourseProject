using Application.Analyzers;
using Microsoft.CodeAnalysis;

namespace SecurityAnalyzer.Rules;

public interface ISecurityRule
{
    IReadOnlyList<AnalyzerFinding> Analyze(SyntaxNode root, string filePath);
}
