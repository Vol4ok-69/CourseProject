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

    [Fact]
    public void Analyze_WithServicePointManagerSimpleAssignmentLambda_ReturnsCritical()
    {
        var source = """
        using System.Net;

        public class LegacyClient
        {
            public void Configure()
            {
                ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) => true;
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "LegacyClient.cs");

        Assert.Single(findings);
        Assert.Equal("SEC005", findings[0].Rule);
        Assert.Equal(Severity.Critical, findings[0].Severity);
        Assert.Contains("ServerCertificateValidationCallback", findings[0].Message);
    }

    [Fact]
    public void Analyze_WithServicePointManagerAddAssignmentLambda_ReturnsCritical()
    {
        var source = """
        using System.Net;

        public class LegacyClient
        {
            public void Configure()
            {
                ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, errors) => true;
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "LegacyClient.cs");

        Assert.Single(findings);
        Assert.Equal("SEC005", findings[0].Rule);
        Assert.Equal(Severity.Critical, findings[0].Severity);
        Assert.Contains("ServerCertificateValidationCallback", findings[0].Message);
    }

    [Fact]
    public void Analyze_WithServicePointManagerBlockLambdaReturningTrue_ReturnsCritical()
    {
        var source = """
        using System.Net;

        public class LegacyClient
        {
            public void Configure()
            {
                ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) =>
                {
                    return true;
                };
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "LegacyClient.cs");

        Assert.Single(findings);
        Assert.Equal("SEC005", findings[0].Rule);
        Assert.Equal(Severity.Critical, findings[0].Severity);
        Assert.Contains("ServerCertificateValidationCallback", findings[0].Message);
    }

    [Fact]
    public void Analyze_WithSystemNetServicePointManager_ReturnsCritical()
    {
        var source = """
        public class LegacyClient
        {
            public void Configure()
            {
                System.Net.ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) => true;
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "LegacyClient.cs");

        Assert.Single(findings);
        Assert.Equal("SEC005", findings[0].Rule);
        Assert.Equal(Severity.Critical, findings[0].Severity);
        Assert.Contains("ServerCertificateValidationCallback", findings[0].Message);
    }

    [Fact]
    public void Analyze_WithGlobalSystemNetServicePointManager_ReturnsCritical()
    {
        var source = """
        public class LegacyClient
        {
            public void Configure()
            {
                global::System.Net.ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) => true;
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "LegacyClient.cs");

        Assert.Single(findings);
        Assert.Equal("SEC005", findings[0].Rule);
        Assert.Equal(Severity.Critical, findings[0].Severity);
        Assert.Contains("ServerCertificateValidationCallback", findings[0].Message);
    }

    [Fact]
    public void Analyze_WithServicePointManagerSystemNetAlias_ReturnsCritical()
    {
        var source = """
        using ServicePointManager = System.Net.ServicePointManager;

        public class LegacyClient
        {
            public void Configure()
            {
                ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) => true;
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "LegacyClient.cs");

        Assert.Single(findings);
        Assert.Equal("SEC005", findings[0].Rule);
        Assert.Equal(Severity.Critical, findings[0].Severity);
        Assert.Contains("ServerCertificateValidationCallback", findings[0].Message);
    }

    [Fact]
    public void Analyze_WithMyNamespaceServicePointManager_DoesNotReturnFinding()
    {
        var source = """
        public class LegacyClient
        {
            public void Configure()
            {
                MyNamespace.ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) => true;
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "LegacyClient.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithCustomTypesServicePointManager_DoesNotReturnFinding()
    {
        var source = """
        public class LegacyClient
        {
            public void Configure()
            {
                CustomTypes.ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) => true;
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "LegacyClient.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithOtherServicePointManager_DoesNotReturnFinding()
    {
        var source = """
        public class LegacyClient
        {
            public void Configure()
            {
                Other.ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) => true;
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "LegacyClient.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithServicePointManagerCustomNamespaceAlias_DoesNotReturnFinding()
    {
        var source = """
        using ServicePointManager = MyNamespace.ServicePointManager;

        public class LegacyClient
        {
            public void Configure()
            {
                ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) => true;
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "LegacyClient.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithAlwaysFalseServicePointManagerLambda_DoesNotReturnFinding()
    {
        var source = """
        using System.Net;

        public class LegacyClient
        {
            public void Configure()
            {
                ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) => false;
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "LegacyClient.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithConditionalServicePointManagerExpression_DoesNotReturnFinding()
    {
        var source = """
        using System.Net;
        using System.Net.Security;

        public class LegacyClient
        {
            public void Configure()
            {
                ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) => errors == SslPolicyErrors.None;
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "LegacyClient.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithConditionalServicePointManagerBranching_DoesNotReturnFinding()
    {
        var source = """
        using System.Net;
        using System.Net.Security;

        public class LegacyClient
        {
            public void Configure()
            {
                ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) =>
                {
                    if (errors != SslPolicyErrors.None)
                    {
                        return false;
                    }

                    return true;
                };
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "LegacyClient.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithServicePointManagerUnsubscribe_DoesNotReturnFinding()
    {
        var source = """
        using System.Net;

        public class LegacyClient
        {
            public void Configure()
            {
                ServicePointManager.ServerCertificateValidationCallback -= (sender, cert, chain, errors) => true;
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "LegacyClient.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithServicePointManagerPropertyRead_DoesNotReturnFinding()
    {
        var source = """
        using System.Net;

        public class LegacyClient
        {
            public object? GetCallback()
            {
                return ServicePointManager.ServerCertificateValidationCallback;
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "LegacyClient.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithDifferentServicePointManagerAliasesInSeparateNamespaces_IsolatesScopes()
    {
        var source = """
        namespace NamespaceA
        {
            using ServicePointManager = System.Net.ServicePointManager;

            public class ClientA
            {
                public void Configure()
                {
                    ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) => true;
                }
            }
        }

        namespace NamespaceB
        {
            using ServicePointManager = MyNamespace.ServicePointManager;

            public class ClientB
            {
                public void Configure()
                {
                    ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) => true;
                }
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "MultiNamespaceClient.cs");

        Assert.Single(findings);
        Assert.Equal("SEC005", findings[0].Rule);
        Assert.Equal(Severity.Critical, findings[0].Severity);
        Assert.Equal(9, findings[0].LineNumber);
        Assert.Contains("ServerCertificateValidationCallback", findings[0].Message);
    }
}
