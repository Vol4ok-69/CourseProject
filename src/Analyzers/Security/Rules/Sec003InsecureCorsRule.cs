using Application.Analyzers;
using Domain.Enums;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SecurityAnalyzer.Rules;

/// <summary>
/// SEC003: Обнаружение чрезмерно открытых политик CORS.
/// Ограничение синтаксического анализа: проверка выполняется по именам методов 'AllowAnyOrigin' и 'SetIsOriginAllowed'
/// синтаксического дерева Roslyn без необходимости полной компиляции проекта через MSBuildWorkspace.
/// </summary>
public sealed class Sec003InsecureCorsRule : ISecurityRule
{
    public IReadOnlyList<AnalyzerFinding> Analyze(SyntaxNode root, string filePath)
    {
        var findings = new List<AnalyzerFinding>();

        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var methodName = memberAccess.Name.Identifier.Text;

                // 1. Проверка метода AllowAnyOrigin()
                if (methodName == "AllowAnyOrigin")
                {
                    var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    findings.Add(new AnalyzerFinding
                    {
                        FilePath = filePath,
                        LineNumber = line,
                        Rule = "SEC003",
                        Message = "Обнаружена чрезмерно открытая политика CORS: вызов 'AllowAnyOrigin()' разрешает межсайтовые запросы с любых источников.",
                        Severity = Severity.Warning,
                        Recommendation = "Рекомендуется ограничить разрешённые домены через метод 'WithOrigins(...)', указав явный список доверенных клиентских адресов, чтобы снизить риски несанкционированного доступа через сторонние веб-сайты."
                    });
                }
                // 2. Проверка метода SetIsOriginAllowed(_ => true)
                else if (methodName == "SetIsOriginAllowed")
                {
                    var firstArg = invocation.ArgumentList.Arguments.FirstOrDefault();
                    if (firstArg?.Expression != null && IsUnconditionallyTrueLambda(firstArg.Expression))
                    {
                        var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        findings.Add(new AnalyzerFinding
                        {
                            FilePath = filePath,
                            LineNumber = line,
                            Rule = "SEC003",
                            Message = "Обнаружена чрезмерно открытая политика CORS: 'SetIsOriginAllowed' безусловно возвращает true для любого источника.",
                            Severity = Severity.Warning,
                            Recommendation = "Рекомендуется проверять источник по списку доверенных адресов вместо безусловного возврата true, чтобы ограничить доступ только авторизованным клиентам."
                        });
                    }
                }
            }
        }

        return findings;
    }

    private static bool IsUnconditionallyTrueLambda(ExpressionSyntax expression)
    {
        if (expression is SimpleLambdaExpressionSyntax simpleLambda)
        {
            return IsBodyUnconditionallyTrue(simpleLambda.Body);
        }

        if (expression is ParenthesizedLambdaExpressionSyntax parenLambda)
        {
            return IsBodyUnconditionallyTrue(parenLambda.Body);
        }

        return false;
    }

    private static bool IsBodyUnconditionallyTrue(CSharpSyntaxNode body)
    {
        // 1. Выражение вида: _ => true
        if (body is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.TrueLiteralExpression))
        {
            return true;
        }

        // 2. Блок вида: { return true; }
        if (body is BlockSyntax block)
        {
            // Если в блоке есть ветвления, условные операторы или тернарные выражения — это условная лямбда
            if (HasBranching(block))
            {
                return false;
            }

            // Должен быть ровно один оператор return, и он должен возвращать литерал true
            var returnStatements = block.DescendantNodes().OfType<ReturnStatementSyntax>().ToList();
            if (returnStatements.Count == 1 &&
                returnStatements[0].Expression is LiteralExpressionSyntax retLit &&
                retLit.IsKind(SyntaxKind.TrueLiteralExpression))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasBranching(BlockSyntax block)
    {
        return block.DescendantNodes().Any(node =>
            node is IfStatementSyntax or
                    ConditionalExpressionSyntax or
                    SwitchStatementSyntax or
                    SwitchExpressionSyntax or
                    TryStatementSyntax or
                    WhileStatementSyntax or
                    ForStatementSyntax or
                    ForEachStatementSyntax);
    }
}
