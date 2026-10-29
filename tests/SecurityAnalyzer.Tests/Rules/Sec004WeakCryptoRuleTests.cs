using Domain.Enums;
using Microsoft.CodeAnalysis.CSharp;
using SecurityAnalyzer.Rules;
using Xunit;

namespace SecurityAnalyzer.Tests.Rules;

public sealed class Sec004WeakCryptoRuleTests
{
    private readonly Sec004WeakCryptoRule _rule = new();

    [Fact]
    public void Analyze_WithMd5Create_ReturnsWarning()
    {
        var source = """
        public class ChecksumService
        {
            public byte[] Compute(byte[] data)
            {
                using var md5 = MD5.Create();
                return md5.ComputeHash(data);
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "ChecksumService.cs");

        Assert.Single(findings);
        Assert.Equal("SEC004", findings[0].Rule);
        Assert.Equal(Severity.Warning, findings[0].Severity);
        Assert.Contains("MD5", findings[0].Message);
        Assert.Contains("Argon2, BCrypt, PBKDF2", findings[0].Recommendation);
    }

    [Fact]
    public void Analyze_WithSha1HashData_ReturnsWarning()
    {
        var source = """
        public class Hasher
        {
            public byte[] Hash(byte[] data)
            {
                return SHA1.HashData(data);
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "Hasher.cs");

        Assert.Single(findings);
        Assert.Equal("SEC004", findings[0].Rule);
        Assert.Equal(Severity.Warning, findings[0].Severity);
        Assert.Contains("SHA1", findings[0].Message);
    }

    [Fact]
    public void Analyze_WithMd5CryptoServiceProvider_ReturnsWarning()
    {
        var source = """
        public class Hasher
        {
            public byte[] Hash(byte[] data)
            {
                using var provider = new MD5CryptoServiceProvider();
                return provider.ComputeHash(data);
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "Hasher.cs");

        Assert.Single(findings);
        Assert.Equal("SEC004", findings[0].Rule);
        Assert.Contains("MD5CryptoServiceProvider", findings[0].Message);
    }

    [Fact]
    public void Analyze_WithDesAndTripleDesCreate_ReturnsWarnings()
    {
        var source = """
        public class CipherService
        {
            public void Encrypt()
            {
                using var des = DES.Create();
                using var tripleDes = TripleDES.Create();
                using var rc2 = RC2.Create();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "CipherService.cs");

        Assert.Equal(3, findings.Count);
        Assert.All(findings, f => Assert.Equal("SEC004", f.Rule));
    }

    [Fact]
    public void Analyze_WithHmacMd5AndHmacSha1_ReturnsWarnings()
    {
        var source = """
        public class Signer
        {
            public void Sign(byte[] key)
            {
                using var hmac1 = new HMACMD5(key);
                using var hmac2 = new HMACSHA1(key);
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "Signer.cs");

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal("SEC004", f.Rule));
    }

    [Fact]
    public void Analyze_WithSha256AndAes_DoesNotReturnFinding()
    {
        var source = """
        public class ModernCryptoService
        {
            public byte[] HashAndEncrypt(byte[] data)
            {
                var hash = SHA256.HashData(data);
                using var sha512 = SHA512.Create();
                using var aes = Aes.Create();
                return hash;
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "ModernCryptoService.cs");

        Assert.Empty(findings);
    }
}
