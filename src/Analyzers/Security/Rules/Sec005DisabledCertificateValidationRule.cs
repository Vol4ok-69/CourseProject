using Application.Analyzers;
using Domain.Enums;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SecurityAnalyzer.Rules;

/// <summary>
/// SEC005: Обнаружение отключения проверки валидности сертификатов SSL/TLS.
/// Проверяет присваивание DangerousAcceptAnyServerCertificateValidator или лямбд, безусловно возвращающих true.
/// Условные проверки сертификатов (например, по отпечатку или SslPolicyErrors) не считаются нарушением.
/// </summary>
public sealed class Sec005DisabledCertificateValidationRule : ISecurityRule
{
    private const string TargetPropertyName = "ServerCertificateCustomValidationCallback";
    private const string DangerousValidatorName = "DangerousAcceptAnyServerCertificateValidator";

    public IReadOnlyList<AnalyzerFinding> Analyze(SyntaxNode root, string filePath)
    {
        var findings = new List<AnalyzerFinding>();

        var assignments = root.DescendantNodes().OfType<AssignmentExpressionSyntax>();

        foreach (var assignment in assignments)
        {
            var propertyName = GetTargetPropertyName(assignment.Left);
            if (propertyName != TargetPropertyName)
            {
                continue;
            }

            if (IsDangerousValidationExpression(assignment.Right))
            {
                var line = assignment.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                findings.Add(new AnalyzerFinding
                {
                    FilePath = filePath,
                    LineNumber = line,
                    Rule = "SEC005",
                    Message = "Отключена валидация сертификатов SSL/TLS в свойстве 'ServerCertificateCustomValidationCallback'. Приложение уязвимо к атакам типа Man-in-the-Middle (MITM).",
                    Severity = Severity.Critical,
                    Recommendation = "Не отключайте проверку сертификатов в продакшен-коде. Для локального тестирования добавьте самоподписанный сертификат в доверенное хранилище корневых сертификатов ОС или настройте выборочную проверку отпечатка (thumbprint) доверенного сертификата."
                });
            }
        }

        return findings;
    }

    private static string? GetTargetPropertyName(ExpressionSyntax left)
    {
        return left switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            _ => null
        };
    }

    private static bool IsDangerousValidationExpression(ExpressionSyntax right)
    {
        // 1. Присваивание HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        if (right is MemberAccessExpressionSyntax memberAccess &&
            memberAccess.Name.Identifier.Text == DangerousValidatorName)
        {
            return true;
        }

        // 2. Присваивание лямбды, безусловно возвращающей true: _ => true
        if (right is SimpleLambdaExpressionSyntax simpleLambda)
        {
            return IsBodyUnconditionallyTrue(simpleLambda.Body);
        }

        if (right is ParenthesizedLambdaExpressionSyntax parenLambda)
        {
            return IsBodyUnconditionallyTrue(parenLambda.Body);
        }

        return false;
    }

    private static bool IsBodyUnconditionallyTrue(CSharpSyntaxNode body)
    {
        // 1. Выражение вида: (s, c, ch, e) => true
        if (body is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.TrueLiteralExpression))
        {
            return true;
        }

        // 2. Блок вида: { return true; }
        if (body is BlockSyntax block)
        {
            // Если в блоке присутствуют ветвления (if, switch, try, ternary) — это условная валидация
            if (HasBranching(block))
            {
                return false;
            }

            // Должен быть ровно один return, возвращающий литерал true
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
