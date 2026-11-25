using System.Collections.Concurrent;
using System.Data.Common;
using Application.Analyzers;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Moq;
using Xunit;

namespace Backend.Tests.Unit;

public sealed class AnalysisLeaseRenewalLifecycleTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ScenarioA_OwnershipLostDuringAnalyzerLoop_CancelsAndAwaitsRenewalBeforeDisposingGuard()
    {
        var events = new ConcurrentQueue<string>();
        var renewalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRenewalFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var executionToken = Guid.NewGuid();
        var analysis = CreateRunningAnalysis(101, executionToken);

        var ownershipLost = false;
        var guard = new LifecycleTrackingLeaseGuard(
            analysis.Id,
            executionToken,
            onValidateOwnership: (_, _) => Task.FromResult(!Volatile.Read(ref ownershipLost)),
            onDispose: () => events.Enqueue("guard_disposed"));

        var analysisRepo = CreateBaseAnalysisRepoMock(analysis);
        analysisRepo
            .Setup(x => x.RenewLeaseAsync(analysis.Id, executionToken, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<int, Guid, TimeSpan, CancellationToken>(async (_, _, _, ct) =>
            {
                events.Enqueue("renewal_started");
                renewalEntered.TrySetResult();
                using var _ = ct.Register(() =>
                {
                    events.Enqueue("renewal_cancelled");
                    renewalCancelled.TrySetResult();
                });

                await allowRenewalFinish.Task.WaitAsync(TestTimeout);
                events.Enqueue("renewal_finished");
                return true;
            });

        var analyzer1 = new Mock<IAnalyzer>();
        analyzer1.SetupGet(x => x.Type).Returns(AnalyzerType.CSharp);
        analyzer1.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns<AnalyzerContext, CancellationToken>(async (_, _) =>
            {
                await renewalEntered.Task.WaitAsync(TestTimeout);
                Volatile.Write(ref ownershipLost, true);
                return [];
            });

        var analyzer2 = new Mock<IAnalyzer>();
        analyzer2.SetupGet(x => x.Type).Returns(AnalyzerType.Security);

        var service = CreateService(analysisRepo.Object, guard, [analyzer1.Object, analyzer2.Object]);

        var executeTask = service.ExecuteAnalysisAsync(
            analysis.Id,
            executionToken,
            CancellationToken.None,
            TimeSpan.FromMilliseconds(30));

        await renewalCancelled.Task.WaitAsync(TestTimeout);
        Assert.False(executeTask.IsCompleted);
        Assert.Equal(0, guard.DisposeCount);

        allowRenewalFinish.TrySetResult();
        var result = await executeTask.WaitAsync(TestTimeout);

        Assert.False(result);
        Assert.Equal(1, guard.DisposeCount);
        analyzer2.Verify(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()), Times.Never);

        var ordered = events.ToArray();
        Assert.True(Array.IndexOf(ordered, "renewal_cancelled") < Array.IndexOf(ordered, "renewal_finished"));
        Assert.True(Array.IndexOf(ordered, "renewal_finished") < Array.IndexOf(ordered, "guard_disposed"));
    }

    [Fact]
    public async Task ScenarioB_ValidateOwnershipThrowsException_CancelsAndAwaitsRenewalBeforeDisposingGuard()
    {
        var events = new ConcurrentQueue<string>();
        var renewalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRenewalFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var executionToken = Guid.NewGuid();
        var analysis = CreateRunningAnalysis(102, executionToken);

        var shouldThrowOnValidate = false;
        var guard = new LifecycleTrackingLeaseGuard(
            analysis.Id,
            executionToken,
            onValidateOwnership: (_, _) =>
            {
                if (Volatile.Read(ref shouldThrowOnValidate))
                {
                    throw new InvalidOperationException("DB connection lost during ownership check");
                }

                return Task.FromResult(true);
            },
            onDispose: () => events.Enqueue("guard_disposed"));

        var analysisRepo = CreateBaseAnalysisRepoMock(analysis);
        analysisRepo
            .Setup(x => x.RenewLeaseAsync(analysis.Id, executionToken, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<int, Guid, TimeSpan, CancellationToken>(async (_, _, _, ct) =>
            {
                events.Enqueue("renewal_started");
                renewalEntered.TrySetResult();
                using var _ = ct.Register(() =>
                {
                    events.Enqueue("renewal_cancelled");
                    renewalCancelled.TrySetResult();
                });

                await allowRenewalFinish.Task.WaitAsync(TestTimeout);
                events.Enqueue("renewal_finished");
                return true;
            });

        var analyzer1 = new Mock<IAnalyzer>();
        analyzer1.SetupGet(x => x.Type).Returns(AnalyzerType.CSharp);
        analyzer1.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns<AnalyzerContext, CancellationToken>(async (_, _) =>
            {
                await renewalEntered.Task.WaitAsync(TestTimeout);
                Volatile.Write(ref shouldThrowOnValidate, true);
                return [];
            });

        var analyzer2 = new Mock<IAnalyzer>();
        analyzer2.SetupGet(x => x.Type).Returns(AnalyzerType.Security);

        var service = CreateService(analysisRepo.Object, guard, [analyzer1.Object, analyzer2.Object]);

        var executeTask = service.ExecuteAnalysisAsync(
            analysis.Id,
            executionToken,
            CancellationToken.None,
            TimeSpan.FromMilliseconds(30));

        await renewalCancelled.Task.WaitAsync(TestTimeout);
        Assert.False(executeTask.IsCompleted);
        Assert.Equal(0, guard.DisposeCount);

        allowRenewalFinish.TrySetResult();
        var result = await executeTask.WaitAsync(TestTimeout);

        Assert.False(result);
        Assert.Equal(1, guard.DisposeCount);

        var ordered = events.ToArray();
        Assert.True(Array.IndexOf(ordered, "renewal_cancelled") < Array.IndexOf(ordered, "renewal_finished"));
        Assert.True(Array.IndexOf(ordered, "renewal_finished") < Array.IndexOf(ordered, "guard_disposed"));
    }

    [Fact]
    public async Task ScenarioC_AnalyzerThrowsException_CancelsAndAwaitsRenewal_AndPreservesOriginalError()
    {
        var events = new ConcurrentQueue<string>();
        var renewalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRenewalFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var executionToken = Guid.NewGuid();
        var analysis = CreateRunningAnalysis(103, executionToken);
        string? capturedFailMessage = null;

        var guard = new LifecycleTrackingLeaseGuard(
            analysis.Id,
            executionToken,
            onValidateOwnership: (_, _) => Task.FromResult(true),
            onDispose: () => events.Enqueue("guard_disposed"));

        var analysisRepo = CreateBaseAnalysisRepoMock(analysis);
        analysisRepo
            .Setup(x => x.RenewLeaseAsync(analysis.Id, executionToken, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<int, Guid, TimeSpan, CancellationToken>(async (_, _, _, ct) =>
            {
                renewalEntered.TrySetResult();
                using var _ = ct.Register(() =>
                {
                    events.Enqueue("renewal_cancelled");
                    renewalCancelled.TrySetResult();
                });

                await allowRenewalFinish.Task.WaitAsync(TestTimeout);
                events.Enqueue("renewal_finished");
                return true;
            });

        analysisRepo
            .Setup(x => x.FailAsync(analysis.Id, executionToken, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Callback<int, Guid, string?, IAnalysisLeaseGuard, CancellationToken>((_, _, msg, _, _) => capturedFailMessage = msg)
            .ReturnsAsync(true);

        var analyzer = new Mock<IAnalyzer>();
        analyzer.SetupGet(x => x.Type).Returns(AnalyzerType.CSharp);
        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns<AnalyzerContext, CancellationToken>(async (_, _) =>
            {
                await renewalEntered.Task.WaitAsync(TestTimeout);
                throw new InvalidOperationException("Original analyzer failure");
            });

        var service = CreateService(analysisRepo.Object, guard, [analyzer.Object]);

        var executeTask = service.ExecuteAnalysisAsync(
            analysis.Id,
            executionToken,
            CancellationToken.None,
            TimeSpan.FromMilliseconds(30));

        await renewalCancelled.Task.WaitAsync(TestTimeout);
        Assert.False(executeTask.IsCompleted);
        Assert.Equal(0, guard.DisposeCount);

        allowRenewalFinish.TrySetResult();
        var result = await executeTask.WaitAsync(TestTimeout);

        Assert.False(result);
        Assert.Equal(1, guard.DisposeCount);
        Assert.NotNull(capturedFailMessage);
        Assert.Contains("Original analyzer failure", capturedFailMessage);

        var ordered = events.ToArray();
        Assert.True(Array.IndexOf(ordered, "renewal_finished") < Array.IndexOf(ordered, "guard_disposed"));
    }

    [Fact]
    public async Task ScenarioD_ExternalCancellation_AwaitsRenewalCompletionBeforeDisposingGuard()
    {
        var events = new ConcurrentQueue<string>();
        var renewalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRenewalFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var executionToken = Guid.NewGuid();
        var analysis = CreateRunningAnalysis(104, executionToken);
        using var externalCts = new CancellationTokenSource();

        var guard = new LifecycleTrackingLeaseGuard(
            analysis.Id,
            executionToken,
            onValidateOwnership: (_, _) => Task.FromResult(true),
            onDispose: () => events.Enqueue("guard_disposed"));

        var analysisRepo = CreateBaseAnalysisRepoMock(analysis);
        analysisRepo
            .Setup(x => x.RenewLeaseAsync(analysis.Id, executionToken, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<int, Guid, TimeSpan, CancellationToken>(async (_, _, _, ct) =>
            {
                renewalEntered.TrySetResult();
                using var _ = ct.Register(() =>
                {
                    events.Enqueue("renewal_cancelled");
                    renewalCancelled.TrySetResult();
                });

                await allowRenewalFinish.Task.WaitAsync(TestTimeout);
                events.Enqueue("renewal_finished");
                return true;
            });

        var analyzer = new Mock<IAnalyzer>();
        analyzer.SetupGet(x => x.Type).Returns(AnalyzerType.CSharp);
        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns<AnalyzerContext, CancellationToken>(async (_, ct) =>
            {
                await renewalEntered.Task.WaitAsync(TestTimeout);
                externalCts.Cancel();
                ct.ThrowIfCancellationRequested();
                return [];
            });

        var service = CreateService(analysisRepo.Object, guard, [analyzer.Object]);

        var executeTask = service.ExecuteAnalysisAsync(
            analysis.Id,
            executionToken,
            externalCts.Token,
            TimeSpan.FromMilliseconds(30));

        await renewalCancelled.Task.WaitAsync(TestTimeout);
        Assert.False(executeTask.IsCompleted);
        Assert.Equal(0, guard.DisposeCount);

        allowRenewalFinish.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executeTask.WaitAsync(TestTimeout));

        Assert.Equal(1, guard.DisposeCount);
        var ordered = events.ToArray();
        Assert.True(Array.IndexOf(ordered, "renewal_finished") < Array.IndexOf(ordered, "guard_disposed"));
    }

    [Fact]
    public async Task ScenarioE_RenewalTaskThrowsException_IsObservedAndCleanedUpOnceWithoutReDisposing()
    {
        var events = new ConcurrentQueue<string>();
        var renewalFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var executionToken = Guid.NewGuid();
        var analysis = CreateRunningAnalysis(105, executionToken);
        string? capturedFailMessage = null;

        var guard = new LifecycleTrackingLeaseGuard(
            analysis.Id,
            executionToken,
            onValidateOwnership: (_, _) => Task.FromResult(true),
            onDispose: () => events.Enqueue("guard_disposed"));

        var analysisRepo = CreateBaseAnalysisRepoMock(analysis);
        analysisRepo
            .Setup(x => x.RenewLeaseAsync(analysis.Id, executionToken, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<int, Guid, TimeSpan, CancellationToken>((_, _, _, _) =>
            {
                events.Enqueue("renewal_threw");
                renewalFailed.TrySetResult();
                throw new InvalidOperationException("Renewal DB timeout");
            });

        analysisRepo
            .Setup(x => x.FailAsync(analysis.Id, executionToken, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Callback<int, Guid, string?, IAnalysisLeaseGuard, CancellationToken>((_, _, msg, _, _) => capturedFailMessage = msg)
            .ReturnsAsync(true);

        var analyzer = new Mock<IAnalyzer>();
        analyzer.SetupGet(x => x.Type).Returns(AnalyzerType.CSharp);
        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns<AnalyzerContext, CancellationToken>(async (_, ct) =>
            {
                await renewalFailed.Task.WaitAsync(TestTimeout);
                var canceledTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = ct.Register(() => canceledTcs.TrySetResult());
                await canceledTcs.Task.WaitAsync(TestTimeout);
                ct.ThrowIfCancellationRequested();
                return [];
            });

        var service = CreateService(analysisRepo.Object, guard, [analyzer.Object]);

        var unobservedExceptions = new ConcurrentQueue<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            unobservedExceptions.Enqueue(e.Exception);
            e.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var result = await service.ExecuteAnalysisAsync(
                analysis.Id,
                executionToken,
                CancellationToken.None,
                TimeSpan.FromMilliseconds(30)).WaitAsync(TestTimeout);

            Assert.False(result);
            Assert.Equal(1, guard.DisposeCount);
            Assert.NotNull(capturedFailMessage);
            Assert.Contains("Renewal DB timeout", capturedFailMessage);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.Empty(unobservedExceptions);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [Fact]
    public async Task ScenarioF_CancellationCallbackThrowsException_StillAwaitsRenewalBeforeDisposingGuard()
    {
        var events = new ConcurrentQueue<string>();
        var renewalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRenewalFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var executionToken = Guid.NewGuid();
        var analysis = CreateRunningAnalysis(106, executionToken);

        var guard = new LifecycleTrackingLeaseGuard(
            analysis.Id,
            executionToken,
            onValidateOwnership: (_, _) => Task.FromResult(true),
            onDispose: () => events.Enqueue("guard_disposed"));

        var analysisRepo = CreateBaseAnalysisRepoMock(analysis);
        analysisRepo
            .Setup(x => x.RenewLeaseAsync(analysis.Id, executionToken, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<int, Guid, TimeSpan, CancellationToken>(async (_, _, _, _) =>
            {
                events.Enqueue("renewal_started");
                renewalEntered.TrySetResult();
                await allowRenewalFinish.Task.WaitAsync(TestTimeout);
                events.Enqueue("renewal_finished");
                return true;
            });

        analysisRepo
            .Setup(x => x.CompleteAsync(
                analysis.Id,
                executionToken,
                It.IsAny<IReadOnlyList<AnalysisResult>>(),
                It.IsAny<IAnalysisLeaseGuard>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var analyzer = new Mock<IAnalyzer>();
        analyzer.SetupGet(x => x.Type).Returns(AnalyzerType.CSharp);
        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns<AnalyzerContext, CancellationToken>(async (_, ct) =>
            {
                ct.Register(() =>
                {
                    events.Enqueue("callback_threw");
                    callbackInvoked.TrySetResult();
                    throw new InvalidOperationException("Cancellation callback failure");
                });

                await renewalEntered.Task.WaitAsync(TestTimeout);
                return [];
            });

        var service = CreateService(analysisRepo.Object, guard, [analyzer.Object]);

        var executeTask = service.ExecuteAnalysisAsync(
            analysis.Id,
            executionToken,
            CancellationToken.None,
            TimeSpan.FromMilliseconds(30));

        await callbackInvoked.Task.WaitAsync(TestTimeout);
        Assert.False(executeTask.IsCompleted);
        Assert.Equal(0, guard.DisposeCount);

        allowRenewalFinish.TrySetResult();
        var result = await executeTask.WaitAsync(TestTimeout);

        Assert.True(result);
        Assert.Equal(1, guard.DisposeCount);

        var ordered = events.ToArray();
        Assert.True(Array.IndexOf(ordered, "callback_threw") < Array.IndexOf(ordered, "renewal_finished"));
        Assert.True(Array.IndexOf(ordered, "renewal_finished") < Array.IndexOf(ordered, "guard_disposed"));
    }

    private static Analysis CreateRunningAnalysis(int id, Guid executionToken)
    {
        return new Analysis
        {
            Id = id,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            ExecutionToken = executionToken,
            AttemptCount = 1,
            MaxAttempts = 3,
            Commit = new Commit
            {
                Id = 1,
                CommitHash = "abcdef123456",
                Project = new Project
                {
                    Id = 1,
                    Name = "TestProj",
                    RepoUrl = "https://github.com/test/repo",
                    OwnerId = 1
                }
            }
        };
    }

    private static Mock<IAnalysisRepository> CreateBaseAnalysisRepoMock(Analysis analysis)
    {
        var analysisRepo = new Mock<IAnalysisRepository>();
        analysisRepo.Setup(x => x.GetByIdAsync(analysis.Id, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        return analysisRepo;
    }

    private static AnalysisService CreateService(
        IAnalysisRepository analysisRepository,
        IAnalysisLeaseGuard guard,
        IEnumerable<IAnalyzer> analyzers)
    {
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
            .ReturnsAsync("C:\\fake-repo");
        sourceService
            .Setup(x => x.CleanupAsync(
                It.IsAny<int>(),
                It.IsAny<Guid>(),
                It.IsAny<IAnalysisLeaseGuard>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var credService = new Mock<IGitHubCredentialService>();
        credService.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        var guardProvider = new Mock<IAnalysisLeaseGuardProvider>();
        guardProvider
            .Setup(x => x.AcquireGuardAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(guard);

        return new AnalysisService(
            analysisRepository,
            commitRepo.Object,
            sourceService.Object,
            analyzers,
            credService.Object,
            guardProvider.Object);
    }

    private sealed class LifecycleTrackingLeaseGuard(
        int analysisId,
        Guid executionToken,
        Func<int[], CancellationToken, Task<bool>> onValidateOwnership,
        Action onDispose) : IAnalysisLeaseGuard
    {
        private int _disposeCount;

        public int AnalysisId { get; } = analysisId;
        public Guid ExecutionToken { get; } = executionToken;
        public bool IsAcquired => true;
        public bool RequiresSessionAdvisoryLock => false;
        public DbConnection? Connection => null;
        public int? ConnectionProcessId => null;
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public Task<bool> ValidateOwnershipAsync(CancellationToken cancellationToken = default)
            => onValidateOwnership([1, 2, 3], cancellationToken);

        public Task<bool> ValidateOwnershipAsync(int[] allowedStatuses, CancellationToken cancellationToken = default)
            => onValidateOwnership(allowedStatuses, cancellationToken);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            onDispose();
            return ValueTask.CompletedTask;
        }
    }
}
