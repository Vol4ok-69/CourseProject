using Application.Analyzers;
using Domain.Enums;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharp.Rules;

public class LargeClassRule
{
    private const int MaxLines = 500;

    public IReadOnlyList<AnalyzerFinding> Analyze(SyntaxNode root, string filePath)
    {
        var findings = new List<AnalyzerFinding>();

        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classDeclaration in classes)
        {
            var lineSpan = classDeclaration.GetLocation().GetLineSpan();
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
                Rule = "CSHARP001",
                Message = $"Класс '{classDeclaration.Identifier.Text}' содержит {lineCount} строк.",
                Severity = Severity.Warning,
                Recommendation = $"Разделить класс '{classDeclaration.Identifier.Text}' на несколько классов с более узкой ответственностью."
            });
        }

        return findings;
    }
}