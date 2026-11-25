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
    private const string CustomValidationPropertyName = "ServerCertificateCustomValidationCallback";
    private const string ServicePointValidationPropertyName = "ServerCertificateValidationCallback";
    private const string ServicePointManagerTypeName = "ServicePointManager";
    private const string DangerousValidatorName = "DangerousAcceptAnyServerCertificateValidator";

    public IReadOnlyList<AnalyzerFinding> Analyze(SyntaxNode root, string filePath)
    {
        var findings = new List<AnalyzerFinding>();

        var assignments = root.DescendantNodes().OfType<AssignmentExpressionSyntax>();

        foreach (var assignment in assignments)
        {
            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                !assignment.IsKind(SyntaxKind.AddAssignmentExpression))
            {
                continue;
            }

            var propertyName = GetTargetPropertyName(assignment);
            if (propertyName is null)
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
                    Message = $"Отключена валидация сертификатов SSL/TLS в свойстве '{propertyName}'. Приложение уязвимо к атакам типа Man-in-the-Middle (MITM).",
                    Severity = Severity.Critical,
                    Recommendation = "Не отключайте проверку сертификатов в продакшен-коде. Для локального тестирования добавьте самоподписанный сертификат в доверенное хранилище корневых сертификатов ОС или настройте выборочную проверку отпечатка (thumbprint) доверенного сертификата."
                });
            }
        }

        return findings;
    }

    private static bool CanUseUnqualifiedServicePointManager(AssignmentExpressionSyntax assignment)
    {
        foreach (var ancestor in assignment.Ancestors())
        {
            SyntaxList<UsingDirectiveSyntax> usings = ancestor switch
            {
                BaseNamespaceDeclarationSyntax ns => ns.Usings,
                CompilationUnitSyntax cu => cu.Usings,
                _ => default
            };

            if (usings.Count == 0)
            {
                continue;
            }

            var matchingAlias = usings.FirstOrDefault(u =>
                u.Alias is not null &&
                u.Alias.Name.Identifier.Text == ServicePointManagerTypeName);

            if (matchingAlias is not null)
            {
                return IsSystemServicePointManagerAlias(matchingAlias);
            }
        }

        return true;
    }

    private static bool IsSystemServicePointManagerAlias(UsingDirectiveSyntax usingDirective)
    {
        if (usingDirective.NamespaceOrType is not QualifiedNameSyntax spmName ||
            spmName.Right.Identifier.Text != ServicePointManagerTypeName)
        {
            return false;
        }

        if (spmName.Left is not QualifiedNameSyntax netName ||
            netName.Right.Identifier.Text != "Net")
        {
            return false;
        }

        return netName.Left switch
        {
            IdentifierNameSyntax sysId => sysId.Identifier.Text == "System",
            AliasQualifiedNameSyntax alias => alias.Alias.Identifier.Text == "global" &&
                                              alias.Name.Identifier.Text == "System",
            _ => false
        };
    }

    private static string? GetTargetPropertyName(AssignmentExpressionSyntax assignment)
    {
        var left = assignment.Left;
        var rawName = left switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            _ => null
        };

        if (rawName == CustomValidationPropertyName)
        {
            return CustomValidationPropertyName;
        }

        if (rawName == ServicePointValidationPropertyName &&
            left is MemberAccessExpressionSyntax spmMemberAccess &&
            IsServicePointManagerExpression(spmMemberAccess.Expression, assignment))
        {
            return ServicePointValidationPropertyName;
        }

        return null;
    }

    private static bool IsServicePointManagerExpression(ExpressionSyntax expression, AssignmentExpressionSyntax assignment)
    {
        if (expression is IdentifierNameSyntax identifier)
        {
            return identifier.Identifier.Text == ServicePointManagerTypeName &&
                   CanUseUnqualifiedServicePointManager(assignment);
        }

        if (expression is MemberAccessExpressionSyntax typeAccess &&
            typeAccess.Name.Identifier.Text == ServicePointManagerTypeName)
        {
            return IsSystemNetNamespace(typeAccess.Expression);
        }

        return false;
    }

    private static bool IsSystemNetNamespace(ExpressionSyntax expression)
    {
        if (expression is not MemberAccessExpressionSyntax netAccess ||
            netAccess.Name.Identifier.Text != "Net")
        {
            return false;
        }

        return netAccess.Expression switch
        {
            IdentifierNameSyntax sysId => sysId.Identifier.Text == "System",
            AliasQualifiedNameSyntax alias => alias.Alias.Identifier.Text == "global" &&
                                              alias.Name.Identifier.Text == "System",
            _ => false
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
