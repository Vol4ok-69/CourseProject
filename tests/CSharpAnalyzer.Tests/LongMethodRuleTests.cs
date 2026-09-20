using CSharp.Rules;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpAnalyzer.Tests;

public class LongMethodRuleTests
{
    [Fact]
    public void Analyze_ShouldReportLongMethod()
    {
        var body = string.Join(Environment.NewLine, Enumerable.Repeat("        var value = 1;", 51));

        var source = $$"""
        public class TestClass
        {
            public void TestMethod()
            {
        {{body}}
            }
        }
        """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();

        var rule = new LongMethodRule();

        var findings = rule.Analyze(root, "TestClass.cs");

        Assert.Single(findings);
        Assert.Equal("CSHARP002", findings[0].Rule);
        Assert.Equal("TestClass.cs", findings[0].FilePath);
    }

    [Fact]
    public void Analyze_ShouldNotReportShortMethod()
    {
        var source = """
        public class TestClass
        {
            public void TestMethod()
            {
                var value = 1;
            }
        }
        """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();

        var rule = new LongMethodRule();

        var findings = rule.Analyze(root, "TestClass.cs");

        Assert.Empty(findings);
    }
}