using System.Text.Json;
using DatabaseAnalyzer.Contracts;
using DatabaseAnalyzer.Metadata;
using DatabaseAnalyzer.Process;
using DatabaseAnalyzer.Roslyn;
using DatabaseAnalyzer.Runner;

namespace DatabaseAnalyzer.Runner.Tests;

public sealed class ContainerRunnerSecurityTests
{
    private static RoslynModelExtractionResult CreateSampleExtraction()
    {
        return new RoslynModelExtractionResult
        {
            MetadataCapabilities = ["CoreRelational"],
            Contexts =
            [
                new DiscoveredDbContext
                {
                    Name = "SampleDbContext",
                    Entities = []
                }
            ]
        };
    }

    [Fact]
    public async Task WithoutLeaseGuard_NoFileOperations_AndContainerNotStarted()
    {
        // TD-003: Без lease guard файловые операции не выполняются и контейнер не запускается.
        var mockAdapter = new MockTrackingAdapter();
        var runner = new DatabaseContainerRunner(mockAdapter, leaseGuardProvider: null);

        var extraction = CreateSampleExtraction();
        var analysisId = 71;
        var token = Guid.NewGuid();

        var expectedExchangeDir = Path.Combine(
            Path.GetTempPath(),
            "netscope-database-analyzer",
            $"{analysisId}_{token:N}");

        var result = await runner.RunAnalysisAsync(extraction, analysisId, token);

        Assert.Equal("Failed", result.Status);
        Assert.Contains("Active workspace lease guard is missing", result.ErrorMessage);
        Assert.False(mockAdapter.WasRunCalled, "Container must not be started without lease guard!");
        Assert.False(Directory.Exists(expectedExchangeDir), "Directory must not be created without lease guard!");
    }

    [Fact]
    public async Task FailedLockAcquisition_DirectoryNeitherCreatedNorDeleted()
    {
        // TD-003: При невозможности захватить блокировку каталог не создаётся и не удаляется.
        var mockAdapter = new MockTrackingAdapter();
        var mockProvider = new MockFailedGuardProvider();
        var runner = new DatabaseContainerRunner(mockAdapter, "netscope-database-analyzer:latest", mockProvider);

        var extraction = CreateSampleExtraction();
        var analysisId = 72;
        var token = Guid.NewGuid();

        var failedGuard = await mockProvider.AcquireGuardAsync(analysisId, token);

        var expectedExchangeDir = Path.Combine(
            Path.GetTempPath(),
            "netscope-database-analyzer",
            $"{analysisId}_{token:N}");

        var result = await runner.RunAnalysisAsync(extraction, analysisId, token, failedGuard);

        Assert.Equal("Failed", result.Status);
        Assert.Contains("Active workspace lease guard is missing or does not match", result.ErrorMessage);
        Assert.False(mockAdapter.WasRunCalled);
        Assert.False(Directory.Exists(expectedExchangeDir));
    }

    [Fact]
    public async Task InitialOwnershipValidationFailed_DirectoryNotCreated()
    {
        // TD-003: Если начальная проверка токена вернула false, каталог не создаётся.
        var mockAdapter = new MockTrackingAdapter();
        var provider = new InMemoryAnalysisLeaseGuardProvider((_, _) => Task.FromResult(false));
        var runner = new DatabaseContainerRunner(mockAdapter, "netscope-database-analyzer:latest", provider);

        var extraction = CreateSampleExtraction();
        var analysisId = 73;
        var token = Guid.NewGuid();

        await using var guard = await provider.AcquireGuardAsync(analysisId, token);

        var expectedExchangeDir = Path.Combine(
            Path.GetTempPath(),
            "netscope-database-analyzer",
            $"{analysisId}_{token:N}");

        var result = await runner.RunAnalysisAsync(extraction, analysisId, token, guard);

        Assert.Equal("Cancelled", result.Status);
        Assert.Contains("Workspace ownership has been lost", result.ErrorMessage);
        Assert.False(mockAdapter.WasRunCalled);
        Assert.False(Directory.Exists(expectedExchangeDir));
    }

    [Fact]
    public async Task SuccessfulRun_PreservesOwnershipUntilFileOperationsComplete_AndCleansUp()
    {
        // TD-003: При успешном завершении владение сохраняется до завершения файловых операций,
        // после чего каталог безопасно очищается.
        var adapter = new LocalProcessAdapter();
        var provider = new InMemoryAnalysisLeaseGuardProvider((_, _) => Task.FromResult(true));
        var runner = new DatabaseContainerRunner(adapter, "netscope-database-analyzer:latest", provider);

        var extraction = CreateSampleExtraction();
        var analysisId = 74;
        var token = Guid.NewGuid();

        await using var guard = await provider.AcquireGuardAsync(analysisId, token);

        var expectedExchangeDir = Path.Combine(
            Path.GetTempPath(),
            "netscope-database-analyzer",
            $"{analysisId}_{token:N}");

        var result = await runner.RunAnalysisAsync(extraction, analysisId, token, guard);

        Assert.Equal("Success", result.Status);
        Assert.Empty(result.Findings);
        Assert.False(Directory.Exists(expectedExchangeDir));
    }

