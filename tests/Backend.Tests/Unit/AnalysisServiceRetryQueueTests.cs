using IAnalysisLeaseGuard = Application.Interfaces.IAnalysisLeaseGuard;
using IAnalysisLeaseGuardProvider = Application.Interfaces.IAnalysisLeaseGuardProvider;
using Application.Analyzers;
using Application.Exceptions;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Application.Services;
using DatabaseAnalyzer.Runner;
using Domain.Entities;
using Domain.Enums;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenRetryScheduled_CallsCleanupWhileStillRunning_BeforeScheduleRetryAsync()
    {
        var token = Guid.NewGuid();
        var analysis = CreateRunningAnalysis(id: 52, token: token, attemptCount: 1, maxAttempts: 3);
        var callOrder = new List<string>();
        var currentStatus = AnalysisStatus.Running;
        Guid? currentToken = token;

        var analysisRepo = new Mock<IAnalysisRepository>();
        analysisRepo.Setup(x => x.GetByIdAsync(52, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo.Setup(x => x.RenewLeaseAsync(52, token, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        analysisRepo
            .Setup(x => x.ScheduleRetryAsync(52, token, It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                callOrder.Add("ScheduleRetry");
                currentStatus = AnalysisStatus.Pending;
                currentToken = null;
            })
            .ReturnsAsync(true);

        var guardMock = new Mock<IAnalysisLeaseGuard>();
        guardMock.SetupGet(g => g.IsAcquired).Returns(true);
        guardMock.SetupGet(g => g.AnalysisId).Returns(52);
        guardMock.SetupGet(g => g.ExecutionToken).Returns(token);
        guardMock
            .Setup(g => g.ValidateOwnershipAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int[] allowed, CancellationToken _) =>
                currentToken == token && allowed.Contains((int)currentStatus));

        var guardProvider = new Mock<IAnalysisLeaseGuardProvider>();
        guardProvider.Setup(p => p.AcquireGuardAsync(52, token, It.IsAny<CancellationToken>())).ReturnsAsync(guardMock.Object);

        var sourceService = new Mock<IRepositorySourceService>();
        sourceService
            .Setup(x => x.DownloadAndExtractAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                52,
                token,
                It.IsAny<string>(),
                It.IsAny<IAnalysisLeaseGuard>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Transient upstream reset"));

        sourceService
            .Setup(x => x.CleanupAsync(52, token, guardMock.Object, It.IsAny<CancellationToken>()))
            .Returns<int, Guid, IAnalysisLeaseGuard, CancellationToken>(async (_, _, activeGuard, ct) =>
            {
                Assert.True(await activeGuard.ValidateOwnershipAsync([(int)AnalysisStatus.Running], ct));
                callOrder.Add("Cleanup");
            });

        var credService = new Mock<IGitHubCredentialService>();
        credService.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync("ghp_test_token_12345");

        var service = new AnalysisService(
            analysisRepo.Object,
            new Mock<ICommitRepository>().Object,
            sourceService.Object,
            [new Mock<IAnalyzer>().Object],
            credService.Object,
            guardProvider.Object,
            new AnalysisRetryPolicy());

        var result = await service.ExecuteAnalysisAsync(52, token);

        Assert.False(result);
        Assert.Equal(["Cleanup", "ScheduleRetry"], callOrder);
        sourceService.Verify(x => x.CleanupAsync(52, token, guardMock.Object, It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.ScheduleRetryAsync(52, token, It.IsAny<DateTime>(), It.IsAny<string>(), guardMock.Object, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenPreRetryCleanupLosesOwnership_DoesNotScheduleRetryOrRunFinallyCleanup()
    {
        var token = Guid.NewGuid();
        var analysis = CreateRunningAnalysis(id: 53, token: token, attemptCount: 1, maxAttempts: 3);

        var analysisRepo = new Mock<IAnalysisRepository>();
        analysisRepo.Setup(x => x.GetByIdAsync(53, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo.Setup(x => x.RenewLeaseAsync(53, token, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var sourceService = new Mock<IRepositorySourceService>();
        sourceService
            .Setup(x => x.DownloadAndExtractAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                53,
                token,
                It.IsAny<string>(),
                It.IsAny<IAnalysisLeaseGuard>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Transient network error"));

        sourceService
            .Setup(x => x.CleanupAsync(53, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Потеряно владение рабочим каталогом перед очисткой."));

        var credService = new Mock<IGitHubCredentialService>();
        credService.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync("ghp_test_token_12345");

        var logger = new Mock<ILogger<AnalysisService>>();

        var service = new AnalysisService(
            analysisRepo.Object,
            new Mock<ICommitRepository>().Object,
            sourceService.Object,
            [new Mock<IAnalyzer>().Object],
            credService.Object,
            new InMemoryAnalysisLeaseGuardProvider(),
            new AnalysisRetryPolicy(),
            logger: logger.Object);

        var result = await service.ExecuteAnalysisAsync(53, token);

        Assert.False(result);
        analysisRepo.Verify(
            x => x.ScheduleRetryAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()),
            Times.Never);
        analysisRepo.Verify(
            x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()),
            Times.Never);
        sourceService.Verify(x => x.CleanupAsync(53, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
        logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<InvalidOperationException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenPreRetryCleanupThrowsIOException_DoesNotTransitionToPending()
    {
        var token = Guid.NewGuid();
        var analysis = CreateRunningAnalysis(id: 54, token: token, attemptCount: 1, maxAttempts: 3);

        var analysisRepo = new Mock<IAnalysisRepository>();
        analysisRepo.Setup(x => x.GetByIdAsync(54, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);

        var sourceService = new Mock<IRepositorySourceService>();
        sourceService
            .Setup(x => x.DownloadAndExtractAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                54,
                token,
                It.IsAny<string>(),
                It.IsAny<IAnalysisLeaseGuard>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Transient network error"));

        sourceService
            .Setup(x => x.CleanupAsync(54, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("File handle locked by another process"));

        var credService = new Mock<IGitHubCredentialService>();
        credService.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync("ghp_test_token_12345");

        var service = new AnalysisService(
            analysisRepo.Object,
            new Mock<ICommitRepository>().Object,
            sourceService.Object,
            [new Mock<IAnalyzer>().Object],
            credService.Object,
            new InMemoryAnalysisLeaseGuardProvider(),
            new AnalysisRetryPolicy());

        var result = await service.ExecuteAnalysisAsync(54, token);

        Assert.False(result);
        analysisRepo.Verify(
            x => x.ScheduleRetryAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()),
            Times.Never);
        sourceService.Verify(x => x.CleanupAsync(54, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenPreRetryCleanupSucceedsAndScheduleRetryReturnsFalse_DoesNotCleanupTwice()
    {
        var token = Guid.NewGuid();
        var analysis = CreateRunningAnalysis(id: 55, token: token, attemptCount: 1, maxAttempts: 3);
        var lifecycleEvents = new List<string>();

        var analysisRepo = new Mock<IAnalysisRepository>();
        analysisRepo.Setup(x => x.GetByIdAsync(55, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo
            .Setup(x => x.ScheduleRetryAsync(55, token, It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Callback(() => lifecycleEvents.Add("ScheduleRetry"))
            .ReturnsAsync(false);
        analysisRepo
            .Setup(x => x.FailAsync(55, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Callback<int, Guid, string?, IAnalysisLeaseGuard, CancellationToken>((_, _, err, _, _) =>
            {
                lifecycleEvents.Add("Fail");
                analysis.Status = AnalysisStatus.Failed;
                analysis.CompletedAt = DateTime.UtcNow;
                analysis.LeaseExpiration = null;
                analysis.ErrorMessage = err;
            })
            .ReturnsAsync(true);

        var guardMock = new Mock<IAnalysisLeaseGuard>();
        guardMock.SetupGet(g => g.IsAcquired).Returns(true);
        guardMock.SetupGet(g => g.AnalysisId).Returns(55);
        guardMock.SetupGet(g => g.ExecutionToken).Returns(token);
        guardMock
            .Setup(g => g.ValidateOwnershipAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        guardMock
            .Setup(g => g.DisposeAsync())
            .Callback(() => lifecycleEvents.Add("GuardDispose"))
            .Returns(ValueTask.CompletedTask);

        var guardProvider = new Mock<IAnalysisLeaseGuardProvider>();
        guardProvider.Setup(p => p.AcquireGuardAsync(55, token, It.IsAny<CancellationToken>())).ReturnsAsync(guardMock.Object);

        var sourceService = new Mock<IRepositorySourceService>();
        sourceService
            .Setup(x => x.DownloadAndExtractAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                55,
                token,
                It.IsAny<string>(),
                It.IsAny<IAnalysisLeaseGuard>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Transient network error"));
        sourceService
            .Setup(x => x.CleanupAsync(55, token, guardMock.Object, It.IsAny<CancellationToken>()))
            .Callback(() => lifecycleEvents.Add("Cleanup"))
            .Returns(Task.CompletedTask);

        var credService = new Mock<IGitHubCredentialService>();
        credService.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync("ghp_test_token_12345");
        var logger = new Mock<ILogger<AnalysisService>>();

        var service = new AnalysisService(
            analysisRepo.Object,
            new Mock<ICommitRepository>().Object,
            sourceService.Object,
            [new Mock<IAnalyzer>().Object],
            credService.Object,
            guardProvider.Object,
            new AnalysisRetryPolicy(),
            logger: logger.Object);

        var result = await service.ExecuteAnalysisAsync(55, token);

        Assert.False(result);
        Assert.Equal(AnalysisStatus.Failed, analysis.Status);
        Assert.Equal(token, analysis.ExecutionToken);
        Assert.Equal(["Cleanup", "ScheduleRetry", "Fail", "GuardDispose"], lifecycleEvents);
        analysisRepo.Verify(
            x => x.ScheduleRetryAsync(55, token, It.IsAny<DateTime>(), It.IsAny<string>(), guardMock.Object, It.IsAny<CancellationToken>()),
            Times.Once);
        analysisRepo.Verify(
            x => x.FailAsync(55, token, It.IsAny<string?>(), guardMock.Object, It.IsAny<CancellationToken>()),
            Times.Once);
        sourceService.Verify(x => x.CleanupAsync(55, token, guardMock.Object, It.IsAny<CancellationToken>()), Times.Once);
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
    public async Task ExecuteAnalysisAsync_WhenPreRetryCleanupSucceedsAndScheduleRetryReturnsFalse_AndOwnershipLost_DoesNotWriteDb()
    {
        var token = Guid.NewGuid();
        var foreignToken = Guid.NewGuid();
        var analysis = CreateRunningAnalysis(id: 58, token: token, attemptCount: 1, maxAttempts: 3);

        var analysisRepo = new Mock<IAnalysisRepository>();
        analysisRepo.Setup(x => x.GetByIdAsync(58, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo
            .Setup(x => x.ScheduleRetryAsync(58, token, It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                // Другой воркер уже перехватил задачу
                analysis.ExecutionToken = foreignToken;
                analysis.Status = AnalysisStatus.Running;
            })
            .ReturnsAsync(false);

        var guardMock = new Mock<IAnalysisLeaseGuard>();
        guardMock.SetupGet(g => g.IsAcquired).Returns(true);
        guardMock.SetupGet(g => g.AnalysisId).Returns(58);
        guardMock.SetupGet(g => g.ExecutionToken).Returns(token);
        guardMock
            .Setup(g => g.ValidateOwnershipAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => analysis.ExecutionToken == token && analysis.Status == AnalysisStatus.Running);

        var guardProvider = new Mock<IAnalysisLeaseGuardProvider>();
        guardProvider.Setup(p => p.AcquireGuardAsync(58, token, It.IsAny<CancellationToken>())).ReturnsAsync(guardMock.Object);

        var sourceService = new Mock<IRepositorySourceService>();
        sourceService
            .Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 58, token, It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Transient network error"));

        var credService = new Mock<IGitHubCredentialService>();
        credService.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync("ghp_test_token_12345");
        var logger = new Mock<ILogger<AnalysisService>>();

        var service = new AnalysisService(
            analysisRepo.Object,
            new Mock<ICommitRepository>().Object,
            sourceService.Object,
            [new Mock<IAnalyzer>().Object],
            credService.Object,
            guardProvider.Object,
            new AnalysisRetryPolicy(),
            logger: logger.Object);

        var result = await service.ExecuteAnalysisAsync(58, token);

        Assert.False(result);
        Assert.Equal(AnalysisStatus.Running, analysis.Status);
        Assert.Equal(foreignToken, analysis.ExecutionToken);
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepo.Verify(x => x.UpdateAsync(It.IsAny<Analysis>(), It.IsAny<CancellationToken>()), Times.Never);
        sourceService.Verify(x => x.CleanupAsync(58, token, guardMock.Object, It.IsAny<CancellationToken>()), Times.Once);
        logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenPreRetryCleanupSucceedsAndScheduleRetryThrows_WhileStillOwner_LogsErrorAndFailsFencedWithoutDoubleCleanup()
    {
        var token = Guid.NewGuid();
        var analysis = CreateRunningAnalysis(id: 59, token: token, attemptCount: 1, maxAttempts: 3);
        var lifecycleEvents = new List<string>();

        var analysisRepo = new Mock<IAnalysisRepository>();
        analysisRepo.Setup(x => x.GetByIdAsync(59, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo
            .Setup(x => x.ScheduleRetryAsync(59, token, It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Callback(() => lifecycleEvents.Add("ScheduleRetry"))
            .ThrowsAsync(new InvalidOperationException("Transient Npgsql write timeout"));
        analysisRepo
            .Setup(x => x.FailAsync(59, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Callback<int, Guid, string?, IAnalysisLeaseGuard, CancellationToken>((_, _, err, _, _) =>
            {
                lifecycleEvents.Add("Fail");
                analysis.Status = AnalysisStatus.Failed;
                analysis.CompletedAt = DateTime.UtcNow;
                analysis.LeaseExpiration = null;
                analysis.ErrorMessage = err;
            })
            .ReturnsAsync(true);

        var guardMock = new Mock<IAnalysisLeaseGuard>();
        guardMock.SetupGet(g => g.IsAcquired).Returns(true);
        guardMock.SetupGet(g => g.AnalysisId).Returns(59);
        guardMock.SetupGet(g => g.ExecutionToken).Returns(token);
        guardMock.Setup(g => g.ValidateOwnershipAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        guardMock
            .Setup(g => g.DisposeAsync())
            .Callback(() => lifecycleEvents.Add("GuardDispose"))
            .Returns(ValueTask.CompletedTask);

        var guardProvider = new Mock<IAnalysisLeaseGuardProvider>();
        guardProvider.Setup(p => p.AcquireGuardAsync(59, token, It.IsAny<CancellationToken>())).ReturnsAsync(guardMock.Object);

        var sourceService = new Mock<IRepositorySourceService>();
        sourceService
            .Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 59, token, It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Transient network error"));
        sourceService
            .Setup(x => x.CleanupAsync(59, token, guardMock.Object, It.IsAny<CancellationToken>()))
            .Callback(() => lifecycleEvents.Add("Cleanup"))
            .Returns(Task.CompletedTask);

        var credService = new Mock<IGitHubCredentialService>();
        credService.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync("ghp_test_token_12345");
        var logger = new Mock<ILogger<AnalysisService>>();

        var service = new AnalysisService(
            analysisRepo.Object,
            new Mock<ICommitRepository>().Object,
            sourceService.Object,
            [new Mock<IAnalyzer>().Object],
            credService.Object,
            guardProvider.Object,
            new AnalysisRetryPolicy(),
            logger: logger.Object);

        var result = await service.ExecuteAnalysisAsync(59, token);

        Assert.False(result);
        Assert.Equal(AnalysisStatus.Failed, analysis.Status);
        Assert.Equal(token, analysis.ExecutionToken);
        Assert.Equal(["Cleanup", "ScheduleRetry", "Fail", "GuardDispose"], lifecycleEvents);
        analysisRepo.Verify(x => x.FailAsync(59, token, It.IsAny<string?>(), guardMock.Object, It.IsAny<CancellationToken>()), Times.Once);
        sourceService.Verify(x => x.CleanupAsync(59, token, guardMock.Object, It.IsAny<CancellationToken>()), Times.Once);
        logger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<InvalidOperationException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenPreRetryCleanupSucceedsAndScheduleRetryThrowsAfterCommit_PreservesPendingAndSkipsFailAsync()
    {
        var token = Guid.NewGuid();
        var analysis = CreateRunningAnalysis(id: 60, token: token, attemptCount: 1, maxAttempts: 3);

        var analysisRepo = new Mock<IAnalysisRepository>();
        analysisRepo.Setup(x => x.GetByIdAsync(60, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo
            .Setup(x => x.ScheduleRetryAsync(60, token, It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                analysis.Status = AnalysisStatus.Pending;
                analysis.ExecutionToken = null;
                analysis.LeaseExpiration = null;
            })
            .ThrowsAsync(new IOException("TCP ACK lost after Pending commit"));

        var sourceService = new Mock<IRepositorySourceService>();
        sourceService
            .Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 60, token, It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Transient network error"));

        var credService = new Mock<IGitHubCredentialService>();
        credService.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync("ghp_test_token_12345");
        var logger = new Mock<ILogger<AnalysisService>>();

        var service = new AnalysisService(
            analysisRepo.Object,
            new Mock<ICommitRepository>().Object,
            sourceService.Object,
            [new Mock<IAnalyzer>().Object],
            credService.Object,
            new InMemoryAnalysisLeaseGuardProvider(),
            new AnalysisRetryPolicy(),
            logger: logger.Object);

        var result = await service.ExecuteAnalysisAsync(60, token);

        Assert.False(result);
        Assert.Equal(AnalysisStatus.Pending, analysis.Status);
        Assert.Null(analysis.ExecutionToken);
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepo.Verify(x => x.UpdateAsync(It.IsAny<Analysis>(), It.IsAny<CancellationToken>()), Times.Never);
        sourceService.Verify(x => x.CleanupAsync(60, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
        logger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<IOException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenPreRetryCleanupSucceedsAndScheduleRetryThrowsAndDbUnreachable_FailsClosedAndLogsError()
    {
        var token = Guid.NewGuid();
        var analysis = CreateRunningAnalysis(id: 61, token: token, attemptCount: 1, maxAttempts: 3);

        var getCalls = 0;
        var analysisRepo = new Mock<IAnalysisRepository>();
        analysisRepo
            .Setup(x => x.GetByIdAsync(61, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                getCalls++;
                if (getCalls == 1)
                {
                    return analysis;
                }

                throw new IOException("DB connection lost during recovery inspection");
            });
        analysisRepo
            .Setup(x => x.ScheduleRetryAsync(61, token, It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("DB socket reset during ScheduleRetryAsync"));

        var sourceService = new Mock<IRepositorySourceService>();
        sourceService
            .Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 61, token, It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Transient network error"));

        var credService = new Mock<IGitHubCredentialService>();
        credService.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync("ghp_test_token_12345");
        var logger = new Mock<ILogger<AnalysisService>>();

        var service = new AnalysisService(
            analysisRepo.Object,
            new Mock<ICommitRepository>().Object,
            sourceService.Object,
            [new Mock<IAnalyzer>().Object],
            credService.Object,
            new InMemoryAnalysisLeaseGuardProvider(),
            new AnalysisRetryPolicy(),
            logger: logger.Object);

        var result = await service.ExecuteAnalysisAsync(61, token);

        Assert.False(result);
        Assert.Equal(AnalysisStatus.Running, analysis.Status);
        Assert.Equal(token, analysis.ExecutionToken);
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepo.Verify(x => x.UpdateAsync(It.IsAny<Analysis>(), It.IsAny<CancellationToken>()), Times.Never);
        sourceService.Verify(x => x.CleanupAsync(61, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
        logger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<IOException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenFinallyCleanupHitsDirectoryNotFoundVsUnexpectedIO_LogsOnlyUnexpectedFailure()
    {
        // 1. DirectoryNotFoundException: завершения без лога Warning
        var tokenNotFound = Guid.NewGuid();
        var analysisNotFound = CreateRunningAnalysis(id: 56, token: tokenNotFound);

        var repoNotFound = new Mock<IAnalysisRepository>();
        repoNotFound.Setup(x => x.GetByIdAsync(56, It.IsAny<CancellationToken>())).ReturnsAsync(analysisNotFound);
        repoNotFound.Setup(x => x.CompleteAsync(56, tokenNotFound, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var sourceNotFound = new Mock<IRepositorySourceService>();
        sourceNotFound
            .Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 56, tokenNotFound, It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\mock\\workspace\\56");
        sourceNotFound
            .Setup(x => x.CleanupAsync(56, tokenNotFound, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DirectoryNotFoundException("Already deleted"));

        var analyzerMock = new Mock<IAnalyzer>();
        analyzerMock.Setup(a => a.Type).Returns(AnalyzerType.CSharp);
        analyzerMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var credService = new Mock<IGitHubCredentialService>();
        credService.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync("ghp_test_token_12345");

        var loggerNotFound = new Mock<ILogger<AnalysisService>>();
        var serviceNotFound = new AnalysisService(
            repoNotFound.Object,
            new Mock<ICommitRepository>().Object,
            sourceNotFound.Object,
            [analyzerMock.Object],
            credService.Object,
            new InMemoryAnalysisLeaseGuardProvider(),
            logger: loggerNotFound.Object);

        Assert.True(await serviceNotFound.ExecuteAnalysisAsync(56, tokenNotFound));
        loggerNotFound.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);

        // 2. IOException в finally: логируется Warning, результат Complete не искажается
        var tokenIo = Guid.NewGuid();
        var analysisIo = CreateRunningAnalysis(id: 57, token: tokenIo);

        var repoIo = new Mock<IAnalysisRepository>();
        repoIo.Setup(x => x.GetByIdAsync(57, It.IsAny<CancellationToken>())).ReturnsAsync(analysisIo);
        repoIo.Setup(x => x.CompleteAsync(57, tokenIo, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var sourceIo = new Mock<IRepositorySourceService>();
        sourceIo
            .Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 57, tokenIo, It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\mock\\workspace\\57");
        sourceIo
            .Setup(x => x.CleanupAsync(57, tokenIo, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Unexpected access denied"));

        var loggerIo = new Mock<ILogger<AnalysisService>>();
        var serviceIo = new AnalysisService(
            repoIo.Object,
            new Mock<ICommitRepository>().Object,
            sourceIo.Object,
            [analyzerMock.Object],
            credService.Object,
            new InMemoryAnalysisLeaseGuardProvider(),
            logger: loggerIo.Object);

        Assert.True(await serviceIo.ExecuteAnalysisAsync(57, tokenIo));
        loggerIo.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<IOException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
