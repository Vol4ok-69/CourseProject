using IAnalysisLeaseGuard = Application.Interfaces.IAnalysisLeaseGuard;
using Application.Analyzers;
using Application.Exceptions;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Application.Services;
using DatabaseAnalyzer.Runner;
using Domain.Entities;
using Domain.Enums;
using Moq;
using Xunit;

namespace Backend.Tests.Unit;

public sealed class AnalysisServiceRetryQueueTests
{
    private static Analysis CreateRunningAnalysis(
        int id = 1,
        Guid? token = null,
        int attemptCount = 1,
        int maxAttempts = 3)
    {
        var executionToken = token ?? Guid.NewGuid();
        return new Analysis
        {
            Id = id,
            CommitId = 10,
            Status = AnalysisStatus.Running,
            ExecutionToken = executionToken,
            AttemptCount = attemptCount,
            MaxAttempts = maxAttempts,
            LeaseExpiration = DateTime.UtcNow.AddMinutes(5),
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            Commit = new Commit
            {
                Id = 10,
                ProjectId = 100,
                CommitHash = "abcdef123456",
                Project = new Project
                {
                    Id = 100,
                    Name = "TestRepo",
                    RepoUrl = "https://github.com/org/repo",
                    OwnerId = 1
                }
            }
        };
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenTransientNetworkException_SchedulesRetry()
    {
        var token = Guid.NewGuid();
        var analysis = CreateRunningAnalysis(id: 42, token: token, attemptCount: 1, maxAttempts: 3);

        var analysisRepo = new Mock<IAnalysisRepository>();
        analysisRepo.Setup(x => x.GetByIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo.Setup(x => x.RenewLeaseAsync(42, token, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        analysisRepo.Setup(x => x.ScheduleRetryAsync(42, token, It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        sourceService
            .Setup(x => x.DownloadAndExtractAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<IAnalysisLeaseGuard>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection reset by peer"));

        var analyzer = new Mock<IAnalyzer>();
        var credService = new Mock<IGitHubCredentialService>();
        credService.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync("ghp_test_token_12345");
        var leaseGuardProvider = new InMemoryAnalysisLeaseGuardProvider();

        var service = new AnalysisService(
            analysisRepo.Object,
            commitRepo.Object,
            sourceService.Object,
            [analyzer.Object],
            credService.Object,
            leaseGuardProvider,
            new AnalysisRetryPolicy());

        var result = await service.ExecuteAnalysisAsync(42, token);

        Assert.False(result);
        analysisRepo.Verify(x => x.ScheduleRetryAsync(42, token, It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        sourceService.Verify(x => x.CleanupAsync(42, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenArchiveLimitExceeded_CallsFailAsync_WithoutRetry()
    {
        var token = Guid.NewGuid();
        var analysis = CreateRunningAnalysis(id: 42, token: token, attemptCount: 1, maxAttempts: 3);

        var analysisRepo = new Mock<IAnalysisRepository>();
        analysisRepo.Setup(x => x.GetByIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo.Setup(x => x.RenewLeaseAsync(42, token, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        analysisRepo.Setup(x => x.FailAsync(42, token, It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        sourceService
            .Setup(x => x.DownloadAndExtractAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<IAnalysisLeaseGuard>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArchiveLimitExceededException("Zip bomb threshold exceeded"));

        var analyzer = new Mock<IAnalyzer>();
        var credService = new Mock<IGitHubCredentialService>();
        credService.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync("ghp_test_token_12345");
        var leaseGuardProvider = new InMemoryAnalysisLeaseGuardProvider();

        var service = new AnalysisService(
            analysisRepo.Object,
            commitRepo.Object,
            sourceService.Object,
            [analyzer.Object],
            credService.Object,
            leaseGuardProvider,
            new AnalysisRetryPolicy());

        var result = await service.ExecuteAnalysisAsync(42, token);

        Assert.False(result);
        analysisRepo.Verify(x => x.FailAsync(42, token, It.Is<string>(s => s.Contains("Zip bomb threshold exceeded") || s.Contains("Превышен лимит")), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.ScheduleRetryAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenAttemptCountReachesMax_ClassifiesAsPoisonPillAndCallsFailAsync()
    {
        var token = Guid.NewGuid();
        // attemptCount 3 == maxAttempts 3 -> Poison Pill
        var analysis = CreateRunningAnalysis(id: 42, token: token, attemptCount: 3, maxAttempts: 3);

        var analysisRepo = new Mock<IAnalysisRepository>();
        analysisRepo.Setup(x => x.GetByIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo.Setup(x => x.RenewLeaseAsync(42, token, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        analysisRepo.Setup(x => x.FailAsync(42, token, It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        sourceService
            .Setup(x => x.DownloadAndExtractAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<IAnalysisLeaseGuard>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection timeout"));

        var analyzer = new Mock<IAnalyzer>();
        var credService = new Mock<IGitHubCredentialService>();
        credService.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync("ghp_test_token_12345");
        var leaseGuardProvider = new InMemoryAnalysisLeaseGuardProvider();

        var service = new AnalysisService(
            analysisRepo.Object,
            commitRepo.Object,
            sourceService.Object,
            [analyzer.Object],
            credService.Object,
            leaseGuardProvider,
            new AnalysisRetryPolicy());

        var result = await service.ExecuteAnalysisAsync(42, token);

        Assert.False(result);
        analysisRepo.Verify(x => x.FailAsync(42, token, It.Is<string>(s => s.Contains("Poison Pill")), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.ScheduleRetryAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_AmbiguousCommitAckLoss_RecoversWhenDbStateIsCompleted()
    {
        var token = Guid.NewGuid();
        var runningAnalysis = CreateRunningAnalysis(id: 42, token: token);

        var completedAnalysisInDb = CreateRunningAnalysis(id: 42, token: token);
        completedAnalysisInDb.Status = AnalysisStatus.Completed;
        completedAnalysisInDb.CompletedAt = DateTime.UtcNow;

        var analysisRepo = new Mock<IAnalysisRepository>();
        analysisRepo.Setup(x => x.GetByIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(runningAnalysis);
        analysisRepo.Setup(x => x.GetAnalysisWithProcessIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((completedAnalysisInDb, (int?)12345));

        analysisRepo.Setup(x => x.RenewLeaseAsync(42, token, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Имитируем сбой сети/TCP ACK ровно в момент коммита в СУБД (Q-04)
        analysisRepo
            .Setup(x => x.CompleteAsync(42, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.IO.IOException("Connection reset by peer / Broken TCP socket on COMMIT ACK"));

        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        sourceService
            .Setup(x => x.DownloadAndExtractAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<IAnalysisLeaseGuard>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\mock\\workspace\\42");

        var analyzer = new Mock<IAnalyzer>();
        analyzer.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
        analyzer
            .Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AnalyzerFinding>());

        var credService = new Mock<IGitHubCredentialService>();
        credService.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync("ghp_test_token_12345");
        var leaseGuardProvider = new InMemoryAnalysisLeaseGuardProvider();

        var service = new AnalysisService(
            analysisRepo.Object,
            commitRepo.Object,
            sourceService.Object,
            [analyzer.Object],
            credService.Object,
            leaseGuardProvider,
            new AnalysisRetryPolicy());

        var result = await service.ExecuteAnalysisAsync(42, token);

        // Сервис обязан обнаружить, что задача в БД перешла в Completed, и вернуть true!
        Assert.True(result);

        // Рабочий каталог коммита обязан быть очищен, так как владение подтверждено успехом
        sourceService.Verify(x => x.CleanupAsync(42, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenCommitFailsAndDbStateIsRunning_RethrowsAndSchedulesRetry()
    {
        var token = Guid.NewGuid();
        var runningAnalysis = CreateRunningAnalysis(id: 42, token: token, attemptCount: 1, maxAttempts: 3);

        var analysisRepo = new Mock<IAnalysisRepository>();
        analysisRepo.Setup(x => x.GetByIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(runningAnalysis);
        // Recovery check verifies DB state: task is still Running (commit rolled back)
        analysisRepo.Setup(x => x.GetAnalysisWithProcessIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((runningAnalysis, (int?)12345));

        analysisRepo.Setup(x => x.RenewLeaseAsync(42, token, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        analysisRepo.Setup(x => x.ScheduleRetryAsync(42, token, It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // CompleteAsync throws transient network exception during commit
        analysisRepo
            .Setup(x => x.CompleteAsync(42, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.IO.IOException("Connection reset before commit"));

        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        sourceService
            .Setup(x => x.DownloadAndExtractAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<IAnalysisLeaseGuard>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\mock\\workspace\\42");

        var analyzer = new Mock<IAnalyzer>();
        analyzer.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
        analyzer
            .Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AnalyzerFinding>());

        var credService = new Mock<IGitHubCredentialService>();
        credService.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync("ghp_test_token_12345");
        var leaseGuardProvider = new InMemoryAnalysisLeaseGuardProvider();

        var service = new AnalysisService(
            analysisRepo.Object,
            commitRepo.Object,
            sourceService.Object,
            [analyzer.Object],
            credService.Object,
            leaseGuardProvider,
            new AnalysisRetryPolicy());

        var result = await service.ExecuteAnalysisAsync(42, token);

        Assert.False(result);
        // F51-02: commitEx is rethrown out of recovery block and outer catch handles retry!
        analysisRepo.Verify(x => x.ScheduleRetryAsync(42, token, It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_AmbiguousCommit_WhenDbCheckFails_SuppressesStateChangesAndRetainsWorkspace()
    {
        var token = Guid.NewGuid();
        var runningAnalysis = CreateRunningAnalysis(id: 42, token: token, attemptCount: 1, maxAttempts: 3);

        var analysisRepo = new Mock<IAnalysisRepository>();
        analysisRepo.Setup(x => x.GetByIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(runningAnalysis);

        // CompleteAsync throws during commit
        analysisRepo
            .Setup(x => x.CompleteAsync(42, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.IO.IOException("TCP RST on COMMIT ACK"));

        // DB check also fails (unreachable DB)
        analysisRepo
            .Setup(x => x.GetAnalysisWithProcessIdAsync(42, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.IO.IOException("Database unreachable during recovery inspection"));

        analysisRepo.Setup(x => x.RenewLeaseAsync(42, token, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        sourceService
            .Setup(x => x.DownloadAndExtractAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<IAnalysisLeaseGuard>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\mock\\workspace\\42");

        var analyzer = new Mock<IAnalyzer>();
        analyzer.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
        analyzer
            .Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AnalyzerFinding>());

        var credService = new Mock<IGitHubCredentialService>();
        credService.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync("ghp_test_token_12345");
        var leaseGuardProvider = new InMemoryAnalysisLeaseGuardProvider();

        var service = new AnalysisService(
            analysisRepo.Object,
            commitRepo.Object,
            sourceService.Object,
            [analyzer.Object],
            credService.Object,
            leaseGuardProvider,
            new AnalysisRetryPolicy());

        var result = await service.ExecuteAnalysisAsync(42, token);

        Assert.False(result);
        // F51-03: Ambiguous commit must NEVER trigger blind FailAsync or ScheduleRetryAsync!
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepo.Verify(x => x.ScheduleRetryAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        // Workspace must NOT be deleted
        sourceService.Verify(x => x.CleanupAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_AmbiguousCommit_WhenResultsInconsistent_SuppressesStateChangesAndRetainsWorkspace()
    {
        var token = Guid.NewGuid();
        var runningAnalysis = CreateRunningAnalysis(id: 42, token: token, attemptCount: 1, maxAttempts: 3);

        var completedAnalysisInDb = CreateRunningAnalysis(id: 42, token: token);
        completedAnalysisInDb.Status = AnalysisStatus.Completed;
        completedAnalysisInDb.CompletedAt = DateTime.UtcNow;
        // Inconsistent results: DB has 0 results, while analysis produced 1
        completedAnalysisInDb.Results = new List<AnalysisResult>();

        var analysisRepo = new Mock<IAnalysisRepository>();
        analysisRepo.Setup(x => x.GetByIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(runningAnalysis);
        analysisRepo.Setup(x => x.GetAnalysisWithProcessIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((completedAnalysisInDb, (int?)12345));

        analysisRepo.Setup(x => x.RenewLeaseAsync(42, token, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // CompleteAsync throws during commit
        analysisRepo
            .Setup(x => x.CompleteAsync(42, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.IO.IOException("TCP RST on COMMIT ACK"));

        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        sourceService
            .Setup(x => x.DownloadAndExtractAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<IAnalysisLeaseGuard>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\mock\\workspace\\42");

        var analyzer = new Mock<IAnalyzer>();
        analyzer.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
        analyzer
            .Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AnalyzerFinding>
            {
                new()
                {
                    Rule = "SEC001",
                    Message = "Vulnerability",
                    FilePath = "Program.cs",
                    LineNumber = 10,
                    Severity = Severity.Error
                }
            });

        var credService = new Mock<IGitHubCredentialService>();
        credService.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync("ghp_test_token_12345");
        var leaseGuardProvider = new InMemoryAnalysisLeaseGuardProvider();

        var service = new AnalysisService(
            analysisRepo.Object,
            commitRepo.Object,
            sourceService.Object,
            [analyzer.Object],
            credService.Object,
            leaseGuardProvider,
            new AnalysisRetryPolicy());

        var result = await service.ExecuteAnalysisAsync(42, token);

        Assert.False(result);
        // Ambiguous commit with corrupted/inconsistent results must NEVER trigger blind FailAsync or ScheduleRetryAsync!
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepo.Verify(x => x.ScheduleRetryAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        // Workspace must NOT be deleted
        sourceService.Verify(x => x.CleanupAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
