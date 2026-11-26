using Application.Analyzers;
using Application.Exceptions;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Microsoft.Extensions.Logging;
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
            It.IsAny<Guid>(),
            "ghp_test_token",
            It.IsAny<IAnalysisLeaseGuard>(),
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

        AnalysisStatus? statusAtAdd = null;
        DateTime? leaseExpirationAtAdd = null;
        Guid? tokenAtAdd = null;

        analysisRepository.Setup(x => x.AddAsync(
            It.IsAny<Analysis>(),
            It.IsAny<CancellationToken>()))
            .Callback<Analysis, CancellationToken>((a, _) =>
            {
                statusAtAdd = a.Status;
                leaseExpirationAtAdd = a.LeaseExpiration;
                tokenAtAdd = a.ExecutionToken;
            })
            .Returns(Task.CompletedTask);

        analysisRepository.Setup(x => x.CompleteAsync(
            It.IsAny<int>(),
            It.IsAny<Guid>(),
            It.IsAny<IReadOnlyList<AnalysisResult>>(),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        analysisRepository.Setup(x => x.RenewLeaseAsync(
            It.IsAny<int>(),
            It.IsAny<Guid>(),
            It.IsAny<TimeSpan>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var (guardProvider, guardMock) = CreateMockGuardProviderWithHandle();

        var service = new AnalysisService(
            analysisRepository.Object,
            commitRepository.Object,
            repositorySourceService.Object,
            [analyzer.Object],
            credentialService.Object,
            guardProvider);

        var result = await service.AnalyzeAsync(1);

        Assert.Equal(AnalysisStatus.Running, statusAtAdd);
        Assert.NotNull(leaseExpirationAtAdd);
        Assert.NotNull(tokenAtAdd);
        Assert.NotEqual(Guid.Empty, tokenAtAdd.Value);

        Assert.Equal(AnalysisStatus.Completed, result.Status);
        Assert.NotNull(result.CompletedAt);
        Assert.NotNull(result.LeaseExpiration);
        Assert.True(result.LeaseExpiration > result.StartedAt);
        Assert.NotNull(result.ExecutionToken);
        Assert.NotEqual(Guid.Empty, result.ExecutionToken.Value);
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

        analysisRepository.Verify(x => x.CompleteAsync(
            It.IsAny<int>(),
            It.IsAny<Guid>(),
            It.Is<IReadOnlyList<AnalysisResult>>(r => r.Count == 1),
            guardMock.Object,
            It.IsAny<CancellationToken>()),
            Times.Once);

        analysisRepository.Verify(x => x.UpdateAsync(
            It.IsAny<Analysis>(),
            It.IsAny<CancellationToken>()),
            Times.Never);

        guardMock.Verify(g => g.ValidateOwnershipAsync(
            It.IsAny<int[]>(),
            It.IsAny<CancellationToken>()),
            Times.AtLeast(3));

        repositorySourceService.Verify(x => x.DownloadAndExtractAsync(
            "https://github.com/test/repository",
            "abc123",
            It.IsAny<int>(),
            It.IsAny<Guid>(),
            "ghp_test_token",
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()),
            Times.Once);

        repositorySourceService.Verify(x => x.CleanupAsync(
            It.IsAny<int>(),
            It.IsAny<Guid>(),
            It.IsAny<IAnalysisLeaseGuard>(),
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
            Mock.Of<IGitHubCredentialService>(),
            CreateMockGuardProvider());

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
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<IAnalysisLeaseGuard>(),
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

        analysisRepository.Setup(x => x.FailAsync(
            It.IsAny<int>(),
            It.IsAny<Guid>(),
            It.IsAny<string?>(),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var (guardProvider, guardMock) = CreateMockGuardProviderWithHandle();

        var service = new AnalysisService(
            analysisRepository.Object,
            commitRepository.Object,
            repositorySourceService.Object,
            [analyzer.Object],
            credentialService.Object,
            guardProvider);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() => service.AnalyzeAsync(1));

        Assert.Equal(GitHubErrorKind.TokenRequired, ex.Kind);
        Assert.Contains("токена GitHub", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Источник не должен вызываться
        repositorySourceService.Verify(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()),
            Times.Never);

        // Анализ должен быть помечен как Failed через fenced FailAsync(guard), а не через UpdateAsync
        analysisRepository.Verify(x => x.FailAsync(
            It.IsAny<int>(),
            It.IsAny<Guid>(),
            It.Is<string?>(s => !string.IsNullOrWhiteSpace(s)),
            guardMock.Object,
            It.IsAny<CancellationToken>()),
            Times.Once);

        analysisRepository.Verify(x => x.UpdateAsync(
            It.IsAny<Analysis>(),
            It.IsAny<CancellationToken>()),
            Times.Never);

        // Cleanup должен быть вызван в finally блоке
        repositorySourceService.Verify(x => x.CleanupAsync(
            It.IsAny<int>(),
            It.IsAny<Guid>(),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AnalyzeAsync_WhenGuardProviderReturnsNull_FailsStartupWithoutLeavingRunning()
    {
        Analysis? storedAnalysis = null;
        var analysisRepository = CreateStatefulAnalysisRepoMock(() => storedAnalysis, a => storedAnalysis = a);
        var commitRepository = new Mock<ICommitRepository>();
        var repositorySourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        commitRepository.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCommit());

        var guardProvider = new Mock<IAnalysisLeaseGuardProvider>();
        guardProvider.Setup(p => p.AcquireGuardAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IAnalysisLeaseGuard)null!);

        var service = new AnalysisService(
            analysisRepository.Object,
            commitRepository.Object,
            repositorySourceService.Object,
            [analyzer.Object],
            credentialService.Object,
            guardProvider.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AnalyzeAsync(1));

        Assert.NotNull(storedAnalysis);
        Assert.Equal(AnalysisStatus.Failed, storedAnalysis!.Status);
        Assert.Null(storedAnalysis.LeaseExpiration);
        Assert.NotNull(storedAnalysis.CompletedAt);
        Assert.False(string.IsNullOrWhiteSpace(storedAnalysis.ErrorMessage));
        analysisRepository.Verify(x => x.TryFailStartupWithoutGuardAsync(storedAnalysis.Id, storedAnalysis.ExecutionToken!.Value, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        analysisRepository.Verify(x => x.UpdateAsync(It.IsAny<Analysis>(), It.IsAny<CancellationToken>()), Times.Never);
        repositorySourceService.Verify(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        repositorySourceService.Verify(x => x.CleanupAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analyzer.Verify(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnalyzeAsync_WhenGuardNotAcquired_ThrowsAndNeverTouchesFilesOrDbResults()
    {
        Analysis? storedAnalysis = null;
        var analysisRepository = CreateStatefulAnalysisRepoMock(() => storedAnalysis, a => storedAnalysis = a);
        var commitRepository = new Mock<ICommitRepository>();
        var repositorySourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        commitRepository.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCommit());

        var unacquiredGuard = new Mock<IAnalysisLeaseGuard>();
        unacquiredGuard.Setup(g => g.IsAcquired).Returns(false);

        var guardProvider = new Mock<IAnalysisLeaseGuardProvider>();
        guardProvider.Setup(p => p.AcquireGuardAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(unacquiredGuard.Object);

        var service = new AnalysisService(
            analysisRepository.Object,
            commitRepository.Object,
            repositorySourceService.Object,
            [analyzer.Object],
            credentialService.Object,
            guardProvider.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AnalyzeAsync(1));

        Assert.NotNull(storedAnalysis);
        Assert.Equal(AnalysisStatus.Failed, storedAnalysis!.Status);
        Assert.Null(storedAnalysis.LeaseExpiration);
        unacquiredGuard.Verify(g => g.DisposeAsync(), Times.Once);
        analysisRepository.Verify(x => x.TryFailStartupWithoutGuardAsync(storedAnalysis.Id, storedAnalysis.ExecutionToken!.Value, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        repositorySourceService.Verify(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()),
            Times.Never);

        repositorySourceService.Verify(x => x.CleanupAsync(
            It.IsAny<int>(),
            It.IsAny<Guid>(),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()),
            Times.Never);

        analyzer.Verify(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepository.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepository.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepository.Verify(x => x.UpdateAsync(It.IsAny<Analysis>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnalyzeAsync_WhenGuardProviderThrowsException_FailsStartupSafelyAndRethrows()
    {
        Analysis? storedAnalysis = null;
        var analysisRepository = CreateStatefulAnalysisRepoMock(() => storedAnalysis, a => storedAnalysis = a);
        var commitRepository = new Mock<ICommitRepository>();
        var repositorySourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        commitRepository.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCommit());

        var guardProvider = new Mock<IAnalysisLeaseGuardProvider>();
        guardProvider.Setup(p => p.AcquireGuardAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("PostgreSQL lock acquire timed out"));

        var service = new AnalysisService(
            analysisRepository.Object,
            commitRepository.Object,
            repositorySourceService.Object,
            [analyzer.Object],
            credentialService.Object,
            guardProvider.Object);

        await Assert.ThrowsAsync<TimeoutException>(() => service.AnalyzeAsync(1));

        Assert.NotNull(storedAnalysis);
        Assert.Equal(AnalysisStatus.Failed, storedAnalysis!.Status);
        Assert.Null(storedAnalysis.LeaseExpiration);
        Assert.Contains("PostgreSQL lock acquire timed out", storedAnalysis.ErrorMessage);
        analysisRepository.Verify(x => x.TryFailStartupWithoutGuardAsync(storedAnalysis.Id, storedAnalysis.ExecutionToken!.Value, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        analysisRepository.Verify(x => x.UpdateAsync(It.IsAny<Analysis>(), It.IsAny<CancellationToken>()), Times.Never);
        repositorySourceService.Verify(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        repositorySourceService.Verify(x => x.CleanupAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnalyzeAsync_WhenOwnershipLostBeforeGuardFailureCleanup_DoesNotModifyForeignRow()
    {
        Analysis? storedAnalysis = null;
        var foreignToken = Guid.NewGuid();
        var analysisRepository = CreateStatefulAnalysisRepoMock(() => storedAnalysis, a => storedAnalysis = a);
        var commitRepository = new Mock<ICommitRepository>();
        var repositorySourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();
        var logger = new Mock<ILogger<AnalysisService>>();

        commitRepository.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCommit());

        var unacquiredGuard = new Mock<IAnalysisLeaseGuard>();
        unacquiredGuard.Setup(g => g.IsAcquired).Returns(false);

        var guardProvider = new Mock<IAnalysisLeaseGuardProvider>();
        guardProvider.Setup(p => p.AcquireGuardAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                // Сторонний исполнитель перехватил запись до завершения AcquireGuardAsync
                storedAnalysis!.ExecutionToken = foreignToken;
                storedAnalysis.Status = AnalysisStatus.Running;
                return unacquiredGuard.Object;
            });

        var service = new AnalysisService(
            analysisRepository.Object,
            commitRepository.Object,
            repositorySourceService.Object,
            [analyzer.Object],
            credentialService.Object,
            guardProvider.Object,
            logger: logger.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AnalyzeAsync(1));

        Assert.NotNull(storedAnalysis);
        Assert.Equal(AnalysisStatus.Running, storedAnalysis!.Status);
        Assert.Equal(foreignToken, storedAnalysis.ExecutionToken);
        analysisRepository.Verify(x => x.UpdateAsync(It.IsAny<Analysis>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepository.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task AnalyzeAsync_WhenGuardFailsAndDatabaseUnavailable_FailClosedAndLogsError()
    {
        var analysisRepository = new Mock<IAnalysisRepository>();
        analysisRepository.Setup(x => x.AddAsync(It.IsAny<Analysis>(), It.IsAny<CancellationToken>()))
            .Callback<Analysis, CancellationToken>((a, _) => a.Id = 77)
            .Returns(Task.CompletedTask);
        analysisRepository.Setup(x => x.TryFailStartupWithoutGuardAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("PostgreSQL unavailable during startup fail"));

        var commitRepository = new Mock<ICommitRepository>();
        commitRepository.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCommit());

        var repositorySourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();
        var logger = new Mock<ILogger<AnalysisService>>();

        var unacquiredGuard = new Mock<IAnalysisLeaseGuard>();
        unacquiredGuard.Setup(g => g.IsAcquired).Returns(false);

        var guardProvider = new Mock<IAnalysisLeaseGuardProvider>();
        guardProvider.Setup(p => p.AcquireGuardAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(unacquiredGuard.Object);

        var service = new AnalysisService(
            analysisRepository.Object,
            commitRepository.Object,
            repositorySourceService.Object,
            [analyzer.Object],
            credentialService.Object,
            guardProvider.Object,
            logger: logger.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AnalyzeAsync(1));

        analysisRepository.Verify(x => x.UpdateAsync(It.IsAny<Analysis>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepository.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        repositorySourceService.Verify(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        logger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<IOException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    private static Mock<IAnalysisRepository> CreateStatefulAnalysisRepoMock(
        Func<Analysis?> getStored,
        Action<Analysis> setStored)
    {
        var repo = new Mock<IAnalysisRepository>();
        repo.Setup(x => x.AddAsync(It.IsAny<Analysis>(), It.IsAny<CancellationToken>()))
            .Callback<Analysis, CancellationToken>((a, _) =>
            {
                a.Id = 77;
                setStored(new Analysis
                {
                    Id = a.Id,
                    CommitId = a.CommitId,
                    Status = a.Status,
                    CreatedAt = a.CreatedAt,
                    StartedAt = a.StartedAt,
                    LeaseExpiration = a.LeaseExpiration,
                    ExecutionToken = a.ExecutionToken,
                    AttemptCount = a.AttemptCount,
                    MaxAttempts = a.MaxAttempts,
                    ErrorMessage = a.ErrorMessage
                });
            })
            .Returns(Task.CompletedTask);

        repo.Setup(x => x.TryFailStartupWithoutGuardAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, Guid token, string error, CancellationToken _) =>
            {
                var current = getStored();
                if (current == null || current.Id != id || current.ExecutionToken != token || current.Status != AnalysisStatus.Running)
                {
                    return false;
                }

                current.Status = AnalysisStatus.Failed;
                current.CompletedAt = DateTime.UtcNow;
                current.LeaseExpiration = null;
                current.ErrorMessage = error;
                return true;
            });

        return repo;
    }

    [Fact]
    public async Task AnalyzeAsync_WhenInitialOwnershipValidationFails_ThrowsAndNeverTouchesFilesOrDbResults()
    {
        var analysisRepository = new Mock<IAnalysisRepository>();
        var commitRepository = new Mock<ICommitRepository>();
        var repositorySourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        commitRepository.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCommit());

        var guard = new Mock<IAnalysisLeaseGuard>();
        guard.Setup(g => g.IsAcquired).Returns(true);
        guard.Setup(g => g.ValidateOwnershipAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var guardProvider = new Mock<IAnalysisLeaseGuardProvider>();
        guardProvider.Setup(p => p.AcquireGuardAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(guard.Object);

        var service = new AnalysisService(
            analysisRepository.Object,
            commitRepository.Object,
            repositorySourceService.Object,
            [analyzer.Object],
            credentialService.Object,
            guardProvider.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AnalyzeAsync(1));

        repositorySourceService.Verify(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()),
            Times.Never);

        repositorySourceService.Verify(x => x.CleanupAsync(
            It.IsAny<int>(),
            It.IsAny<Guid>(),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()),
            Times.Never);

        analyzer.Verify(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepository.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepository.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepository.Verify(x => x.UpdateAsync(It.IsAny<Analysis>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnalyzeAsync_WhenOwnershipLostBetweenAnalyzers_AbortsWithoutPersistOrCleanup()
    {
        var analysisRepository = new Mock<IAnalysisRepository>();
        var commitRepository = new Mock<ICommitRepository>();
        var repositorySourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var firstAnalyzer = new Mock<IAnalyzer>();
        var secondAnalyzer = new Mock<IAnalyzer>();

        commitRepository.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCommit());
        credentialService.Setup(x => x.GetUserTokenAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");
        repositorySourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\Repository");

        firstAnalyzer.Setup(a => a.Type).Returns(AnalyzerType.CSharp);
        firstAnalyzer.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        secondAnalyzer.Setup(a => a.Type).Returns(AnalyzerType.Sql);

        var validationCalls = 0;
        var guard = new Mock<IAnalysisLeaseGuard>();
        guard.Setup(g => g.IsAcquired).Returns(true);
        guard.Setup(g => g.ValidateOwnershipAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                validationCalls++;
                // 1: initial validation, 2: before firstAnalyzer; 3: before secondAnalyzer -> false
                return validationCalls <= 2;
            });

        var guardProvider = new Mock<IAnalysisLeaseGuardProvider>();
        guardProvider.Setup(p => p.AcquireGuardAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(guard.Object);

        var service = new AnalysisService(
            analysisRepository.Object,
            commitRepository.Object,
            repositorySourceService.Object,
            [firstAnalyzer.Object, secondAnalyzer.Object],
            credentialService.Object,
            guardProvider.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AnalyzeAsync(1));

        firstAnalyzer.Verify(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()), Times.Once);
        secondAnalyzer.Verify(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepository.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepository.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepository.Verify(x => x.UpdateAsync(It.IsAny<Analysis>(), It.IsAny<CancellationToken>()), Times.Never);
        repositorySourceService.Verify(x => x.CleanupAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Commit CreateCommit() => new()
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

    private static IAnalysisLeaseGuardProvider CreateMockGuardProvider()
        => CreateMockGuardProviderWithHandle().Provider;

    private static (IAnalysisLeaseGuardProvider Provider, Mock<IAnalysisLeaseGuard> Guard) CreateMockGuardProviderWithHandle()
    {
        var guard = new Mock<IAnalysisLeaseGuard>();
        guard.Setup(g => g.IsAcquired).Returns(true);
        guard.Setup(g => g.ValidateOwnershipAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        guard.Setup(g => g.ValidateOwnershipAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var provider = new Mock<IAnalysisLeaseGuardProvider>();
        provider.Setup(p => p.AcquireGuardAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(guard.Object);
        return (provider.Object, guard);
    }
}
