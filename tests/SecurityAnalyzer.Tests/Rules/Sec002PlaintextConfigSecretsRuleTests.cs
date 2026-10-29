using Domain.Enums;
using SecurityAnalyzer.Rules;
using Xunit;

namespace SecurityAnalyzer.Tests.Rules;

public sealed class Sec002PlaintextConfigSecretsRuleTests
{
    private readonly Sec002PlaintextConfigSecretsRule _rule = new();

    [Fact]
    public void Analyze_WithConnectionStringPlaintextPassword_ReturnsWarningWithoutLeakingPassword()
    {
        var passwordValue = "ProductionDbPassword2026!";
        var json = $$"""
        {
          "ConnectionStrings": {
            "DefaultConnection": "Host=prod.db;Database=app;Username=admin;Password={{passwordValue}};"
          }
        }
        """;

        var findings = _rule.Analyze(json, "appsettings.json");

        Assert.Single(findings);
        Assert.Equal("SEC002", findings[0].Rule);
        Assert.Equal(Severity.Warning, findings[0].Severity);
        Assert.Contains("ConnectionStrings:DefaultConnection", findings[0].Message);
        Assert.DoesNotContain(passwordValue, findings[0].Message);
        Assert.DoesNotContain(passwordValue, findings[0].Recommendation);
    }

