using Application.Analyzers;
using Application.Interfaces.Repositories;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Moq;

namespace CSharpAnalyzer.Tests;

public class AnalysisServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_CommitExists_CompletesAnalysisAndSavesFindings()
    {
        var analysisRepository = new Mock<IAnalysisRepository>();
        var commitRepository = new Mock<ICommitRepository>();
        var analyzer = new Mock<IAnalyzer>();

        var commit = new Commit
        {
            Id = 1,
            ProjectId = 1,
            CommitHash = "abc123"
        };

        commitRepository
            .Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(commit);

        analyzer
            .Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new AnalyzerFinding
                {
                    FilePath = "Test.cs",
                    LineNumber = 10,
                    Rule = "TEST001",
                    Message = "Test finding",
                    Severity = Severity.Warning,
                    Recommendation = "Test recommendation"
                }
            ]);

        var service = new AnalysisService(
            analysisRepository.Object,
            commitRepository.Object,
            [analyzer.Object]);

        var context = new AnalyzerContext
        {
            RepositoryPath = "C:\\Repository",
            CommitHash = "abc123"
        };

        var result = await service.AnalyzeAsync(1, context);

        Assert.Equal(AnalysisStatus.Completed, result.Status);
        Assert.NotNull(result.CompletedAt);
        Assert.Single(result.Results);

        var finding = result.Results.Single();

        Assert.Equal("Test.cs", finding.FilePath);
        Assert.Equal(10, finding.LineNumber);
        Assert.Equal("TEST001", finding.Rule);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal(AnalyzerType.CSharp, finding.AnalyzerType);

        analysisRepository.Verify(
            x => x.AddAsync(It.IsAny<Analysis>(), It.IsAny<CancellationToken>()),
            Times.Once);

        analysisRepository.Verify(
            x => x.UpdateAsync(It.IsAny<Analysis>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AnalyzeAsync_CommitDoesNotExist_ThrowsException()
    {
        var analysisRepository = new Mock<IAnalysisRepository>();
        var commitRepository = new Mock<ICommitRepository>();
        var analyzer = new Mock<IAnalyzer>();

        commitRepository
            .Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Commit?)null);

        var service = new AnalysisService(
            analysisRepository.Object,
            commitRepository.Object,
            [analyzer.Object]);

        var context = new AnalyzerContext
        {
            RepositoryPath = "C:\\Repository",
            CommitHash = "abc123"
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AnalyzeAsync(1, context));

        Assert.Equal("Коммит не найден.", exception.Message);

        analysisRepository.Verify(
            x => x.AddAsync(It.IsAny<Analysis>(), It.IsAny<CancellationToken>()),
            Times.Never);

        analyzer.Verify(
            x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}