using Application.Analyzers;

namespace SecurityAnalyzer.Rules;

public interface IConfigSecurityRule
{
    IReadOnlyList<AnalyzerFinding> Analyze(string jsonContent, string filePath);
}
