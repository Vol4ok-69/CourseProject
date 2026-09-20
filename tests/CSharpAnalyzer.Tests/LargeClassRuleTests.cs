using CSharp.Rules;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpAnalyzer.Tests;

public class LargeClassRuleTests
{
    [Fact]
    public void Analyze_ShouldReportLargeClass()
    {
        var source = $$"""
        public class TestClass
        {
        {{string.Join(Environment.NewLine, Enumerable.Repeat("    private int value;", 501))}}
        }
        """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();

        var rule = new LargeClassRule();

        var findings = rule.Analyze(root, "TestClass.cs");

        Assert.Single(findings);
        Assert.Equal("CSHARP001", findings[0].Rule);
        Assert.Equal("TestClass.cs", findings[0].FilePath);
    }

    [Fact]
    public void Analyze_ShouldNotReportSmallClass()
    {
        var source = """
        public class TestClass
        {
            private int value;
        }
        """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();

        var rule = new LargeClassRule();

        var findings = rule.Analyze(root, "TestClass.cs");

        Assert.Empty(findings);
    }
}