using Application.Analyzers;
using Domain.Enums;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SecurityAnalyzer.Rules;

public sealed class Sec004WeakCryptoRule : ISecurityRule
{
    private static readonly HashSet<string> WeakAlgorithmTypes = new(StringComparer.Ordinal)
    {
        "MD5",
        "SHA1",
        "DES",
        "TripleDES",
        "RC2",
        "HMACMD5",
        "HMACSHA1"
    };

    private static readonly HashSet<string> WeakProviderTypes = new(StringComparer.Ordinal)
    {
        "MD5CryptoServiceProvider",
        "MD5Cng",
        "SHA1Managed",
        "SHA1CryptoServiceProvider",
        "SHA1Cng",
        "DESCryptoServiceProvider",
        "TripleDESCryptoServiceProvider",
        "RC2CryptoServiceProvider",
        "HMACMD5",
        "HMACSHA1"
    };

    public IReadOnlyList<AnalyzerFinding> Analyze(SyntaxNode root, string filePath)
    {
        var findings = new List<AnalyzerFinding>();

        // 1. Поиск вызовов фабричных методов: MD5.Create(), SHA1.Create(), MD5.HashData() и т.д.
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var invocation in invocations)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var callerName = GetSimpleName(memberAccess.Expression);
                var methodName = memberAccess.Name.Identifier.Text;

                if (callerName != null && WeakAlgorithmTypes.Contains(callerName) &&
                    (methodName is "Create" or "HashData"))
                {
                    var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    findings.Add(new AnalyzerFinding
                    {
                        FilePath = filePath,
                        LineNumber = line,
                        Rule = "SEC004",
                        Message = $"Обнаружено использование криптографически слабого или устаревшего алгоритма '{callerName}'. Алгоритм подвержен известным уязвимостям к атакам на коллизии.",
                        Severity = Severity.Warning,
                        Recommendation = "Проверьте назначение алгоритма: для цифровых подписей и проверки целостности используйте семейство SHA-2 (SHA-256, SHA-512) или SHA-3; для симметричного шифрования — AES (Aes.Create()); для паролей применяйте специализированные функции растяжения ключа (Argon2, BCrypt, PBKDF2), но не быстрые хеш-функции общего назначения."
                    });
                }
            }
        }

        // 2. Поиск создания объектов провайдеров: new MD5CryptoServiceProvider(), new SHA1Managed() и т.д.
        var objectCreations = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>();
        foreach (var creation in objectCreations)
        {
            var typeName = GetSimpleName(creation.Type);
            if (typeName != null && WeakProviderTypes.Contains(typeName))
            {
                var line = creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                findings.Add(new AnalyzerFinding
                {
                    FilePath = filePath,
                    LineNumber = line,
                    Rule = "SEC004",
                    Message = $"Обнаружено прямое создание экземпляра слабого криптографического провайдера '{typeName}'.",
                    Severity = Severity.Warning,
                    Recommendation = "Проверьте назначение алгоритма: для цифровых подписей и проверки целостности используйте семейство SHA-2 (SHA-256, SHA-512) или SHA-3; для симметричного шифрования — AES (Aes.Create()); для паролей применяйте специализированные функции растяжения ключа (Argon2, BCrypt, PBKDF2), но не быстрые хеш-функции общего назначения."
                });
            }
        }

        return findings;
    }

    private static string? GetSimpleName(TypeSyntax typeSyntax)
    {
        return typeSyntax switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            _ => null
        };
    }

    private static string? GetSimpleName(ExpressionSyntax expressionSyntax)
    {
        return expressionSyntax switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            _ => null
        };
    }
}
