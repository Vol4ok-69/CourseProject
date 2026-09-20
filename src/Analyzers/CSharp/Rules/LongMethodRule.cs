using Application.Analyzers;
using Domain.Enums;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharp.Rules;

public class LongMethodRule
{
    private const int MaxLines = 50;

    public IReadOnlyList<AnalyzerFinding> Analyze(SyntaxNode root, string filePath)
    {
        var findings = new List<AnalyzerFinding>();

        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();

        foreach (var method in methods)
        {
            var lineSpan = method.GetLocation().GetLineSpan();
            var startLine = lineSpan.StartLinePosition.Line + 1;
            var endLine = lineSpan.EndLinePosition.Line + 1;
            var lineCount = endLine - startLine + 1;

            if (lineCount <= MaxLines)
            {
                continue;
            }

            findings.Add(new AnalyzerFinding
            {
                FilePath = filePath,
                LineNumber = startLine,
                Rule = "CSHARP002",
                Message = $"Метод '{method.Identifier.Text}' содержит {lineCount} строк.",
                Severity = Severity.Warning,
                Recommendation = $"Разделить метод '{method.Identifier.Text}' на несколько методов с более узкой ответственностью."
            });
        }

        return findings;
    }
}