    [Fact]
    public void Analyze_WithDuplicatePropertyNamesAcrossSections_CalculatesDistinctLineNumbers()
    {
        var json = """
        {
          "PrimaryAuth": {
            "Secret": "super_secret_primary_key_here"
          },
          "SecondaryAuth": {
            "Secret": "super_secret_secondary_key_here"
          }
        }
        """;

        var findings = _rule.Analyze(json, "appsettings.json");

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.LineNumber == 3 && f.Message.Contains("PrimaryAuth:Secret"));
        Assert.Contains(findings, f => f.LineNumber == 6 && f.Message.Contains("SecondaryAuth:Secret"));
        Assert.NotEqual(findings[0].LineNumber, findings[1].LineNumber);
    }

    [Fact]
    public void Analyze_WithPlaintextJwtSecret_ReturnsWarningWithoutLeakingSecret()
    {
        var secretValue = "super_secret_jwt_signing_key_production_256bit";
        var json = $$"""
        {
          "Jwt": {
            "Secret": "{{secretValue}}"
          }
        }
        """;

        var findings = _rule.Analyze(json, "appsettings.Production.json");

        Assert.Single(findings);
        Assert.Equal("SEC002", findings[0].Rule);
        Assert.Equal(Severity.Warning, findings[0].Severity);
        Assert.Contains("Jwt:Secret", findings[0].Message);
        Assert.DoesNotContain(secretValue, findings[0].Message);
    }

    [Fact]
    public void Analyze_WithCommonWeakPasswordsInConfig_ReturnsWarning()
    {
        var json = """
        {
          "Auth": {
            "DefaultPassword": "password",
            "AdminPassword": "admin"
          }
        }
        """;

        var findings = _rule.Analyze(json, "appsettings.json");

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal("SEC002", f.Rule));
    }

    [Fact]
    public void Analyze_WithDeeplyNestedApiKey_ReturnsWarning()
    {
        var apiKeyValue = "sk_live_9876543210abcdef";
        var json = $$"""
        {
          "Services": {
            "PaymentGateway": {
              "ApiKey": "{{apiKeyValue}}"
            }
          }
        }
        """;

        var findings = _rule.Analyze(json, "appsettings.json");

        Assert.Single(findings);
        Assert.Equal("SEC002", findings[0].Rule);
        Assert.Contains("Services:PaymentGateway:ApiKey", findings[0].Message);
        Assert.DoesNotContain(apiKeyValue, findings[0].Message);
    }

    [Fact]
    public void Analyze_WithEnvVarReferences_DoesNotReturnFinding()
    {
        var json = """
        {
          "ConnectionStrings": {
            "DefaultConnection": "Host=prod.db;Username=admin;Password=${DB_PASSWORD};"
          },
          "Jwt": {
            "Secret": "#{JWT_SECRET}#",
            "ApiKey": "%MY_API_KEY%"
          }
        }
        """;

        var findings = _rule.Analyze(json, "appsettings.json");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithEmptyOrPlaceholderValues_DoesNotReturnFinding()
    {
        var json = """
        {
          "ConnectionStrings": {
            "DefaultConnection": "Host=localhost;Database=mydb;Username=postgres;Password=***;"
          },
          "Jwt": {
            "Secret": "",
            "Password": "TODO",
            "ApiKey": "<your-key-here>"
          }
        }
        """;

        var findings = _rule.Analyze(json, "appsettings.json");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithNonSensitiveConfigKeys_DoesNotReturnFinding()
    {
        var json = """
        {
          "Logging": {
            "LogLevel": {
              "Default": "Information"
            }
          },
          "Jwt": {
            "Issuer": "https://identity.myapp.com",
            "Audience": "https://api.myapp.com"
          }
        }
        """;

        var findings = _rule.Analyze(json, "appsettings.json");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithMalformedJson_DoesNotThrowAndReturnsEmpty()
    {
        var invalidJson = "{ \"ConnectionStrings\": { \"DefaultConnection\": broken json ... ";

        var findings = _rule.Analyze(invalidJson, "appsettings.json");

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_WithVariousConnectionStringParameterOrders_ReturnsWarningWithoutLeakingPassword()
    {
        var passwordValue = "RealSecret123";
        var json = """
        {
          "ConnectionStrings": {
            "Conn1": "Host=localhost;Password=RealSecret123;Database=AppDb;",
            "Conn2": "Password=RealSecret123;Host=localhost;Database=AppDb;",
            "Conn3": "Server=localhost;Pwd=RealSecret123;Database=AppDb;",
            "Conn4": "Pwd=RealSecret123;Server=localhost;Database=AppDb;",
            "Safe1": "Password=${DB_PASSWORD};Host=localhost;Database=AppDb;",
            "Safe2": "Host=localhost;Password={DB_PASSWORD};Database=AppDb;",
            "Safe3": "Password=<password>;Server=localhost;Database=AppDb;",
            "Safe4": "Server=localhost;Password=***;Database=AppDb;",
            "Safe5": "Password=%DB_PASSWORD%;Data Source=localhost;Database=AppDb;",
            "Safe6": "Password=#{DB_PASSWORD}#;Host=localhost;Database=AppDb;"
          }
        }
        """;

        var findings = _rule.Analyze(json, "appsettings.json");

        Assert.Equal(4, findings.Count);
        Assert.All(findings, f =>
        {
            Assert.Equal("SEC002", f.Rule);
            Assert.Equal(Severity.Warning, f.Severity);
            Assert.DoesNotContain(passwordValue, f.Message);
            Assert.DoesNotContain(passwordValue, f.Recommendation);
        });

        Assert.Contains(findings, f => f.LineNumber == 3 && f.Message.Contains("ConnectionStrings:Conn1"));
        Assert.Contains(findings, f => f.LineNumber == 4 && f.Message.Contains("ConnectionStrings:Conn2"));
        Assert.Contains(findings, f => f.LineNumber == 5 && f.Message.Contains("ConnectionStrings:Conn3"));
        Assert.Contains(findings, f => f.LineNumber == 6 && f.Message.Contains("ConnectionStrings:Conn4"));
    }

    [Fact]
    public void Analyze_WithArrayOfObjectsFollowedByNestedObject_FindsBothSecretsWithCorrectPathsAndLines()
    {
        var apiKeySecret = "secret-api-key-in-array";
        var passwordSecret = "secret-password-in-nested-object";

        var json = """
        {
          "Logging": {
            "LogLevel": "Information"
          },
          "Integrations": [
            {
              "Name": "ServiceA",
              "ApiKey": "secret-api-key-in-array"
            }
          ],
          "DatabaseSettings": {
            "Credentials": {
              "Password": "secret-password-in-nested-object"
            }
          }
        }
        """;

        var findings = _rule.Analyze(json, "appsettings.json");

        Assert.Equal(2, findings.Count);

        var first = findings.Single(f => f.Message.Contains("Integrations:ApiKey"));
        Assert.Equal("SEC002", first.Rule);
        Assert.Equal(8, first.LineNumber);
        Assert.DoesNotContain(apiKeySecret, first.Message);
        Assert.DoesNotContain(apiKeySecret, first.Recommendation);

        var second = findings.Single(f => f.Message.Contains("DatabaseSettings:Credentials:Password"));
        Assert.Equal("SEC002", second.Rule);
        Assert.Equal(13, second.LineNumber);
        Assert.DoesNotContain(passwordSecret, second.Message);
        Assert.DoesNotContain(passwordSecret, second.Recommendation);
    }

    [Fact]
    public void Analyze_WithMultipleAdjacentArrayElements_FindsAllSecretsWithDistinctLines()
    {
        var secretA = "key_service_a_secret_12345";
        var secretB = "key_service_b_secret_67890";

        var json = """
        {
          "Services": [
            {
              "Name": "ServiceA",
              "ApiKey": "key_service_a_secret_12345"
            },
            {
              "Name": "ServiceB",
              "ApiKey": "key_service_b_secret_67890"
            }
          ]
        }
        """;

        var findings = _rule.Analyze(json, "appsettings.json");

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f =>
        {
            Assert.Equal("SEC002", f.Rule);
            Assert.Contains("Services:ApiKey", f.Message);
            Assert.DoesNotContain(secretA, f.Message);
            Assert.DoesNotContain(secretA, f.Recommendation);
            Assert.DoesNotContain(secretB, f.Message);
            Assert.DoesNotContain(secretB, f.Recommendation);
        });

        Assert.Contains(findings, f => f.LineNumber == 5);
        Assert.Contains(findings, f => f.LineNumber == 9);
    }

    [Fact]
    public void Analyze_WithDeeplyNestedArraysAndObjects_FindsSecretWithFullHierarchicalPath()
    {
        var clusterSecret = "deeply-nested-cluster-token-123";

        var json = """
        {
          "Cluster": {
            "Nodes": [
              {
                "Id": "node-1",
                "Tokens": [
                  {
                    "Secret": "deeply-nested-cluster-token-123"
                  }
                ]
              }
            ]
          }
        }
        """;

        var findings = _rule.Analyze(json, "appsettings.json");

        Assert.Single(findings);
        var finding = findings[0];
        Assert.Equal("SEC002", finding.Rule);
        Assert.Contains("Cluster:Nodes:Tokens:Secret", finding.Message);
        Assert.Equal(8, finding.LineNumber);
        Assert.DoesNotContain(clusterSecret, finding.Message);
        Assert.DoesNotContain(clusterSecret, finding.Recommendation);
    }

    [Fact]
    public void Analyze_WithDuplicatePropertyNamesAtDifferentNestingLevels_PreservesContextAndLines()
    {
        var secretValue = "secret-value-unique-123";

        var json = """
        {
          "Secret": "secret-value-unique-123",
          "SectionA": {
            "Secret": "secret-value-unique-123",
            "SectionB": {
              "Secret": "secret-value-unique-123"
            }
          }
        }
        """;

        var findings = _rule.Analyze(json, "appsettings.json");

        Assert.Equal(3, findings.Count);
        Assert.All(findings, f =>
        {
            Assert.Equal("SEC002", f.Rule);
            Assert.DoesNotContain(secretValue, f.Message);
            Assert.DoesNotContain(secretValue, f.Recommendation);
        });

        Assert.Contains(findings, f => f.LineNumber == 2 && f.Message.Contains("'Secret'"));
        Assert.Contains(findings, f => f.LineNumber == 4 && f.Message.Contains("'SectionA:Secret'"));
        Assert.Contains(findings, f => f.LineNumber == 6 && f.Message.Contains("'SectionA:SectionB:Secret'"));
    }

    [Fact]
    public void Analyze_WithStringValuesInSensitiveArray_FindsAllSecretsWithCorrectLines()
    {
        var key1 = "sk_live_array_token_1111111111";
        var key2 = "sk_live_array_token_2222222222";

        var json = """
        {
          "ApiKeys": [
            "sk_live_array_token_1111111111",
            "sk_live_array_token_2222222222"
          ]
        }
        """;

        var findings = _rule.Analyze(json, "appsettings.json");

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f =>
        {
            Assert.Equal("SEC002", f.Rule);
            Assert.Contains("ApiKeys", f.Message);
            Assert.DoesNotContain(key1, f.Message);
            Assert.DoesNotContain(key1, f.Recommendation);
            Assert.DoesNotContain(key2, f.Message);
            Assert.DoesNotContain(key2, f.Recommendation);
        });

        Assert.Contains(findings, f => f.LineNumber == 3);
        Assert.Contains(findings, f => f.LineNumber == 4);
    }
}