using Domain.Enums;
using Microsoft.CodeAnalysis.CSharp;
using SecurityAnalyzer.Rules;
using Xunit;

namespace SecurityAnalyzer.Tests.Rules;

public sealed class Sec005DisabledCertificateValidationRuleTests
{
    private readonly Sec005DisabledCertificateValidationRule _rule = new();

    [Fact]
    public void Analyze_WithDangerousValidatorAssignment_ReturnsCritical()
    {
        var source = """
        public class ApiClient
        {
            public HttpClient CreateClient()
            {
                var handler = new HttpClientHandler();
                handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                return new HttpClient(handler);
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "ApiClient.cs");

        Assert.Single(findings);
        Assert.Equal("SEC005", findings[0].Rule);
        Assert.Equal(Severity.Critical, findings[0].Severity);
        Assert.Contains("ServerCertificateCustomValidationCallback", findings[0].Message);
    }

    [Fact]
    public void Analyze_WithDangerousValidatorInObjectInitializer_ReturnsCritical()
    {
        var source = """
        public class ApiClient
        {
            public HttpClient CreateClient()
            {
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                };
                return new HttpClient(handler);
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "ApiClient.cs");

        Assert.Single(findings);
        Assert.Equal("SEC005", findings[0].Rule);
        Assert.Equal(Severity.Critical, findings[0].Severity);
    }

    [Fact]
    public void Analyze_WithAlwaysTrueLambdaExpression_ReturnsCritical()
    {
        var source = """
        public class ApiClient
        {
            public HttpClient CreateClient()
            {
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
                };
                return new HttpClient(handler);
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "ApiClient.cs");

        Assert.Single(findings);
        Assert.Equal("SEC005", findings[0].Rule);
        Assert.Equal(Severity.Critical, findings[0].Severity);
    }

    [Fact]
    public void Analyze_WithAlwaysTrueLambdaBlock_ReturnsCritical()
    {
        var source = """
        public class ApiClient
        {
            public void Setup(HttpClientHandler handler)
            {
                handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) =>
                {
                    return true;
                };
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "ApiClient.cs");

        Assert.Single(findings);
        Assert.Equal("SEC005", findings[0].Rule);
        Assert.Equal(Severity.Critical, findings[0].Severity);
    }

    [Fact]
    public void Analyze_WithConditionalValidationBranches_DoesNotReturnFinding()
    {
        var source = """
        public class ApiClient
        {
            public void Setup(HttpClientHandler handler)
            {
                handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) =>
                {
                    if (sslPolicyErrors != SslPolicyErrors.None)
                    {
                        return false;
                    }
                    return true;
                };
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "ApiClient.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithStandardHttpClientHandler_DoesNotReturnFinding()
    {
        var source = """
        public class ApiClient
        {
            public HttpClient CreateClient()
            {
                var handler = new HttpClientHandler();
                return new HttpClient(handler);
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "ApiClient.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithRealCertificateValidationCheck_DoesNotReturnFinding()
    {
        var source = """
        public class ApiClient
        {
            public void Setup(HttpClientHandler handler)
            {
                handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) =>
                {
                    if (sslPolicyErrors == SslPolicyErrors.None) return true;
                    return cert?.GetCertHashString() == "TRUSTED_HASH_HERE";
                };
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "ApiClient.cs");

        Assert.Empty(findings);
    }
}
