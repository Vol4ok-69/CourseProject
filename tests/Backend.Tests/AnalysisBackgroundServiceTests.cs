using DatabaseAnalyzer.Runner;
using IAnalysisLeaseGuard = Application.Interfaces.IAnalysisLeaseGuard;
using Api.BackgroundServices;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Backend.Tests;

public sealed class AnalysisBackgroundServiceTests
{
    [Fact]
    public async Task BackgroundService_ProcessesPendingTasksSuccessfully()
    {
        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var logger = new Mock<ILogger<AnalysisBackgroundService>>();

        var token = Guid.NewGuid();
        var task = new Analysis
        {
            Id = 101,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 1,
                CommitHash = "hash101",
                Project = new Project { RepoUrl = "https://github.com/repo", OwnerId = 1 }
            }
        };

        var completedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var calls = 0;
        analysisRepo.Setup(x => x.TryAcquireNextPendingOrStaleAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                calls++;
                return calls == 1 ? task : null;
            });

        analysisRepo.Setup(x => x.GetByIdAsync(101, It.IsAny<CancellationToken>())).ReturnsAsync(task);
        analysisRepo.Setup(x => x.CompleteAsync(101, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Callback(() => completedTcs.TrySetResult(true))
            .ReturnsAsync(true);

        credentialService.Setup(x => x.GetUserTokenAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 101, token, "ghp_test_token", It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\Temp");

        var services = new ServiceCollection();
        services.AddSingleton(analysisRepo.Object);
        services.AddSingleton(commitRepo.Object);
        services.AddSingleton(sourceService.Object);
        services.AddSingleton(credentialService.Object);
        services.AddSingleton(new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider()));

        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var bgService = new AnalysisBackgroundService(scopeFactory, logger.Object);

        using var cts = new CancellationTokenSource();
        await bgService.StartAsync(cts.Token);

        // Синхронизируемся с фактическим завершением задачи вместо фиксированной паузы
        await completedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await bgService.StopAsync(CancellationToken.None);

        analysisRepo.Verify(x => x.CompleteAsync(101, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BackgroundService_SingleTaskError_DoesNotStopBackgroundService()
    {
        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var logger = new Mock<ILogger<AnalysisBackgroundService>>();

        var token1 = Guid.NewGuid();
        var token2 = Guid.NewGuid();
        var credentialService = new Mock<IGitHubCredentialService>();

        var failingTask = new Analysis
        {
            Id = 201,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            ExecutionToken = token1,
            Commit = new Commit { Id = 1, CommitHash = "h1", Project = new Project { RepoUrl = "url", OwnerId = 1 } }
        };

        var successTask = new Analysis
        {
            Id = 202,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            ExecutionToken = token2,
            Commit = new Commit { Id = 1, CommitHash = "h2", Project = new Project { RepoUrl = "url", OwnerId = 1 } }
        };

        var task1FailedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task2CompletedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var callCount = 0;
        analysisRepo.Setup(x => x.TryAcquireNextPendingOrStaleAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1) return failingTask;
                if (callCount == 2) return successTask;
                return null;
            });

        // Первая задача падает с ошибкой
        analysisRepo.Setup(x => x.GetByIdAsync(201, It.IsAny<CancellationToken>())).ReturnsAsync(failingTask);
        analysisRepo.Setup(x => x.FailAsync(201, token1, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Callback(() => task1FailedTcs.TrySetResult(true))
            .ReturnsAsync(true);
        credentialService.Setup(x => x.GetUserTokenAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), "h1", 201, token1, "ghp_test_token", It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("GitHub download failed"));

        // Вторая задача выполняется успешно
        analysisRepo.Setup(x => x.GetByIdAsync(202, It.IsAny<CancellationToken>())).ReturnsAsync(successTask);
        analysisRepo.Setup(x => x.CompleteAsync(202, token2, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Callback(() => task2CompletedTcs.TrySetResult(true))
            .ReturnsAsync(true);
        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), "h2", 202, token2, "ghp_test_token", It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\Temp");

        var services = new ServiceCollection();
        services.AddSingleton(analysisRepo.Object);
        services.AddSingleton(commitRepo.Object);
        services.AddSingleton(sourceService.Object);
        services.AddSingleton(credentialService.Object);
        services.AddSingleton(new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider()));

        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var bgService = new AnalysisBackgroundService(scopeFactory, logger.Object);

        using var cts = new CancellationTokenSource();
        await bgService.StartAsync(cts.Token);

        // Дожидаемся обработки обеих задач
        await task1FailedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await task2CompletedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await bgService.StopAsync(CancellationToken.None);

        // Убеждаемся, что ошибка на первой задаче не остановила обработку второй задачи
        analysisRepo.Verify(x => x.FailAsync(201, token1, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.CompleteAsync(202, token2, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void RepositorySourceService_DependencyInjection_ResolvesWithoutAmbiguity()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient<IRepositorySourceService, Infrastructure.GitHub.RepositorySourceService>();

        using var provider = services.BuildServiceProvider();
        var service = provider.GetService<IRepositorySourceService>();

        Assert.NotNull(service);
        Assert.IsType<Infrastructure.GitHub.RepositorySourceService>(service);
    }

    [Fact]
    public async Task AnalysisBackgroundService_WhenExecutionReturnsFalse_DoesNotMutateAnalysisState()
    {
        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var logger = new Mock<ILogger<AnalysisBackgroundService>>();

        var token = Guid.NewGuid();
        var task = new Analysis
        {
            Id = 301,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 1,
                CommitHash = "hash301",
                Project = new Project { RepoUrl = "https://github.com/repo", OwnerId = 1 }
            }
        };

        var completedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var acquireCount = 0;
        analysisRepo.Setup(x => x.TryAcquireNextPendingOrStaleAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                acquireCount++;
                if (acquireCount == 1) return task;
                if (acquireCount == 2) completedTcs.TrySetResult(true);
                return null;
            });

        analysisRepo.Setup(x => x.GetByIdAsync(301, It.IsAny<CancellationToken>())).ReturnsAsync(task);
        // CompleteAsync throws to simulate ambiguous commit failure
        analysisRepo.Setup(x => x.CompleteAsync(301, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.IO.IOException("Simulated ACK loss on commit"));

        // Recovery DB check also throws to produce ambiguous commit
        analysisRepo.Setup(x => x.GetAnalysisWithProcessIdAsync(301, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.IO.IOException("DB unreachable on recovery"));

        analysisRepo.Setup(x => x.RenewLeaseAsync(301, token, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        credentialService.Setup(x => x.GetUserTokenAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 301, token, "ghp_test_token", It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\Temp");

        var services = new ServiceCollection();
        services.AddSingleton(analysisRepo.Object);
        services.AddSingleton(commitRepo.Object);
        services.AddSingleton(sourceService.Object);
        services.AddSingleton(credentialService.Object);
        services.AddSingleton(new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider()));

        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var bgService = new AnalysisBackgroundService(scopeFactory, logger.Object);

        using var cts = new CancellationTokenSource();
        await bgService.StartAsync(cts.Token);

        // Ждем пока сервис выполнит первую задачу и перейдет к следующему циклу
        await completedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await bgService.StopAsync(CancellationToken.None);

        // 1. При false сервис НЕ вызывает FailAsync
        analysisRepo.Verify(x => x.FailAsync(301, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);

        // 2. Сервис НЕ вызывает ScheduleRetryAsync при неоднозначном исходе
        analysisRepo.Verify(x => x.ScheduleRetryAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);

        // 3. Воркер не удаляет workspace самостоятельно
        sourceService.Verify(x => x.CleanupAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);

        // 4. Сервис продолжил работу и запросил следующую задачу через репозиторий
        Assert.True(acquireCount >= 2);
    }
}