    [Fact]
    public async Task ConnectionLoss_DuringExecution_ResultsRejectedAndDirectoryPreserved()
    {
        // TD-003: При потере соединения во время выполнения контейнера результаты не принимаются,
        // а каталог сохраняется.
        var callCount = 0;
        var provider = new InMemoryAnalysisLeaseGuardProvider((_, _) =>
        {
            callCount++;
            if (callCount <= 1)
            {
                return Task.FromResult(true); // Initial pre-check passes
            }
            // Потеря соединения после выполнения контейнера
            return Task.FromResult(false);
        });

        var adapter = new LocalProcessAdapter();
        var runner = new DatabaseContainerRunner(adapter, "netscope-database-analyzer:latest", provider);

        var extraction = CreateSampleExtraction();
        var analysisId = 75;
        var token = Guid.NewGuid();

        await using var guard = await provider.AcquireGuardAsync(analysisId, token);

        var expectedExchangeDir = Path.Combine(
            Path.GetTempPath(),
            "netscope-database-analyzer",
            $"{analysisId}_{token:N}");

        try
        {
            var result = await runner.RunAnalysisAsync(extraction, analysisId, token, guard);

            Assert.Equal("Cancelled", result.Status);
            Assert.Contains("Workspace ownership has been lost", result.ErrorMessage);
            Assert.True(Directory.Exists(expectedExchangeDir),
                "Directory was deleted despite loss of lease ownership!");
        }
        finally
        {
            if (Directory.Exists(expectedExchangeDir))
            {
                Directory.Delete(expectedExchangeDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TokenMismatch_ThrowsSecurityException_AndDoesNotDeleteExchangeDirectory()
    {
        // TD-003: При несовпадении токена в выходном файле каталог НЕ удаляется.
        var mockAdapter = new MockMismatchTokenAdapter();
        var provider = new InMemoryAnalysisLeaseGuardProvider((_, _) => Task.FromResult(true));
        var runner = new DatabaseContainerRunner(mockAdapter, "netscope-database-analyzer:latest", provider);

        var extraction = CreateSampleExtraction();
        var analysisId = 76;
        var token = Guid.NewGuid();

        await using var guard = await provider.AcquireGuardAsync(analysisId, token);

        var expectedExchangeDir = Path.Combine(
            Path.GetTempPath(),
            "netscope-database-analyzer",
            $"{analysisId}_{token:N}");

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => runner.RunAnalysisAsync(extraction, analysisId, token, guard));

            Assert.Contains("Output token mismatch", ex.Message);
            Assert.True(Directory.Exists(expectedExchangeDir),
                "Directory was deleted by runner after token mismatch!");
        }
        finally
        {
            if (Directory.Exists(expectedExchangeDir))
            {
                Directory.Delete(expectedExchangeDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task OversizedOutputFile_ThrowsSecurityException()
    {
        var mockAdapter = new MockOversizedOutputAdapter();
        var provider = new InMemoryAnalysisLeaseGuardProvider((_, _) => Task.FromResult(true));
        var runner = new DatabaseContainerRunner(mockAdapter, "netscope-database-analyzer:latest", provider);

        var extraction = CreateSampleExtraction();
        var analysisId = 77;
        var token = Guid.NewGuid();

        await using var guard = await provider.AcquireGuardAsync(analysisId, token);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAnalysisAsync(extraction, analysisId, token, guard));

        Assert.Contains("exceeds maximum allowed limit", ex.Message);
    }

    [Fact]
    public async Task RunnerTimeout_StopsContainer_AndReturnsTimedOutStatus()
    {
        var mockAdapter = new MockTimeoutAdapter();
        var provider = new InMemoryAnalysisLeaseGuardProvider((_, _) => Task.FromResult(true));
        var runner = new DatabaseContainerRunner(mockAdapter, "netscope-database-analyzer:latest", provider);

        var extraction = CreateSampleExtraction();
        var analysisId = 78;
        var token = Guid.NewGuid();

        await using var guard = await provider.AcquireGuardAsync(analysisId, token);

        var result = await runner.RunAnalysisAsync(extraction, analysisId, token, guard);

        Assert.Equal("TimedOut", result.Status);
    }

    [Fact]
    public async Task RunnerCancelled_StopsContainer_AndReturnsCancelledStatus()
    {
        var mockAdapter = new MockCancelledAdapter();
        var provider = new InMemoryAnalysisLeaseGuardProvider((_, _) => Task.FromResult(true));
        var runner = new DatabaseContainerRunner(mockAdapter, "netscope-database-analyzer:latest", provider);

        var extraction = CreateSampleExtraction();
        var analysisId = 79;
        var token = Guid.NewGuid();

        await using var guard = await provider.AcquireGuardAsync(analysisId, token);

        var result = await runner.RunAnalysisAsync(extraction, analysisId, token, guard);

        Assert.Equal("Cancelled", result.Status);
    }

    [Fact]
    public async Task ConcurrentWorkers_SameAnalysisId_BlockedFromSimultaneousExecution()
    {
        // TD-003: Одновременная работа двух worker с одним AnalysisId невозможна.
        var provider = new InMemoryAnalysisLeaseGuardProvider();
        var adapter = new LocalProcessAdapter();
        var runner1 = new DatabaseContainerRunner(adapter, "netscope-database-analyzer:latest", provider);
        var runner2 = new DatabaseContainerRunner(adapter, "netscope-database-analyzer:latest", provider);

        var extraction = CreateSampleExtraction();
        var analysisId = 888;
        var token1 = Guid.NewGuid();
        var token2 = Guid.NewGuid();

        // Worker 1 захватывает guard
        await using var guard1 = await provider.AcquireGuardAsync(analysisId, token1);
        Assert.True(guard1.IsAcquired);

        // Worker 2 пытается получить блокировку для того же AnalysisId
        await using var guard2Attempt = await provider.AcquireGuardAsync(analysisId, token2);
        Assert.False(guard2Attempt.IsAcquired, "Worker 2 не должен мочь захватить guard, пока guard1 удерживается");

        // При передаче не захваченного guard runner2 отклоняет операцию
        var result2 = await runner2.RunAnalysisAsync(extraction, analysisId, token2, guard2Attempt);
        Assert.Equal("Failed", result2.Status);
        Assert.Contains("Active workspace lease guard is missing or does not match", result2.ErrorMessage);

        // После освобождения guard1 Worker 2 может получить блокировку
        await guard1.DisposeAsync();

        await using var guard2Success = await provider.AcquireGuardAsync(analysisId, token2);
        Assert.True(guard2Success.IsAcquired, "После освобождения guard1 Worker 2 успешно получает guard");

        var result2AfterRelease = await runner2.RunAnalysisAsync(extraction, analysisId, token2, guard2Success);
        Assert.Equal("Success", result2AfterRelease.Status);
    }

    #region Mock Adapters for Security Tests

    private sealed class MockTrackingAdapter : IContainerProcessAdapter
    {
        public bool WasRunCalled { get; private set; }

        public Task<ProcessExecutionResult> RunAsync(ContainerRunOptions options, CancellationToken cancellationToken = default)
        {
            WasRunCalled = true;
            return Task.FromResult(new ProcessExecutionResult { ExitCode = 0 });
        }
    }

    private sealed class MockFailedGuardProvider : IAnalysisLeaseGuardProvider
    {
        public Task<IAnalysisLeaseGuard> AcquireGuardAsync(int analysisId, Guid executionToken, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IAnalysisLeaseGuard>(new FailedLeaseGuard(analysisId, executionToken));
        }

        private sealed class FailedLeaseGuard(int analysisId, Guid executionToken) : IAnalysisLeaseGuard
        {
            public int AnalysisId => analysisId;
            public Guid ExecutionToken => executionToken;
            public bool IsAcquired => false;
            public Task<bool> ValidateOwnershipAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class MockMismatchTokenAdapter : IContainerProcessAdapter
    {
        public async Task<ProcessExecutionResult> RunAsync(ContainerRunOptions options, CancellationToken cancellationToken = default)
        {
            var output = new DatabaseAnalyzerOutputDocument
            {
                SchemaVersion = "1.0",
                AnalysisId = 88,
                ExecutionToken = Guid.NewGuid(), // Different token!
                Status = "Success",
                CompletedAtUtc = DateTimeOffset.UtcNow
            };

            var json = JsonSerializer.Serialize(output, DatabaseAnalyzerJsonContext.Default.DatabaseAnalyzerOutputDocument);
            await File.WriteAllTextAsync(options.OutputFilePath, json, cancellationToken);

            return new ProcessExecutionResult { ExitCode = 0 };
        }
    }

    private sealed class MockOversizedOutputAdapter : IContainerProcessAdapter
    {
        public async Task<ProcessExecutionResult> RunAsync(ContainerRunOptions options, CancellationToken cancellationToken = default)
        {
            var buffer = new byte[11 * 1024 * 1024];
            await File.WriteAllBytesAsync(options.OutputFilePath, buffer, cancellationToken);

            return new ProcessExecutionResult { ExitCode = 0 };
        }
    }

    private sealed class MockTimeoutAdapter : IContainerProcessAdapter
    {
        public Task<ProcessExecutionResult> RunAsync(ContainerRunOptions options, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ProcessExecutionResult
            {
                ExitCode = -1,
                IsTimedOut = true,
                StandardError = "Process timed out."
            });
        }
    }

    private sealed class MockCancelledAdapter : IContainerProcessAdapter
    {
        public Task<ProcessExecutionResult> RunAsync(ContainerRunOptions options, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ProcessExecutionResult
            {
                ExitCode = -1,
                IsCancelled = true,
                StandardError = "Process was cancelled."
            });
        }
    }

    #endregion
}
