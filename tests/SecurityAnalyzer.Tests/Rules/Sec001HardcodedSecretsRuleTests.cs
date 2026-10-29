using Domain.Enums;
using Microsoft.CodeAnalysis.CSharp;
using SecurityAnalyzer.Rules;
using Xunit;

namespace SecurityAnalyzer.Tests.Rules;

public sealed class Sec001HardcodedSecretsRuleTests
{
    private readonly Sec001HardcodedSecretsRule _rule = new();

    [Fact]
    public void Analyze_WithPemPrivateKey_ReturnsCriticalFindingWithoutLeakingSecret()
    {
        var rawSecret = "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA0Y...\n-----END RSA PRIVATE KEY-----";
        var source = $$"""
        public class KeyProvider
        {
            private const string PrivateKey = "{{rawSecret}}";
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "KeyProvider.cs");

        Assert.Single(findings);
        Assert.Equal("SEC001", findings[0].Rule);
        Assert.Equal(Severity.Critical, findings[0].Severity);
        Assert.DoesNotContain("MIIEowIBAAKCAQEA0Y", findings[0].Message);
        Assert.DoesNotContain("MIIEowIBAAKCAQEA0Y", findings[0].Recommendation);
    }

    [Fact]
    public void Analyze_WithConnectionStringPassword_ReturnsWarningWithoutLeakingPassword()
    {
        var secretPassword = "SuperSecretPassword123!";
        var source = $$"""
        public class DbConfig
        {
            public string GetConnectionString()
            {
                var connStr = "Host=localhost;Database=mydb;Username=postgres;Password={{secretPassword}};";
                return connStr;
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "DbConfig.cs");

        Assert.Single(findings);
        Assert.Equal("SEC001", findings[0].Rule);
        Assert.Equal(Severity.Warning, findings[0].Severity);
        Assert.DoesNotContain(secretPassword, findings[0].Message);
        Assert.DoesNotContain(secretPassword, findings[0].Recommendation);
    }

    [Fact]
    public void Analyze_WithWeakCommonPasswords_ReturnsWarning()
    {
        var source = """
        public class UserCredentials
        {
            public void Login()
            {
                var secretKey = "password";
                var userPwd = "admin";
                var defaultSecret = "123";
                var connStr = "Host=localhost;Database=db;Username=postgres;Password=123456;";
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserCredentials.cs");

        Assert.Equal(4, findings.Count);
        Assert.All(findings, f =>
        {
            Assert.Equal("SEC001", f.Rule);
            Assert.DoesNotContain("password", f.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("admin", f.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("123456", f.Message);
        });
    }

    [Fact]
    public void Analyze_WithHardcodedVariableSecret_ReturnsWarning()
    {
        var apiKey = string.Concat("sk_", "live_", "51Abcdefghijklmnop123456789");
        var source = $$"""
        public class PaymentService
        {
            public void Pay()
            {
                var apiKey = "{{apiKey}}";
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "PaymentService.cs");

        Assert.Single(findings);
        Assert.Equal("SEC001", findings[0].Rule);
        Assert.Equal(Severity.Warning, findings[0].Severity);
        Assert.Contains("apiKey", findings[0].Message);
        Assert.DoesNotContain(apiKey, findings[0].Message);
    }

    [Fact]
    public void Analyze_WithHardcodedFieldPassword_ReturnsWarning()
    {
        var passwordValue = "MyAdminP@ssw0rd2026";
        var source = $$"""
        public class AdminClient
        {
            private string _adminPassword = "{{passwordValue}}";
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "AdminClient.cs");

        Assert.Single(findings);
        Assert.Equal("SEC001", findings[0].Rule);
        Assert.Equal(Severity.Warning, findings[0].Severity);
        Assert.Contains("_adminPassword", findings[0].Message);
        Assert.DoesNotContain(passwordValue, findings[0].Message);
    }

    [Fact]
    public void Analyze_WithHardcodedPropertyJwtSecret_ReturnsWarning()
    {
        var tokenValue = "my_custom_jwt_secret_key_long_value";
        var source = $$"""
        public class AuthOptions
        {
            public string JwtSecret { get; set; } = "{{tokenValue}}";
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "AuthOptions.cs");

        Assert.Single(findings);
        Assert.Equal("SEC001", findings[0].Rule);
        Assert.Equal(Severity.Warning, findings[0].Severity);
        Assert.Contains("JwtSecret", findings[0].Message);
        Assert.DoesNotContain(tokenValue, findings[0].Message);
    }

    [Fact]
    public void Analyze_WithConfigLookup_DoesNotReturnFinding()
    {
        var source = """
        public class AuthOptions
        {
            public void Configure(IConfiguration config)
            {
                var jwtSecret = config["Jwt:Key"];
                var connStr = config.GetConnectionString("DefaultConnection");
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "AuthOptions.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithPlaceholdersAndTemplateTokens_DoesNotReturnFinding()
    {
        var source = """
        public class TestFixtures
        {
            public void Setup()
            {
                var password = "TODO";
                var secretKey = "CHANGEME";
                var token = "<password>";
                var dummyApiKey = "YOUR_API_KEY";
                var samplePassword = "{token}";
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "TestFixtures.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithNonSensitiveSuffixes_DoesNotReturnFinding()
    {
        var source = """
        public class UserService
        {
            public void Validate(string input)
            {
                var passwordHash = "8d969eef6ecad3c29a3a629280e686cf0c3f5d5a86aff3ca12020c923adc6c92";
                var passwordSalt = "random_salt_123456";
                var passwordRegex = "^(?=.*[A-Z]).{8,}$";
                var passwordPolicy = "MinLength=8";
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserService.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithConnectionStringPlaceholderPassword_DoesNotReturnFinding()
    {
        var source = """
        public class DbConfig
        {
            public void Connect()
            {
                var conn1 = "Host=localhost;Database=mydb;Username=postgres;Password=***;";
                var conn2 = "Host=localhost;Database=mydb;Username=postgres;Password=TODO;";
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "DbConfig.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithVariousConnectionStringParameterOrders_ReturnsWarningWithoutLeakingPassword()
    {
        var source = """
        public class DbConfig
        {
            public void Connect()
            {
                var conn1 = "Host=localhost;Password=RealSecret123;Database=AppDb;";
                var conn2 = "Password=RealSecret123;Host=localhost;Database=AppDb;";
                var conn3 = "Server=localhost;Pwd=RealSecret123;Database=AppDb;";
                var conn4 = "Pwd=RealSecret123;Server=localhost;Database=AppDb;";
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "DbConfig.cs");

        Assert.Equal(4, findings.Count);
        Assert.All(findings, f =>
        {
            Assert.Equal("SEC001", f.Rule);
            Assert.Equal(Severity.Warning, f.Severity);
            Assert.DoesNotContain("RealSecret123", f.Message);
            Assert.DoesNotContain("RealSecret123", f.Recommendation);
        });
    }

    [Fact]
    public void Analyze_WithConnectionStringSafePlaceholdersAndEnvVars_DoesNotReturnFinding()
    {
        var source = """
        public class DbConfig
        {
            public void Connect()
            {
                var conn1 = "Password=${DB_PASSWORD};Host=localhost;Database=mydb;Username=postgres;";
                var conn2 = "Host=localhost;Database=mydb;Username=postgres;Password=${DB_PASSWORD};";
                var conn3 = "Password={DB_PASSWORD};Server=localhost;Database=mydb;User Id=sa;";
                var conn4 = "Server=localhost;Database=mydb;User Id=sa;Password={DB_PASSWORD};";
                var conn5 = "Password=<password>;Data Source=localhost;Database=mydb;";
                var conn6 = "Password=***;Host=localhost;Database=mydb;";
                var conn7 = "Password=%DB_PASSWORD%;Server=localhost;Database=mydb;";
                var conn8 = "Password=#{DB_PASSWORD}#;Host=localhost;Database=mydb;";
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "DbConfig.cs");

        Assert.Empty(findings);
    }
}