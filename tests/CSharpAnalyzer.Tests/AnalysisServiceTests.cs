using Application.Analyzers;
using Application.Exceptions;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Moq;
using Xunit;

namespace CSharpAnalyzer.Tests;

public class AnalysisServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_CommitExists_CompletesAnalysisAndSavesFindings()
    {
        var analysisRepository = new Mock<IAnalysisRepository>();
        var commitRepository = new Mock<ICommitRepository>();
        var repositorySourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        var commit = new Commit
        {
            Id = 1,
            ProjectId = 1,
            CommitHash = "abc123",
            Project = new Project
            {
                Id = 1,
                Name = "Test",
                RepoUrl = "https://github.com/test/repository",
                OwnerId = 1
            }
        };

        commitRepository.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(commit);

        credentialService.Setup(x => x.GetUserTokenAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        repositorySourceService.Setup(x => x.DownloadAndExtractAsync(
            "https://github.com/test/repository",
            "abc123",
            It.IsAny<int>(),
            "ghp_test_token",
            It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\Repository");

        analyzer.Setup(x => x.AnalyzeAsync(
            It.IsAny<AnalyzerContext>(),
            It.IsAny<CancellationToken>()))
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
            repositorySourceService.Object,
            [analyzer.Object],
            credentialService.Object);

        var result = await service.AnalyzeAsync(1);

        Assert.Equal(AnalysisStatus.Completed, result.Status);
        Assert.NotNull(result.CompletedAt);
        Assert.Single(result.Results);

        var finding = result.Results.Single();

        Assert.Equal("Test.cs", finding.FilePath);
        Assert.Equal(10, finding.LineNumber);
        Assert.Equal("TEST001", finding.Rule);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal(AnalyzerType.CSharp, finding.AnalyzerType);

        analysisRepository.Verify(x => x.AddAsync(
            It.IsAny<Analysis>(),
            It.IsAny<CancellationToken>()),
            Times.Once);

        analysisRepository.Verify(x => x.UpdateAsync(
            It.IsAny<Analysis>(),
            It.IsAny<CancellationToken>()),
            Times.Once);

        repositorySourceService.Verify(x => x.DownloadAndExtractAsync(
            "https://github.com/test/repository",
            "abc123",
            It.IsAny<int>(),
            "ghp_test_token",
            It.IsAny<CancellationToken>()),
            Times.Once);

        repositorySourceService.Verify(x => x.CleanupAsync(
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()),
            Times.Once);

        analyzer.Verify(x => x.AnalyzeAsync(
            It.IsAny<AnalyzerContext>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AnalyzeAsync_CommitDoesNotExist_ThrowsException()
    {
        var analysisRepository = new Mock<IAnalysisRepository>();
        var commitRepository = new Mock<ICommitRepository>();
        var repositorySourceService = new Mock<IRepositorySourceService>();
        var analyzer = new Mock<IAnalyzer>();

        commitRepository
            .Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Commit?)null);

        var service = new AnalysisService(
            analysisRepository.Object,
            commitRepository.Object,
            repositorySourceService.Object,
            [analyzer.Object],
            Mock.Of<IGitHubCredentialService>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AnalyzeAsync(1));

        Assert.Equal("Коммит не найден.", exception.Message);

        analysisRepository.Verify(x => x.AddAsync(
            It.IsAny<Analysis>(),
            It.IsAny<CancellationToken>()),
            Times.Never);

        repositorySourceService.Verify(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()),
            Times.Never);

        analyzer.Verify(x => x.AnalyzeAsync(
            It.IsAny<AnalyzerContext>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AnalyzeAsync_WhenOwnerHasNoPat_ThrowsTokenRequiredException_AndNeverCallsSourceService()
    {
        var analysisRepository = new Mock<IAnalysisRepository>();
        var commitRepository = new Mock<ICommitRepository>();
        var repositorySourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        var commit = new Commit
        {
            Id = 1,
            ProjectId = 1,
            CommitHash = "abc123",
            Project = new Project
            {
                Id = 1,
                Name = "Test",
                RepoUrl = "https://github.com/test/repository",
                OwnerId = 42
            }
        };

        commitRepository.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(commit);
        credentialService.Setup(x => x.GetUserTokenAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        var service = new AnalysisService(
            analysisRepository.Object,
            commitRepository.Object,
            repositorySourceService.Object,
            [analyzer.Object],
            credentialService.Object);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() => service.AnalyzeAsync(1));

        Assert.Equal(GitHubErrorKind.TokenRequired, ex.Kind);
        Assert.Contains("токена GitHub", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Источник не должен вызываться
        repositorySourceService.Verify(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()),
            Times.Never);

        // Анализ должен быть помечен как Failed
        analysisRepository.Verify(x => x.UpdateAsync(
            It.Is<Analysis>(a => a.Status == AnalysisStatus.Failed),
            It.IsAny<CancellationToken>()),
            Times.Once);

        // Cleanup должен быть вызван в finally блоке
        repositorySourceService.Verify(x => x.CleanupAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
