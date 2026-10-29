using Domain.Enums;
using Microsoft.CodeAnalysis.CSharp;
using SecurityAnalyzer.Rules;
using Xunit;

namespace SecurityAnalyzer.Tests.Rules;

public sealed class Sec003InsecureCorsRuleTests
{
    private readonly Sec003InsecureCorsRule _rule = new();

    [Fact]
    public void Analyze_WithAllowAnyOrigin_ReturnsWarning()
    {
        var source = """
        public class Startup
        {
            public void ConfigureServices(IServiceCollection services)
            {
                services.AddCors(options =>
                {
                    options.AddDefaultPolicy(policy =>
                    {
                        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
                    });
                });
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "Startup.cs");

        Assert.Single(findings);
        Assert.Equal("SEC003", findings[0].Rule);
        Assert.Equal(Severity.Warning, findings[0].Severity);
        Assert.Contains("AllowAnyOrigin", findings[0].Message);
    }

    [Fact]
    public void Analyze_WithSetIsOriginAllowedReturningTrueExpression_ReturnsWarning()
    {
        var source = """
        public class Startup
        {
            public void ConfigureCors(CorsPolicyBuilder builder)
            {
                builder.SetIsOriginAllowed(_ => true);
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "Startup.cs");

        Assert.Single(findings);
        Assert.Equal("SEC003", findings[0].Rule);
        Assert.Equal(Severity.Warning, findings[0].Severity);
        Assert.Contains("SetIsOriginAllowed", findings[0].Message);
    }

    [Fact]
    public void Analyze_WithSetIsOriginAllowedReturningTrueBlock_ReturnsWarning()
    {
        var source = """
        public class Startup
        {
            public void ConfigureCors(CorsPolicyBuilder builder)
            {
                builder.SetIsOriginAllowed(origin => { return true; });
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "Startup.cs");

        Assert.Single(findings);
        Assert.Equal("SEC003", findings[0].Rule);
        Assert.Equal(Severity.Warning, findings[0].Severity);
    }

    [Fact]
    public void Analyze_WithConditionalBranchesReturningTrueAndFalse_DoesNotReturnFinding()
    {
        var source = """
        public class Startup
        {
            public void ConfigureCors(CorsPolicyBuilder builder)
            {
                builder.SetIsOriginAllowed(origin =>
                {
                    if (origin == "https://bad.com")
                    {
                        return false;
                    }
                    return true;
                });
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "Startup.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithIfBranchReturningTrue_DoesNotReturnFinding()
    {
        var source = """
        public class Startup
        {
            public void ConfigureCors(CorsPolicyBuilder builder)
            {
                builder.SetIsOriginAllowed(origin =>
                {
                    if (origin == "https://myapp.com")
                    {
                        return true;
                    }
                    return false;
                });
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "Startup.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithTernaryConditionalOriginCheck_DoesNotReturnFinding()
    {
        var source = """
        public class Startup
        {
            public void ConfigureCors(CorsPolicyBuilder builder)
            {
                builder.SetIsOriginAllowed(origin => origin.StartsWith("https://") ? true : false);
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "Startup.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithSpecificOrigins_DoesNotReturnFinding()
    {
        var source = """
        public class Startup
        {
            public void ConfigureServices(IServiceCollection services)
            {
                services.AddCors(options =>
                {
                    options.AddDefaultPolicy(policy =>
                    {
                        policy.WithOrigins("https://myapp.com", "https://admin.myapp.com")
                              .AllowAnyHeader()
                              .AllowAnyMethod();
                    });
                });
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "Startup.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithCommentOrLiteralContainingAllowAnyOrigin_DoesNotReturnFinding()
    {
        var source = """
        public class Documentation
        {
            // Do not use AllowAnyOrigin in production!
            public string Note => "We avoided AllowAnyOrigin for security reasons.";
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "Documentation.cs");

        Assert.Empty(findings);
    }
}
