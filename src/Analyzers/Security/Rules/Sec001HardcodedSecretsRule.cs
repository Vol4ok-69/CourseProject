using Application.Analyzers;
using Domain.Enums;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;

namespace SecurityAnalyzer.Rules;

/// <summary>
/// SEC001: Поиск захардкоженных секретов и паролей в исходном коде C#.
/// Значение обнаруженного секрета никогда не выводится в диагностических сообщениях во избежание утечки.
/// </summary>
public sealed class Sec001HardcodedSecretsRule : ISecurityRule
{
    private static readonly Regex PemPrivateKeyRegex = new(
        @"-----BEGIN\s+(?:RSA\s+|EC\s+|OPENSSH\s+|DSA\s+)?PRIVATE\s+KEY-----",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ConnectionStringPasswordRegex = new(
        @"(?:(?:Host|Server|Data\s+Source|Database)\s*=.*?;.*?\b(?:Password|Pwd)\s*=\s*(?<pwd>[^;""'\s]+)|\b(?:Password|Pwd)\s*=\s*(?<pwd>[^;""'\s]+)\s*;.*?\b(?:Host|Server|Data\s+Source|Database)\s*=)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SensitiveIdentifierRegex = new(
        @"(?i)(password|passwd|pwd|secret|api_?key|jwt_?secret|access_?token|auth_?token|client_?secret|private_?key)",
        RegexOptions.Compiled);

    private static readonly Regex ExcludedIdentifierSuffixRegex = new(
        @"(?i)(hash|salt|hasher|regex|pattern|length|policy|expiry|expiration|format|claim|type|header|name|field|param|attribute|column|keyname|validator)$",
        RegexOptions.Compiled);

    // Только явные шаблоны-заполнители. Распространённые слабые пароли (123456, admin, password) намеренно НЕ входят сюда, так как являются реальными уязвимостями.
    private static readonly HashSet<string> PlaceholderValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "TODO",
        "CHANGEME",
        "CHANGE_ME",
        "REPLACE_ME",
        "YOUR_PASSWORD",
        "YOUR_SECRET",
        "YOUR_KEY",
        "YOUR_API_KEY",
        "PLACEHOLDER",
        "***"
    };

