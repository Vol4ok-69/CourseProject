using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Xunit;

namespace Backend.Tests.Unit;

public class AnalysisResultsConsistencyTests
{
    private static AnalysisResult CreateSampleResult(
        int id = 0,
        int analysisId = 0,
        string filePath = "src/Service.cs",
        int lineNumber = 42,
        string rule = "SEC001",
        string message = "SQL injection vulnerability detected",
        Severity severity = Severity.Critical,
        string? recommendation = "Use parameterized queries",
        AnalyzerType analyzerType = AnalyzerType.Security)
    {
        return new AnalysisResult
        {
            Id = id,
            AnalysisId = analysisId,
            FilePath = filePath,
            LineNumber = lineNumber,
            Rule = rule,
            Message = message,
            Severity = severity,
            Recommendation = recommendation,
            AnalyzerType = analyzerType
        };
    }

    [Fact]
    public void AreResultsConsistent_BothNull_ReturnsFalse()
    {
        Assert.False(AnalysisService.AreResultsConsistent(null, null));
    }

    [Fact]
    public void AreResultsConsistent_ActualNull_ReturnsFalse()
    {
        var expected = new List<AnalysisResult> { CreateSampleResult() };
        Assert.False(AnalysisService.AreResultsConsistent(null, expected));
    }

    [Fact]
    public void AreResultsConsistent_ExpectedNull_ReturnsFalse()
    {
        var actual = new List<AnalysisResult> { CreateSampleResult() };
        Assert.False(AnalysisService.AreResultsConsistent(actual, null));
    }

    [Fact]
    public void AreResultsConsistent_BothEmpty_ReturnsTrue()
    {
        var actual = new List<AnalysisResult>();
        var expected = new List<AnalysisResult>();
        Assert.True(AnalysisService.AreResultsConsistent(actual, expected));
    }

    [Fact]
    public void AreResultsConsistent_CountsMismatch_ReturnsFalse()
    {
        var actual = new List<AnalysisResult> { CreateSampleResult() };
        var expected = new List<AnalysisResult>();
        Assert.False(AnalysisService.AreResultsConsistent(actual, expected));
    }

    [Fact]
    public void AreResultsConsistent_IdenticalSingleResult_ReturnsTrue()
    {
        var actual = new List<AnalysisResult> { CreateSampleResult() };
        var expected = new List<AnalysisResult> { CreateSampleResult() };
        Assert.True(AnalysisService.AreResultsConsistent(actual, expected));
    }

    [Fact]
    public void AreResultsConsistent_IgnoresDatabaseIdAndAnalysisId_ReturnsTrue()
    {
        var actual = new List<AnalysisResult> { CreateSampleResult(id: 101, analysisId: 50) };
        var expected = new List<AnalysisResult> { CreateSampleResult(id: 0, analysisId: 0) };
        Assert.True(AnalysisService.AreResultsConsistent(actual, expected));
    }

    [Fact]
    public void AreResultsConsistent_DifferentOrdering_ReturnsTrue()
    {
        var item1 = CreateSampleResult(rule: "R1", lineNumber: 10);
        var item2 = CreateSampleResult(rule: "R2", lineNumber: 20);

        var actual = new List<AnalysisResult> { item1, item2 };
        var expected = new List<AnalysisResult> { item2, item1 };

        Assert.True(AnalysisService.AreResultsConsistent(actual, expected));
    }

    [Fact]
    public void AreResultsConsistent_BothNullRecommendations_ReturnsTrue()
    {
        var actual = new List<AnalysisResult> { CreateSampleResult(recommendation: null) };
        var expected = new List<AnalysisResult> { CreateSampleResult(recommendation: null) };
        Assert.True(AnalysisService.AreResultsConsistent(actual, expected));
    }

    [Fact]
    public void AreResultsConsistent_RecommendationMismatch_OneNull_ReturnsFalse()
    {
        var actual = new List<AnalysisResult> { CreateSampleResult(recommendation: null) };
        var expected = new List<AnalysisResult> { CreateSampleResult(recommendation: "Fix it") };
        Assert.False(AnalysisService.AreResultsConsistent(actual, expected));
    }

