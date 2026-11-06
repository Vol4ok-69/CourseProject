using System.Reflection;
using Application.Exceptions;
﻿using Application.Analyzers;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Repository;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Backend.Tests;

public sealed class AnalysisServiceQueueTests
{
    [Fact]
    public async Task EnqueueAsync_CreatesPendingAnalysisWithCreatedAt_WithoutRunningAnalyzersOrDownloadingSources()
    {
        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var analyzer = new Mock<IAnalyzer>();

        var commit = new Commit
        {
            Id = 1,
            ProjectId = 1,
            CommitHash = "abcdef",
            Project = new Project { Id = 1, Name = "TestProj", RepoUrl = "https://github.com/repo", OwnerId = 1 }
        };

        commitRepo.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(commit);

        analysisRepo.Setup(x => x.AddAsync(It.IsAny<Analysis>(), It.IsAny<CancellationToken>()))
            .Callback<Analysis, CancellationToken>((a, _) => a.Id = 100)
            .Returns(Task.CompletedTask);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], Mock.Of<IGitHubCredentialService>());

        var result = await service.EnqueueAsync(1);

        Assert.Equal(100, result.Id);
        Assert.Equal(1, result.CommitId);
        Assert.Equal(AnalysisStatus.Pending, result.Status);
        Assert.Null(result.StartedAt);
        Assert.Null(result.CompletedAt);
        Assert.True(result.CreatedAt > DateTime.UtcNow.AddMinutes(-1));

        analysisRepo.Verify(x => x.AddAsync(It.IsAny<Analysis>(), It.IsAny<CancellationToken>()), Times.Once);

        // Гарантируем, что исходники не скачиваются и анализаторы не вызываются
        sourceService.Verify(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        analyzer.Verify(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnqueueAsync_WithNonExistentCommit_ThrowsInvalidOperationException()
    {
        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var analyzer = new Mock<IAnalyzer>();

        commitRepo.Setup(x => x.GetByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((Commit?)null);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], Mock.Of<IGitHubCredentialService>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnqueueAsync(999));
        Assert.Equal("Коммит не найден.", ex.Message);

        analysisRepo.Verify(x => x.AddAsync(It.IsAny<Analysis>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_SuccessfulRun_SavesResultsAndMarksCompletedAndCleansUp()
    {
        var token = Guid.NewGuid();
        var analysis = new Analysis
        {
            Id = 50,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 1,
                CommitHash = "hash123",
                Project = new Project { RepoUrl = "https://github.com/test/repo" }
            }
        };

        analysis.Commit.Project.OwnerId = 1;

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        credentialService.Setup(x => x.GetUserTokenAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        analysisRepo.Setup(x => x.GetByIdAsync(50, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo.Setup(x => x.CompleteAsync(50, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        sourceService.Setup(x => x.DownloadAndExtractAsync("https://github.com/test/repo", "hash123", 50, "ghp_test_token", It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\TempRepo");

        analyzer.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new AnalyzerFinding
                {
                    FilePath = "Main.cs",
                    LineNumber = 12,
                    Rule = "CSHARP001",
                    Message = "Violation",
                    Severity = Severity.Warning
                }
            ]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object);

        var success = await service.ExecuteAnalysisAsync(50, token);

        Assert.True(success);

        analysisRepo.Verify(x => x.CompleteAsync(50, token, It.Is<IReadOnlyList<AnalysisResult>>(r => r.Count == 1), It.IsAny<CancellationToken>()), Times.Once);
        sourceService.Verify(x => x.CleanupAsync(50, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_AnalyzerThrows_MarksFailedAndCleansUp()
    {
        var token = Guid.NewGuid();
        var analysis = new Analysis
        {
            Id = 51,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 1,
                CommitHash = "hash123",
                Project = new Project { RepoUrl = "https://github.com/test/repo" }
            }
        };

        analysis.Commit.Project.OwnerId = 1;

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        credentialService.Setup(x => x.GetUserTokenAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        analysisRepo.Setup(x => x.GetByIdAsync(51, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo.Setup(x => x.FailAsync(51, token, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 51, "ghp_test_token", It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\TempRepo");

        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Roslyn crash"));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object);

        var success = await service.ExecuteAnalysisAsync(51, token);

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(51, token, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        sourceService.Verify(x => x.CleanupAsync(51, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_CleanupThrows_DoesNotPreventCompletion()
    {
        var token = Guid.NewGuid();
        var analysis = new Analysis
        {
            Id = 52,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 1,
                CommitHash = "hash123",
                Project = new Project { RepoUrl = "https://github.com/test/repo" }
            }
        };

        analysis.Commit.Project.OwnerId = 1;

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        credentialService.Setup(x => x.GetUserTokenAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        analysisRepo.Setup(x => x.GetByIdAsync(52, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo.Setup(x => x.CompleteAsync(52, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 52, "ghp_test_token", It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\TempRepo");
        sourceService.Setup(x => x.CleanupAsync(52, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Directory locked by another process"));

        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object);

        var success = await service.ExecuteAnalysisAsync(52, token);

        Assert.True(success);
        analysisRepo.Verify(x => x.CompleteAsync(52, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnqueueAndExecution_CancellationOfOriginalHttpRequest_DoesNotCancelExecution()
    {
        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var analyzer = new Mock<IAnalyzer>();

        var commit = new Commit
        {
            Id = 1,
            ProjectId = 1,
            CommitHash = "hash123",
            Project = new Project { Id = 1, Name = "P", RepoUrl = "url", OwnerId = 1 }
        };

        commitRepo.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(commit);
        analysisRepo.Setup(x => x.AddAsync(It.IsAny<Analysis>(), It.IsAny<CancellationToken>()))
            .Callback<Analysis, CancellationToken>((a, _) => a.Id = 77)
            .Returns(Task.CompletedTask);

        var credentialService = new Mock<IGitHubCredentialService>();
        credentialService.Setup(x => x.GetUserTokenAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object);

        // Симулируем отмену HTTP-запроса сразу после сохранения задачи
        using var httpRequestCts = new CancellationTokenSource();
        var enqueuedAnalysis = await service.EnqueueAsync(1, httpRequestCts.Token);
        httpRequestCts.Cancel();

        // Фоновый воркер запускает задачу со своим собственным токеном, не зависящим от HTTP
        var executionToken = Guid.NewGuid();
        enqueuedAnalysis.Status = AnalysisStatus.Running;
        enqueuedAnalysis.ExecutionToken = executionToken;
        enqueuedAnalysis.Commit = commit;

        analysisRepo.Setup(x => x.GetByIdAsync(77, It.IsAny<CancellationToken>())).ReturnsAsync(enqueuedAnalysis);
        analysisRepo.Setup(x => x.CompleteAsync(77, executionToken, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 77, "ghp_test_token", It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\Temp");
        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        using var backgroundServiceCts = new CancellationTokenSource();
        var executionSuccess = await service.ExecuteAnalysisAsync(77, executionToken, backgroundServiceCts.Token);

        Assert.True(executionSuccess);
        analysisRepo.Verify(x => x.CompleteAsync(77, executionToken, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenLoadedFromRepositoryWithoutPreloadedNavigations_SuccessfullyAccessesProjectRepoUrl()
    {
        // Создаем реальную базу данных SQLite в памяти для симуляции раздельных DbContext
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        var token = Guid.NewGuid();

        // Контекст 1: первичное наполнение базы данных
        using (var seedContext = new AppDbContext(options))
        {
            seedContext.Database.EnsureCreated();

            var role = new Role { Id = 1, Name = "User" };
            var user = new User { Id = 1, Username = "dev", PasswordHash = "hash", RoleId = 1 };
            var project = new Project { Id = 1, Name = "TestProject", RepoUrl = "https://github.com/org/repo.git", OwnerId = 1 };
            var commit = new Commit { Id = 1, ProjectId = 1, CommitHash = "commit-abc-123", CommitDate = DateTime.UtcNow };
            var analysis = new Analysis
            {
                Id = 1,
                CommitId = 1,
                Status = AnalysisStatus.Running,
                CreatedAt = DateTime.UtcNow.AddMinutes(-2),
                StartedAt = DateTime.UtcNow.AddMinutes(-1),
                ExecutionToken = token,
                LeaseExpiration = DateTime.UtcNow.AddMinutes(5)
            };

            seedContext.Roles.Add(role);
            seedContext.Users.Add(user);
            seedContext.Projects.Add(project);
            seedContext.Commits.Add(commit);
            seedContext.Analyses.Add(analysis);
            seedContext.SaveChanges();
        }

        // Контекст 2: полностью изолированный контекст без предзагруженных навигационных свойств в памяти
        using (var runContext = new AppDbContext(options))
        {
            var analysisRepo = new AnalysisRepository(runContext);
            var commitRepo = new CommitRepository(runContext);
            var sourceService = new Mock<IRepositorySourceService>();
            var credentialService = new Mock<IGitHubCredentialService>();
            var analyzer = new Mock<IAnalyzer>();

            credentialService.Setup(x => x.GetUserTokenAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync("ghp_test_token");

            analyzer.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
            analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            string? requestedRepoUrl = null;
            sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 1, "ghp_test_token", It.IsAny<CancellationToken>()))
                .Callback<string, string, int, string, CancellationToken>((url, _, _, _, _) => requestedRepoUrl = url)
                .ReturnsAsync("C:\\TempExtracted");

            var service = new AnalysisService(analysisRepo, commitRepo, sourceService.Object, [analyzer.Object], credentialService.Object);

            var success = await service.ExecuteAnalysisAsync(1, token);

            Assert.True(success);
            Assert.Equal("https://github.com/org/repo.git", requestedRepoUrl);

            // Проверяем статус в базе через третий контекст
            using var verifyContext = new AppDbContext(options);
            var finished = await verifyContext.Analyses.FindAsync(1);
            Assert.NotNull(finished);
            Assert.Equal(AnalysisStatus.Completed, finished.Status);
            Assert.NotNull(finished.CompletedAt);
        }
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenHostShutdownCanceled_DoesNotMarkTaskFailed_AndCleansUpTempFiles()
    {
        var token = Guid.NewGuid();
        var analysis = new Analysis
        {
            Id = 88,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 1,
                CommitHash = "hash88",
                Project = new Project { RepoUrl = "https://github.com/repo", OwnerId = 1 }
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        credentialService.Setup(x => x.GetUserTokenAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        analysisRepo.Setup(x => x.GetByIdAsync(88, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);

        using var shutdownCts = new CancellationTokenSource();

        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 88, "ghp_test_token", It.IsAny<CancellationToken>()))
            .Returns<string, string, int, string, CancellationToken>(async (_, _, _, _, ct) =>
            {
                shutdownCts.Cancel(); // Имитируем остановку приложения во время скачивания
                ct.ThrowIfCancellationRequested();
                await Task.CompletedTask;
                return "C:\\Temp";
            });

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExecuteAnalysisAsync(88, token, shutdownCts.Token));

        // Гарантируем, что задача НЕ помечена как сбойная (Failed)
        analysisRepo.Verify(x => x.FailAsync(88, token, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // Гарантируем, что временные файлы всё равно очищены
        sourceService.Verify(x => x.CleanupAsync(88, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenShutdownCanceled_TaskCanBeReacquiredAfterLeaseExpiration()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        var token = Guid.NewGuid();

        using (var seedContext = new AppDbContext(options))
        {
            seedContext.Database.EnsureCreated();

            seedContext.Roles.Add(new Role { Id = 1, Name = "User" });
            seedContext.Users.Add(new User { Id = 1, Username = "dev", PasswordHash = "hash", RoleId = 1 });
            seedContext.Projects.Add(new Project { Id = 1, Name = "P", RepoUrl = "https://github.com/repo", OwnerId = 1 });
            seedContext.Commits.Add(new Commit { Id = 1, ProjectId = 1, CommitHash = "h1", CommitDate = DateTime.UtcNow });
            seedContext.Analyses.Add(new Analysis
            {
                Id = 99,
                CommitId = 1,
                Status = AnalysisStatus.Running,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
                StartedAt = DateTime.UtcNow.AddMinutes(-4),
                ExecutionToken = token,
                LeaseExpiration = DateTime.UtcNow.AddSeconds(-1) // Аренда уже истекла после остановки приложения
            });
            seedContext.SaveChanges();
        }

        using (var recoverContext = new AppDbContext(options))
        {
            var repo = new AnalysisRepository(recoverContext);
            var reacquired = await repo.TryAcquireNextPendingOrStaleAsync(TimeSpan.FromMinutes(2));

            Assert.NotNull(reacquired);
            Assert.Equal(99, reacquired.Id);
            Assert.Equal(AnalysisStatus.Running, reacquired.Status);
            Assert.NotEqual(token, reacquired.ExecutionToken); // Назначен новый токен новым обработчиком
        }
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenCommitNotFound_MarksTaskFailed_AndCallsCleanup()
    {
        var token = Guid.NewGuid();
        var analysis = new Analysis
        {
            Id = 91,
            CommitId = 999,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = null!
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var analyzer = new Mock<IAnalyzer>();

        analysisRepo.Setup(x => x.GetByIdAsync(91, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        commitRepo.Setup(x => x.GetByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((Commit?)null);
        analysisRepo.Setup(x => x.FailAsync(91, token, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], Mock.Of<IGitHubCredentialService>());

        var success = await service.ExecuteAnalysisAsync(91, token);

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(91, token, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        sourceService.Verify(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        sourceService.Verify(x => x.CleanupAsync(91, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenProjectNotFound_MarksTaskFailed_AndCallsCleanup()
    {
        var token = Guid.NewGuid();
        var analysis = new Analysis
        {
            Id = 92,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 1,
                CommitHash = "hash92",
                Project = null!
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var analyzer = new Mock<IAnalyzer>();

        analysisRepo.Setup(x => x.GetByIdAsync(92, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        commitRepo.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new Commit { Id = 1, CommitHash = "hash92", Project = null! });
        analysisRepo.Setup(x => x.FailAsync(92, token, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], Mock.Of<IGitHubCredentialService>());

        var success = await service.ExecuteAnalysisAsync(92, token);

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(92, token, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        sourceService.Verify(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        sourceService.Verify(x => x.CleanupAsync(92, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenCommitRepoThrowsException_MarksTaskFailed()
    {
        var token = Guid.NewGuid();
        var analysis = new Analysis
        {
            Id = 93,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = null!
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var analyzer = new Mock<IAnalyzer>();

        analysisRepo.Setup(x => x.GetByIdAsync(93, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        commitRepo.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("DB unavailable"));
        analysisRepo.Setup(x => x.FailAsync(93, token, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], Mock.Of<IGitHubCredentialService>());

        var success = await service.ExecuteAnalysisAsync(93, token);

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(93, token, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        sourceService.Verify(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        sourceService.Verify(x => x.CleanupAsync(93, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenLeaseLostDuringAnalysis_DoesNotCallCleanupAndDoesNotCallFailAsync()
    {
        var token = Guid.NewGuid();
        var analysis = new Analysis
        {
            Id = 94,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 1,
                CommitHash = "hash94",
                Project = new Project { RepoUrl = "https://github.com/repo", OwnerId = 1 }
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        credentialService.Setup(x => x.GetUserTokenAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        analysisRepo.Setup(x => x.GetByIdAsync(94, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        // Симулируем, что продление аренды возвращает false (аренда потеряна)
        analysisRepo.Setup(x => x.RenewLeaseAsync(94, token, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 94, "ghp_test_token", It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\Temp");

        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns<AnalyzerContext, CancellationToken>(async (_, ct) =>
            {
                // Ожидаем отмены токена ct из-за потери аренды
                var tcs = new TaskCompletionSource<bool>();
                using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
                await tcs.Task;
                return [];
            });

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object);

        // Передаем короткую длительность аренды (60ms), чтобы цикл продления сработал через 20ms
        var success = await service.ExecuteAnalysisAsync(94, token, default, TimeSpan.FromMilliseconds(60));

        Assert.False(success);
        // Не изменяем задачу чужого воркера
        analysisRepo.Verify(x => x.FailAsync(94, token, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        // Не удаляем временные файлы, так как ими может пользоваться новый воркер
        sourceService.Verify(x => x.CleanupAsync(94, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenTaskStolenBeforeCompletion_DoesNotCallCleanupAndReturnsFalse()
    {
        var token = Guid.NewGuid();
        var analysis = new Analysis
        {
            Id = 95,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 1,
                CommitHash = "hash95",
                Project = new Project { RepoUrl = "https://github.com/repo", OwnerId = 1 }
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        credentialService.Setup(x => x.GetUserTokenAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        analysisRepo.Setup(x => x.GetByIdAsync(95, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        // Задача была перехвачена в БД, поэтому CompleteAsync возвращает false
        analysisRepo.Setup(x => x.CompleteAsync(95, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 95, "ghp_test_token", It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\Temp");
        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object);

        var success = await service.ExecuteAnalysisAsync(95, token);

        Assert.False(success);
        // Не удаляем временные файлы, так как ими уже владеет другой воркер
        sourceService.Verify(x => x.CleanupAsync(95, It.IsAny<CancellationToken>()), Times.Never);
        analysisRepo.Verify(x => x.FailAsync(95, token, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenTaskStolenBeforeFail_DoesNotCallCleanupAndReturnsFalse()
    {
        var token = Guid.NewGuid();
        var analysis = new Analysis
        {
            Id = 96,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 1,
                CommitHash = "hash96",
                Project = new Project { RepoUrl = "https://github.com/repo", OwnerId = 1 }
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        credentialService.Setup(x => x.GetUserTokenAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        analysisRepo.Setup(x => x.GetByIdAsync(96, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 96, "ghp_test_token", It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\Temp");
        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Analysis error"));

        // Задача перехвачена в БД другим воркером, поэтому FailAsync возвращает false
        analysisRepo.Setup(x => x.FailAsync(96, token, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object);

        var success = await service.ExecuteAnalysisAsync(96, token);

        Assert.False(success);
        // Не удаляем временные файлы, так как ими уже владеет другой воркер
        sourceService.Verify(x => x.CleanupAsync(96, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenAnalyzerFailsAndCleanupThrows_ReturnsFalseAndDoesNotMaskFailure()
    {
        var token = Guid.NewGuid();
        var analysis = new Analysis
        {
            Id = 97,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 1,
                CommitHash = "hash97",
                Project = new Project { RepoUrl = "https://github.com/repo", OwnerId = 1 }
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        credentialService.Setup(x => x.GetUserTokenAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        analysisRepo.Setup(x => x.GetByIdAsync(97, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 97, "ghp_test_token", It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\Temp");
        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Fatal analysis error"));

        analysisRepo.Setup(x => x.FailAsync(97, token, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Исключение при очистке
        sourceService.Setup(x => x.CleanupAsync(97, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Access denied during cleanup"));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object);

        var success = await service.ExecuteAnalysisAsync(97, token);

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(97, token, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        sourceService.Verify(x => x.CleanupAsync(97, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_OwnerHasPat_PassesPatToRepositorySourceService()
    {
        var token = Guid.NewGuid();
        var analysis = new Analysis
        {
            Id = 301,
            CommitId = 10,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 10,
                CommitHash = "hash-stage85",
                Project = new Project { RepoUrl = "https://github.com/org/repo-stage85", OwnerId = 15 }
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        analysisRepo.Setup(x => x.GetByIdAsync(301, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo.Setup(x => x.CompleteAsync(301, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        credentialService.Setup(x => x.GetUserTokenAsync(15, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_owner_secret_pat_stage85");

        sourceService.Setup(x => x.DownloadAndExtractAsync(
            "https://github.com/org/repo-stage85",
            "hash-stage85",
            301,
            "ghp_owner_secret_pat_stage85",
            It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\TempStage85");

        analyzer.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object);

        var success = await service.ExecuteAnalysisAsync(301, token);

        Assert.True(success);
        sourceService.Verify(x => x.DownloadAndExtractAsync(
            "https://github.com/org/repo-stage85",
            "hash-stage85",
            301,
            "ghp_owner_secret_pat_stage85",
            It.IsAny<CancellationToken>()), Times.Once);
        sourceService.Verify(x => x.CleanupAsync(301, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_AdminStartedAnalysis_UsesProjectOwnerPat_NeverAdminPat()
    {
        var token = Guid.NewGuid();
        const int projectOwnerId = 42;
        const int adminId = 1;

        var analysis = new Analysis
        {
            Id = 302,
            CommitId = 20,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 20,
                CommitHash = "hash-admin-test",
                Project = new Project { RepoUrl = "https://github.com/owner/project", OwnerId = projectOwnerId }
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        analysisRepo.Setup(x => x.GetByIdAsync(302, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo.Setup(x => x.CompleteAsync(302, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        credentialService.Setup(x => x.GetUserTokenAsync(projectOwnerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_owner_pat_42");
        credentialService.Setup(x => x.GetUserTokenAsync(adminId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_admin_pat_1");

        sourceService.Setup(x => x.DownloadAndExtractAsync(
            "https://github.com/owner/project",
            "hash-admin-test",
            302,
            "ghp_owner_pat_42",
            It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\TempOwner");

        analyzer.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object);

        var success = await service.ExecuteAnalysisAsync(302, token);

        Assert.True(success);

        // Убеждаемся, что вызван именно токен владельца проекта (OwnerId = 42)
        credentialService.Verify(x => x.GetUserTokenAsync(projectOwnerId, It.IsAny<CancellationToken>()), Times.Once);

        // Убеждаемся, что токен админа никогда не запрашивался
        credentialService.Verify(x => x.GetUserTokenAsync(adminId, It.IsAny<CancellationToken>()), Times.Never);

        sourceService.Verify(x => x.DownloadAndExtractAsync(
            "https://github.com/owner/project",
            "hash-admin-test",
            302,
            "ghp_owner_pat_42",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenOwnerHasNoPat_MarksTaskFailedWithTokenRequired_AndNeverCallsSourceService()
    {
        var token = Guid.NewGuid();
        var analysis = new Analysis
        {
            Id = 303,
            CommitId = 30,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 30,
                CommitHash = "hash30",
                Project = new Project { RepoUrl = "https://github.com/owner/repo", OwnerId = 77 }
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        analysisRepo.Setup(x => x.GetByIdAsync(303, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo.Setup(x => x.FailAsync(303, token, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Владелец не имеет PAT
        credentialService.Setup(x => x.GetUserTokenAsync(77, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object);

        var success = await service.ExecuteAnalysisAsync(303, token);

        Assert.False(success);

        // Задача должна быть помечена как Failed с понятным сообщением
        analysisRepo.Verify(x => x.FailAsync(
            303,
            token,
            It.Is<string>(msg => msg.Contains("токена GitHub", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<CancellationToken>()), Times.Once);

        // Скачивание никогда не должно вызываться
        sourceService.Verify(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // Временные файлы очищаются
        sourceService.Verify(x => x.CleanupAsync(303, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public async Task ExecuteAnalysisAsync_WhenOwnerPatIsWhitespace_MarksTaskFailedWithTokenRequired_AndNeverCallsSourceService(string whitespaceToken)
    {
        var token = Guid.NewGuid();
        var analysis = new Analysis
        {
            Id = 304,
            CommitId = 31,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 31,
                CommitHash = "hash31",
                Project = new Project { RepoUrl = "https://github.com/owner/repo", OwnerId = 88 }
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        analysisRepo.Setup(x => x.GetByIdAsync(304, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo.Setup(x => x.FailAsync(304, token, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        credentialService.Setup(x => x.GetUserTokenAsync(88, It.IsAny<CancellationToken>()))
            .ReturnsAsync(whitespaceToken);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object);

        var success = await service.ExecuteAnalysisAsync(304, token);

        Assert.False(success);

        analysisRepo.Verify(x => x.FailAsync(
            304,
            token,
            It.Is<string>(msg => msg.Contains("токена GitHub", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<CancellationToken>()), Times.Once);

        sourceService.Verify(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);

        sourceService.Verify(x => x.CleanupAsync(304, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenOwnerPatIsExpired_MarksTaskFailed()
    {
        var token = Guid.NewGuid();
        var analysis = new Analysis
        {
            Id = 305,
            CommitId = 32,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 32,
                CommitHash = "hash32",
                Project = new Project { RepoUrl = "https://github.com/owner/repo", OwnerId = 99 }
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        analysisRepo.Setup(x => x.GetByIdAsync(305, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo.Setup(x => x.FailAsync(305, token, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        credentialService.Setup(x => x.GetUserTokenAsync(99, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_expired_token");

        sourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            305,
            "ghp_expired_token",
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.TokenExpired, "Персональный токен GitHub недействителен или отозван."));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object);

        var success = await service.ExecuteAnalysisAsync(305, token);

        Assert.False(success);

        analysisRepo.Verify(x => x.FailAsync(
            305,
            token,
            It.Is<string>(msg => msg.Contains("недействителен или отозван", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<CancellationToken>()), Times.Once);

        sourceService.Verify(x => x.CleanupAsync(305, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenGitHubReturns403RateLimited_MarksTaskFailed()
    {
        var token = Guid.NewGuid();
        var analysis = new Analysis
        {
            Id = 306,
            CommitId = 33,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 33,
                CommitHash = "hash33",
                Project = new Project { RepoUrl = "https://github.com/owner/repo", OwnerId = 100 }
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        analysisRepo.Setup(x => x.GetByIdAsync(306, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo.Setup(x => x.FailAsync(306, token, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        credentialService.Setup(x => x.GetUserTokenAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_rate_limited");

        sourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            306,
            "ghp_rate_limited",
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.RateLimited, "Превышен лимит запросов к GitHub API (Rate Limit)."));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object);

        var success = await service.ExecuteAnalysisAsync(306, token);

        Assert.False(success);

        analysisRepo.Verify(x => x.FailAsync(
            306,
            token,
            It.Is<string>(msg => msg.Contains("лимит запросов", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<CancellationToken>()), Times.Once);

        sourceService.Verify(x => x.CleanupAsync(306, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenGitHubReturns403AccessDenied_MarksTaskFailed()
    {
        var token = Guid.NewGuid();
        var analysis = new Analysis
        {
            Id = 307,
            CommitId = 34,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 34,
                CommitHash = "hash34",
                Project = new Project { RepoUrl = "https://github.com/owner/repo", OwnerId = 101 }
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        analysisRepo.Setup(x => x.GetByIdAsync(307, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo.Setup(x => x.FailAsync(307, token, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        credentialService.Setup(x => x.GetUserTokenAsync(101, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_access_denied");

        sourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            307,
            "ghp_access_denied",
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.AccessDenied, "Доступ к репозиторию GitHub запрещен (недостаточно прав или ограничения организации)."));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object);

        var success = await service.ExecuteAnalysisAsync(307, token);

        Assert.False(success);

        analysisRepo.Verify(x => x.FailAsync(
            307,
            token,
            It.Is<string>(msg => msg.Contains("запрещен", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<CancellationToken>()), Times.Once);

        sourceService.Verify(x => x.CleanupAsync(307, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenGitHubReturns404RepoNotFound_MarksTaskFailed()
    {
        var token = Guid.NewGuid();
        var analysis = new Analysis
        {
            Id = 308,
            CommitId = 35,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 35,
                CommitHash = "hash35",
                Project = new Project { RepoUrl = "https://github.com/owner/repo", OwnerId = 102 }
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        analysisRepo.Setup(x => x.GetByIdAsync(308, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo.Setup(x => x.FailAsync(308, token, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        credentialService.Setup(x => x.GetUserTokenAsync(102, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_repo_not_found");

        sourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            308,
            "ghp_repo_not_found",
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.RepoNotFound, "Репозиторий GitHub не найден или токен не имеет к нему доступа."));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object);

        var success = await service.ExecuteAnalysisAsync(308, token);

        Assert.False(success);

        analysisRepo.Verify(x => x.FailAsync(
            308,
            token,
            It.Is<string>(msg => msg.Contains("не найден", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<CancellationToken>()), Times.Once);

        sourceService.Verify(x => x.CleanupAsync(308, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WhenGitHubServiceUnavailable_MarksTaskFailed()
    {
        var token = Guid.NewGuid();
        var analysis = new Analysis
        {
            Id = 309,
            CommitId = 36,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 36,
                CommitHash = "hash36",
                Project = new Project { RepoUrl = "https://github.com/owner/repo", OwnerId = 103 }
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        analysisRepo.Setup(x => x.GetByIdAsync(309, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo.Setup(x => x.FailAsync(309, token, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        credentialService.Setup(x => x.GetUserTokenAsync(103, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_service_unavailable");

        sourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            309,
            "ghp_service_unavailable",
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.ServiceUnavailable, "Сервис GitHub временно недоступен."));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object);

        var success = await service.ExecuteAnalysisAsync(309, token);

        Assert.False(success);

        analysisRepo.Verify(x => x.FailAsync(
            309,
            token,
            It.Is<string>(msg => msg.Contains("временно недоступен", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<CancellationToken>()), Times.Once);

        sourceService.Verify(x => x.CleanupAsync(309, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_PatIsNeverRetainedInServiceFields()
    {
        var token = Guid.NewGuid();
        const string secretPat = "ghp_super_secret_pat_9876543210zyxwv";

        var analysis = new Analysis
        {
            Id = 310,
            CommitId = 37,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 37,
                CommitHash = "hash37",
                Project = new Project { RepoUrl = "https://github.com/owner/repo", OwnerId = 104 }
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        analysisRepo.Setup(x => x.GetByIdAsync(310, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo.Setup(x => x.CompleteAsync(310, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        credentialService.Setup(x => x.GetUserTokenAsync(104, It.IsAny<CancellationToken>()))
            .ReturnsAsync(secretPat);

        sourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            310,
            secretPat,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\TempSecret");

        analyzer.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object);

        await service.ExecuteAnalysisAsync(310, token);

        // Рефлексивный аудит: ни одно поле AnalysisService не должно содержать сырой токен
        var fields = typeof(AnalysisService).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        foreach (var field in fields)
        {
            var value = field.GetValue(service);
            Assert.NotEqual(secretPat, value);
            if (value is string strVal)
            {
                Assert.DoesNotContain(secretPat, strVal, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_ErrorMessageDoesNotContainPat()
    {
        var token = Guid.NewGuid();
        const string rawPat = "ghp_leaked_token_should_never_appear_in_error";

        var analysis = new Analysis
        {
            Id = 311,
            CommitId = 38,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 38,
                CommitHash = "hash38",
                Project = new Project { RepoUrl = "https://github.com/owner/repo", OwnerId = 105 }
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();
        var analyzer = new Mock<IAnalyzer>();

        analysisRepo.Setup(x => x.GetByIdAsync(311, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);

        string? recordedErrorMessage = null;
        analysisRepo.Setup(x => x.FailAsync(311, token, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<int, Guid, string?, CancellationToken>((_, _, msg, _) => recordedErrorMessage = msg)
            .ReturnsAsync(true);

        credentialService.Setup(x => x.GetUserTokenAsync(105, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rawPat);

        // Симулируем выброс исключения со строкой токена в сообщении
        sourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            311,
            rawPat,
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException($"Download failed with token {rawPat}"));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object);

        var success = await service.ExecuteAnalysisAsync(311, token);

        Assert.False(success);
        Assert.NotNull(recordedErrorMessage);
        Assert.DoesNotContain(rawPat, recordedErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ghp_", recordedErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Во время выполнения анализа произошла внутренняя ошибка.", recordedErrorMessage);
    }

    [Fact]
    public void AnalysisService_Constructor_ThrowsArgumentNullException_WhenCredentialServiceIsNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AnalysisService(
                Mock.Of<IAnalysisRepository>(),
                Mock.Of<ICommitRepository>(),
                Mock.Of<IRepositorySourceService>(),
                [],
                null!));
    }

    [Theory]
    [InlineData("ghp_test_token_classic_1234567890")]
    [InlineData("github_pat_test_token_finegrained_abcdef")]
    [InlineData("gho_test_token_oauth_1234567890")]
    [InlineData("ghu_test_token_user_to_server_12345")]
    [InlineData("ghs_test_token_server_to_server_1234")]
    [InlineData("ghr_test_token_refresh_1234567890")]
    [InlineData("bearer test_bearer_token_string")]
    [InlineData("authorization: secret_token_data")]
    public void SanitizeErrorMessage_MasksAllSupportedGitHubTokenPrefixesAndAuthHeaders(string sensitiveSnippet)
    {
        var exception = new InvalidOperationException($"Error occurred with {sensitiveSnippet} during analysis");

        var sanitized = AnalysisService.SanitizeErrorMessage(exception);

        Assert.Equal("Во время выполнения анализа произошла внутренняя ошибка.", sanitized);
        Assert.DoesNotContain(sensitiveSnippet, sanitized, StringComparison.OrdinalIgnoreCase);
    }


    [Theory]
    [InlineData("CancellationToken was cancelled.")]
    [InlineData("The operation was canceled: CancellationToken was cancelled")]
    [InlineData("Validation failed: password complexity requirements not met")]
    [InlineData("Secret key configuration is missing")]
    [InlineData("Service credentials could not be loaded")]
    [InlineData("Token syntax error in lexer at line 42")]
    public void SanitizeErrorMessage_PreservesTechnicalMessages_WithoutLeakedValues(string technicalMessage)
    {
        var exception = new InvalidOperationException(technicalMessage);

        var sanitized = AnalysisService.SanitizeErrorMessage(exception);

        Assert.Equal(technicalMessage, sanitized);
    }

    [Theory]
    [InlineData("Failed with Bearer my_secret_token_value_12345")]
    [InlineData("Request header Authorization: Bearer secret_pat_value_here")]
    [InlineData("Header corrupted: Authorization: secret_raw_token")]
    [InlineData("Config dump: api_key=secret_api_key_value")]
    [InlineData("Config dump: password=super_secret_password")]
    [InlineData("Config dump: secret=super_secret_vault_value")]
    [InlineData("Config dump: pwd=my_db_pwd_value")]
    [InlineData("Config dump: private_key=rsa_private_key_value")]
    [InlineData("Config dump: token=assigned_secret_token_value")]
    public void SanitizeErrorMessage_MasksContextualCredentialLeaks(string leakMessage)
    {
        var exception = new InvalidOperationException(leakMessage);

        var sanitized = AnalysisService.SanitizeErrorMessage(exception);

        Assert.Equal("Во время выполнения анализа произошла внутренняя ошибка.", sanitized);
    }

    [Fact]
    public void SanitizeErrorMessage_MasksSensitiveData_InInnerException()
    {
        var innerException = new InvalidOperationException("Failed to connect: password=secret_password_123");
        var rootException = new ApplicationException("Outer operation failed", innerException);

        var sanitized = AnalysisService.SanitizeErrorMessage(rootException);

        Assert.Equal("Во время выполнения анализа произошла внутренняя ошибка.", sanitized);
        Assert.DoesNotContain("secret_password_123", sanitized);
    }

    [Fact]
    public void SanitizeErrorMessage_MasksSensitiveData_InAggregateException()
    {
        var inner1 = new InvalidOperationException("Safe preliminary error");
        var inner2 = new InvalidOperationException("Leaked token: ghp_test_token_leaked_in_aggregate_12345");
        var aggregate = new AggregateException("Multiple errors occurred", inner1, inner2);

        var sanitized = AnalysisService.SanitizeErrorMessage(aggregate);

        Assert.Equal("Во время выполнения анализа произошла внутренняя ошибка.", sanitized);
        Assert.DoesNotContain("ghp_test", sanitized);
    }

    [Fact]
    public void SanitizeErrorMessage_PreservesInformativeMessage_InAggregateException_WhenSafe()
    {
        var inner = new InvalidOperationException("CancellationToken was cancelled during repository download.");
        var aggregate = new AggregateException("Task failed", inner);

        var sanitized = AnalysisService.SanitizeErrorMessage(aggregate);

        Assert.Equal("CancellationToken was cancelled during repository download.", sanitized);
    }
}
