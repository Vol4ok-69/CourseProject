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
                Project = new Project { RepoUrl = "https://github.com/repo" }
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
        analysisRepo.Setup(x => x.CompleteAsync(101, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<CancellationToken>()))
            .Callback(() => completedTcs.TrySetResult(true))
            .ReturnsAsync(true);

        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 101, It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\Temp");

        var services = new ServiceCollection();
        services.AddSingleton(analysisRepo.Object);
        services.AddSingleton(commitRepo.Object);
        services.AddSingleton(sourceService.Object);
        services.AddSingleton(new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, []));

        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var bgService = new AnalysisBackgroundService(scopeFactory, logger.Object);

        using var cts = new CancellationTokenSource();
        await bgService.StartAsync(cts.Token);

        // Синхронизируемся с фактическим завершением задачи вместо фиксированной паузы
        await completedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await bgService.StopAsync(CancellationToken.None);

        analysisRepo.Verify(x => x.CompleteAsync(101, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<CancellationToken>()), Times.Once);
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

        var failingTask = new Analysis
        {
            Id = 201,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            ExecutionToken = token1,
            Commit = new Commit { Id = 1, CommitHash = "h1", Project = new Project { RepoUrl = "url" } }
        };

        var successTask = new Analysis
        {
            Id = 202,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            ExecutionToken = token2,
            Commit = new Commit { Id = 1, CommitHash = "h2", Project = new Project { RepoUrl = "url" } }
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
        analysisRepo.Setup(x => x.FailAsync(201, token1, It.IsAny<CancellationToken>()))
            .Callback(() => task1FailedTcs.TrySetResult(true))
            .ReturnsAsync(true);
        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), "h1", 201, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("GitHub download failed"));

        // Вторая задача выполняется успешно
        analysisRepo.Setup(x => x.GetByIdAsync(202, It.IsAny<CancellationToken>())).ReturnsAsync(successTask);
        analysisRepo.Setup(x => x.CompleteAsync(202, token2, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<CancellationToken>()))
            .Callback(() => task2CompletedTcs.TrySetResult(true))
            .ReturnsAsync(true);
        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), "h2", 202, It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\Temp");

        var services = new ServiceCollection();
        services.AddSingleton(analysisRepo.Object);
        services.AddSingleton(commitRepo.Object);
        services.AddSingleton(sourceService.Object);
        services.AddSingleton(new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, []));

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
        analysisRepo.Verify(x => x.FailAsync(201, token1, It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.CompleteAsync(202, token2, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