    public IReadOnlyList<AnalyzerFinding> Analyze(SyntaxNode root, string filePath)
    {
        var findings = new List<AnalyzerFinding>();
        var reportedLines = new HashSet<int>();

        // 1. Поиск PEM-приватных ключей во всех строковых литералах
        var stringLiterals = root.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression));

        foreach (var literal in stringLiterals)
        {
            var value = literal.Token.ValueText;

            if (PemPrivateKeyRegex.IsMatch(value))
            {
                var line = literal.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                if (reportedLines.Add(line))
                {
                    findings.Add(new AnalyzerFinding
                    {
                        FilePath = filePath,
                        LineNumber = line,
                        Rule = "SEC001",
                        Message = "Обнаружен захардкоженный закрытый криптографический ключ в формате PEM (значение скрыто во избежание утечки).",
                        Severity = Severity.Critical,
                        Recommendation = "Никогда не храните закрытые ключи в исходном коде репозитория. Используйте специализированное хранилище секретов (Azure Key Vault, HashiCorp Vault) или переменные окружения."
                    });
                }
                continue;
            }

            // 2. Поиск строк подключения с открытым паролем
            var connMatch = ConnectionStringPasswordRegex.Match(value);
            if (connMatch.Success)
            {
                var pwd = connMatch.Groups["pwd"].Value.Trim();
                if (IsRealSecretValue(pwd))
                {
                    var line = literal.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    if (reportedLines.Add(line))
                    {
                        findings.Add(new AnalyzerFinding
                        {
                            FilePath = filePath,
                            LineNumber = line,
                            Rule = "SEC001",
                            Message = "Обнаружена строка подключения с открытым паролем в исходном коде (пароль скрыт).",
                            Severity = Severity.Warning,
                            Recommendation = "Вынесите строку подключения в защищённую конфигурацию (User Secrets, переменные окружения или Secret Manager), не сохраняя пароли в коде."
                        });
                    }
                }
            }
        }

        // 3. Поиск инициализаторов переменных и полей с чувствительными именами
        var variableDeclarators = root.DescendantNodes().OfType<VariableDeclaratorSyntax>();
        foreach (var declarator in variableDeclarators)
        {
            var identifier = declarator.Identifier.Text;
            if (!IsSensitiveIdentifier(identifier))
            {
                continue;
            }

            if (declarator.Initializer?.Value is ExpressionSyntax initExpr)
            {
                var stringValue = ExtractStaticString(initExpr);
                if (stringValue != null && IsRealSecretValue(stringValue))
                {
                    var line = declarator.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    if (reportedLines.Add(line))
                    {
                        findings.Add(new AnalyzerFinding
                        {
                            FilePath = filePath,
                            LineNumber = line,
                            Rule = "SEC001",
                            Message = $"Обнаружен захардкоженный секрет или пароль в переменной/поле '{identifier}' (значение скрыто во избежание утечки).",
                            Severity = Severity.Warning,
                            Recommendation = "Вынесите секретные значения, токены и пароли в защищённую конфигурацию, переменные окружения или хранилище секретов."
                        });
                    }
                }
            }
        }

        // 4. Поиск инициализаторов свойств с чувствительными именами
        var propertyDeclarations = root.DescendantNodes().OfType<PropertyDeclarationSyntax>();
        foreach (var property in propertyDeclarations)
        {
            var identifier = property.Identifier.Text;
            if (!IsSensitiveIdentifier(identifier))
            {
                continue;
            }

            if (property.Initializer?.Value is ExpressionSyntax initExpr)
            {
                var stringValue = ExtractStaticString(initExpr);
                if (stringValue != null && IsRealSecretValue(stringValue))
                {
                    var line = property.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    if (reportedLines.Add(line))
                    {
                        findings.Add(new AnalyzerFinding
                        {
                            FilePath = filePath,
                            LineNumber = line,
                            Rule = "SEC001",
                            Message = $"Обнаружен захардкоженный секрет или пароль в свойстве '{identifier}' (значение скрыто во избежание утечки).",
                            Severity = Severity.Warning,
                            Recommendation = "Вынесите секретные значения, токены и пароли в защищённую конфигурацию, переменные окружения или хранилище секретов."
                        });
                    }
                }
            }
        }

        return findings;
    }

    private static bool IsSensitiveIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        if (ExcludedIdentifierSuffixRegex.IsMatch(identifier))
        {
            return false;
        }

        return SensitiveIdentifierRegex.IsMatch(identifier);
    }

    private static bool IsRealSecretValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();

        // Проверка шаблонных заполнителей и переменных окружения: ${VAR}, #{VAR}#, %VAR%, <password>, {token}
        if ((trimmed.StartsWith("${") && trimmed.EndsWith("}")) ||
            (trimmed.StartsWith("#{") && trimmed.EndsWith("}#")) ||
            (trimmed.StartsWith("%") && trimmed.EndsWith("%")) ||
            (trimmed.StartsWith("<") && trimmed.EndsWith(">")) ||
            (trimmed.StartsWith("{") && trimmed.EndsWith("}")))
        {
            return false;
        }

        if (PlaceholderValues.Contains(trimmed))
        {
            return false;
        }

        return true;
    }

    private static string? ExtractStaticString(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                return literal.Token.ValueText;

            case InterpolatedStringExpressionSyntax interpolated:
                if (!interpolated.Contents.OfType<InterpolationSyntax>().Any())
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var text in interpolated.Contents.OfType<InterpolatedStringTextSyntax>())
                    {
                        sb.Append(text.TextToken.ValueText);
                    }
                    return sb.ToString();
                }
                return null;

            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
                var left = ExtractStaticString(binary.Left);
                var right = ExtractStaticString(binary.Right);
                return left != null && right != null ? left + right : null;

            default:
                return null;
        }
    }
}