    [Fact]
    public void AreResultsConsistent_RecommendationMismatch_DifferentText_ReturnsFalse()
    {
        var actual = new List<AnalysisResult> { CreateSampleResult(recommendation: "Recommendation A") };
        var expected = new List<AnalysisResult> { CreateSampleResult(recommendation: "Recommendation B") };
        Assert.False(AnalysisService.AreResultsConsistent(actual, expected));
    }

    [Fact]
    public void AreResultsConsistent_SeverityMismatch_ReturnsFalse()
    {
        var actual = new List<AnalysisResult> { CreateSampleResult(severity: Severity.Warning) };
        var expected = new List<AnalysisResult> { CreateSampleResult(severity: Severity.Error) };
        Assert.False(AnalysisService.AreResultsConsistent(actual, expected));
    }

    [Fact]
    public void AreResultsConsistent_RuleMismatch_ReturnsFalse()
    {
        var actual = new List<AnalysisResult> { CreateSampleResult(rule: "RULE_A") };
        var expected = new List<AnalysisResult> { CreateSampleResult(rule: "RULE_B") };
        Assert.False(AnalysisService.AreResultsConsistent(actual, expected));
    }

    [Fact]
    public void AreResultsConsistent_MessageMismatch_ReturnsFalse()
    {
        var actual = new List<AnalysisResult> { CreateSampleResult(message: "Message 1") };
        var expected = new List<AnalysisResult> { CreateSampleResult(message: "Message 2") };
        Assert.False(AnalysisService.AreResultsConsistent(actual, expected));
    }

    [Fact]
    public void AreResultsConsistent_FilePathMismatch_ReturnsFalse()
    {
        var actual = new List<AnalysisResult> { CreateSampleResult(filePath: "PathA.cs") };
        var expected = new List<AnalysisResult> { CreateSampleResult(filePath: "PathB.cs") };
        Assert.False(AnalysisService.AreResultsConsistent(actual, expected));
    }

    [Fact]
    public void AreResultsConsistent_LineNumberMismatch_ReturnsFalse()
    {
        var actual = new List<AnalysisResult> { CreateSampleResult(lineNumber: 10) };
        var expected = new List<AnalysisResult> { CreateSampleResult(lineNumber: 11) };
        Assert.False(AnalysisService.AreResultsConsistent(actual, expected));
    }

    [Fact]
    public void AreResultsConsistent_AnalyzerTypeMismatch_ReturnsFalse()
    {
        var actual = new List<AnalysisResult> { CreateSampleResult(analyzerType: AnalyzerType.Database) };
        var expected = new List<AnalysisResult> { CreateSampleResult(analyzerType: AnalyzerType.Security) };
        Assert.False(AnalysisService.AreResultsConsistent(actual, expected));
    }

    [Fact]
    public void AreResultsConsistent_DuplicateItemsInBoth_ReturnsTrue()
    {
        var dup1Actual = CreateSampleResult();
        var dup2Actual = CreateSampleResult();
        var dup1Expected = CreateSampleResult();
        var dup2Expected = CreateSampleResult();

        var actual = new List<AnalysisResult> { dup1Actual, dup2Actual };
        var expected = new List<AnalysisResult> { dup1Expected, dup2Expected };

        Assert.True(AnalysisService.AreResultsConsistent(actual, expected));
    }

    [Fact]
    public void AreResultsConsistent_DuplicateInActualButNotExpected_ReturnsFalse()
    {
        var item1 = CreateSampleResult(rule: "R1");
        var item2 = CreateSampleResult(rule: "R2");

        var actual = new List<AnalysisResult> { item1, item1 };
        var expected = new List<AnalysisResult> { item1, item2 };

        Assert.False(AnalysisService.AreResultsConsistent(actual, expected));
    }
}
