using IAnalysisLeaseGuard = Application.Interfaces.IAnalysisLeaseGuard;
using IAnalysisLeaseGuardProvider = Application.Interfaces.IAnalysisLeaseGuardProvider;
using DatabaseAnalyzer.Runner;
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

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], Mock.Of<IGitHubCredentialService>(), new InMemoryAnalysisLeaseGuardProvider());

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

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], Mock.Of<IGitHubCredentialService>(), new InMemoryAnalysisLeaseGuardProvider());

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
        analysisRepo.Setup(x => x.CompleteAsync(50, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        sourceService.Setup(x => x.DownloadAndExtractAsync("https://github.com/test/repo", "hash123", 50, token, "ghp_test_token", It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
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

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(50, token);

        Assert.True(success);

        analysisRepo.Verify(x => x.CompleteAsync(50, token, It.Is<IReadOnlyList<AnalysisResult>>(r => r.Count == 1), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
        sourceService.Verify(x => x.CleanupAsync(50, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
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
        analysisRepo.Setup(x => x.FailAsync(51, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 51, token, "ghp_test_token", It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\TempRepo");

        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Roslyn crash"));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(51, token);

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(51, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
        sourceService.Verify(x => x.CleanupAsync(51, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
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
        analysisRepo.Setup(x => x.CompleteAsync(52, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 52, token, "ghp_test_token", It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\TempRepo");
        sourceService.Setup(x => x.CleanupAsync(52, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Directory locked by another process"));

        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(52, token);

        Assert.True(success);
        analysisRepo.Verify(x => x.CompleteAsync(52, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
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

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

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
        analysisRepo.Setup(x => x.CompleteAsync(77, executionToken, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 77, executionToken, "ghp_test_token", It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\Temp");
        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        using var backgroundServiceCts = new CancellationTokenSource();
        var executionSuccess = await service.ExecuteAnalysisAsync(77, executionToken, backgroundServiceCts.Token);

        Assert.True(executionSuccess);
        analysisRepo.Verify(x => x.CompleteAsync(77, executionToken, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
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
            sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 1, token, "ghp_test_token", It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, int, Guid, string, IAnalysisLeaseGuard, CancellationToken>((url, _, _, _, _, _, _) => requestedRepoUrl = url)
                .ReturnsAsync("C:\\TempExtracted");

            var service = new AnalysisService(analysisRepo, commitRepo, sourceService.Object, [analyzer.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

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

        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 88, token, "ghp_test_token", It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, int, Guid, string, IAnalysisLeaseGuard, CancellationToken>(async (_, _, _, _, _, _, ct) =>
            {
                shutdownCts.Cancel(); // Имитируем остановку приложения во время скачивания
                ct.ThrowIfCancellationRequested();
                await Task.CompletedTask;
                return "C:\\Temp";
            });

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExecuteAnalysisAsync(88, token, shutdownCts.Token));

        // Гарантируем, что задача НЕ помечена как сбойная (Failed)
        analysisRepo.Verify(x => x.FailAsync(88, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);

        // Гарантируем, что временные файлы всё равно очищены
        sourceService.Verify(x => x.CleanupAsync(88, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
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
        analysisRepo.Setup(x => x.FailAsync(91, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], Mock.Of<IGitHubCredentialService>(), new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(91, token);

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(91, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
        sourceService.Verify(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        sourceService.Verify(x => x.CleanupAsync(91, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
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
        analysisRepo.Setup(x => x.FailAsync(92, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], Mock.Of<IGitHubCredentialService>(), new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(92, token);

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(92, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
        sourceService.Verify(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        sourceService.Verify(x => x.CleanupAsync(92, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
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
        analysisRepo.Setup(x => x.FailAsync(93, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], Mock.Of<IGitHubCredentialService>(), new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(93, token);

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(93, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
        sourceService.Verify(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        sourceService.Verify(x => x.CleanupAsync(93, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
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

        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 94, token, "ghp_test_token", It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
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

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        // Передаем короткую длительность аренды (60ms), чтобы цикл продления сработал через 20ms
        var success = await service.ExecuteAnalysisAsync(94, token, default, TimeSpan.FromMilliseconds(60));

        Assert.False(success);
        // Не изменяем задачу чужого воркера
        analysisRepo.Verify(x => x.FailAsync(94, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        // Не удаляем временные файлы, так как ими может пользоваться новый воркер
        sourceService.Verify(x => x.CleanupAsync(94, It.IsAny<Guid>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
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
        analysisRepo.Setup(x => x.CompleteAsync(95, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 95, token, "ghp_test_token", It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\Temp");
        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(95, token);

        Assert.False(success);
        // Не удаляем временные файлы, так как ими уже владеет другой воркер
        sourceService.Verify(x => x.CleanupAsync(95, It.IsAny<Guid>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepo.Verify(x => x.FailAsync(95, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
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
        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 96, token, "ghp_test_token", It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\Temp");
        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Analysis error"));

        // Задача перехвачена в БД другим воркером, поэтому FailAsync возвращает false
        analysisRepo.Setup(x => x.FailAsync(96, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(96, token);

        Assert.False(success);
        // Не удаляем временные файлы, так как ими уже владеет другой воркер
        sourceService.Verify(x => x.CleanupAsync(96, It.IsAny<Guid>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
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
        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 97, token, "ghp_test_token", It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\Temp");
        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Fatal analysis error"));

        analysisRepo.Setup(x => x.FailAsync(97, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Исключение при очистке
        sourceService.Setup(x => x.CleanupAsync(97, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Access denied during cleanup"));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(97, token);

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(97, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
        sourceService.Verify(x => x.CleanupAsync(97, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
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
        analysisRepo.Setup(x => x.CompleteAsync(301, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        credentialService.Setup(x => x.GetUserTokenAsync(15, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_owner_secret_pat_stage85");

        sourceService.Setup(x => x.DownloadAndExtractAsync(
            "https://github.com/org/repo-stage85",
            "hash-stage85",
            301,
            token,
            "ghp_owner_secret_pat_stage85",
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\TempStage85");

        analyzer.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(301, token);

        Assert.True(success);
        sourceService.Verify(x => x.DownloadAndExtractAsync(
            "https://github.com/org/repo-stage85",
            "hash-stage85",
            301,
            token,
            "ghp_owner_secret_pat_stage85",
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        sourceService.Verify(x => x.CleanupAsync(301, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
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
        analysisRepo.Setup(x => x.CompleteAsync(302, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        credentialService.Setup(x => x.GetUserTokenAsync(projectOwnerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_owner_pat_42");
        credentialService.Setup(x => x.GetUserTokenAsync(adminId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_admin_pat_1");

        sourceService.Setup(x => x.DownloadAndExtractAsync(
            "https://github.com/owner/project",
            "hash-admin-test",
            302,
            token,
            "ghp_owner_pat_42",
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\TempOwner");

        analyzer.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

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
            token,
            "ghp_owner_pat_42",
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        sourceService.Verify(x => x.CleanupAsync(302, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
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
        analysisRepo.Setup(x => x.FailAsync(303, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Владелец не имеет PAT
        credentialService.Setup(x => x.GetUserTokenAsync(77, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(303, token);

        Assert.False(success);

        // Задача должна быть помечена как Failed с понятным сообщением
        analysisRepo.Verify(x => x.FailAsync(
            303,
            token,
            It.Is<string>(msg => msg.Contains("токена GitHub", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Скачивание никогда не должно вызываться
        sourceService.Verify(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // Временные файлы очищаются
        sourceService.Verify(x => x.CleanupAsync(303, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
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
        analysisRepo.Setup(x => x.FailAsync(304, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        credentialService.Setup(x => x.GetUserTokenAsync(88, It.IsAny<CancellationToken>()))
            .ReturnsAsync(whitespaceToken);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(304, token);

        Assert.False(success);

        analysisRepo.Verify(x => x.FailAsync(
            304,
            token,
            It.Is<string>(msg => msg.Contains("токена GitHub", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);

        sourceService.Verify(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);

        sourceService.Verify(x => x.CleanupAsync(304, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
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
        analysisRepo.Setup(x => x.FailAsync(305, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        credentialService.Setup(x => x.GetUserTokenAsync(99, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_expired_token");

        sourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            305,
            token,
            "ghp_expired_token",
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.TokenExpired, "Персональный токен GitHub недействителен или отозван."));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(305, token);

        Assert.False(success);

        analysisRepo.Verify(x => x.FailAsync(
            305,
            token,
            It.Is<string>(msg => msg.Contains("недействителен или отозван", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);

        sourceService.Verify(x => x.CleanupAsync(305, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
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
        analysisRepo.Setup(x => x.FailAsync(306, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        credentialService.Setup(x => x.GetUserTokenAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_rate_limited");

        sourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            306,
            token,
            "ghp_rate_limited",
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.RateLimited, "Превышен лимит запросов к GitHub API (Rate Limit)."));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(306, token);

        Assert.False(success);

        analysisRepo.Verify(x => x.FailAsync(
            306,
            token,
            It.Is<string>(msg => msg.Contains("лимит запросов", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);

        sourceService.Verify(x => x.CleanupAsync(306, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
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
        analysisRepo.Setup(x => x.FailAsync(307, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        credentialService.Setup(x => x.GetUserTokenAsync(101, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_access_denied");

        sourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            307,
            token,
            "ghp_access_denied",
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.AccessDenied, "Доступ к репозиторию GitHub запрещен (недостаточно прав или ограничения организации)."));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(307, token);

        Assert.False(success);

        analysisRepo.Verify(x => x.FailAsync(
            307,
            token,
            It.Is<string>(msg => msg.Contains("запрещен", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);

        sourceService.Verify(x => x.CleanupAsync(307, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
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
        analysisRepo.Setup(x => x.FailAsync(308, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        credentialService.Setup(x => x.GetUserTokenAsync(102, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_repo_not_found");

        sourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            308,
            token,
            "ghp_repo_not_found",
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.RepoNotFound, "Репозиторий GitHub не найден или токен не имеет к нему доступа."));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(308, token);

        Assert.False(success);

        analysisRepo.Verify(x => x.FailAsync(
            308,
            token,
            It.Is<string>(msg => msg.Contains("не найден", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);

        sourceService.Verify(x => x.CleanupAsync(308, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
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
        analysisRepo.Setup(x => x.FailAsync(309, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        credentialService.Setup(x => x.GetUserTokenAsync(103, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_service_unavailable");

        sourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            309,
            token,
            "ghp_service_unavailable",
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.ServiceUnavailable, "Сервис GitHub временно недоступен."));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(309, token);

        Assert.False(success);

        analysisRepo.Verify(x => x.FailAsync(
            309,
            token,
            It.Is<string>(msg => msg.Contains("временно недоступен", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);

        sourceService.Verify(x => x.CleanupAsync(309, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
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
        analysisRepo.Setup(x => x.CompleteAsync(310, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        credentialService.Setup(x => x.GetUserTokenAsync(104, It.IsAny<CancellationToken>()))
            .ReturnsAsync(secretPat);

        sourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            310,
            token,
            secretPat,
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\TempSecret");

        analyzer.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

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
        analysisRepo.Setup(x => x.FailAsync(311, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Callback<int, Guid, string?, IAnalysisLeaseGuard, CancellationToken>((_, _, msg, _, _) => recordedErrorMessage = msg)
            .ReturnsAsync(true);

        credentialService.Setup(x => x.GetUserTokenAsync(105, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rawPat);

        // Симулируем выброс исключения со строкой токена в сообщении
        sourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            311,
            token,
            rawPat,
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException($"Download failed with token {rawPat}"));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

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
                null!,
                new InMemoryAnalysisLeaseGuardProvider()));

        Assert.Throws<ArgumentNullException>(() =>
            new AnalysisService(
                Mock.Of<IAnalysisRepository>(),
                Mock.Of<ICommitRepository>(),
                Mock.Of<IRepositorySourceService>(),
                [],
                Mock.Of<IGitHubCredentialService>(),
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

    #region TD-002: Pipeline Fault-Tolerance for DatabaseAnalyzer

    private static (Analysis analysis, Guid token, Mock<IAnalysisRepository> analysisRepo, Mock<ICommitRepository> commitRepo, Mock<IRepositorySourceService> sourceService, Mock<IGitHubCredentialService> credentialService) CreateAnalysisPipelineContext(int analysisId = 101)
    {
        var token = Guid.NewGuid();
        var analysis = new Analysis
        {
            Id = analysisId,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            Commit = new Commit
            {
                Id = 1,
                CommitHash = "hash-td002",
                Project = new Project { RepoUrl = "https://github.com/test/repo", OwnerId = 1 }
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var credentialService = new Mock<IGitHubCredentialService>();

        credentialService.Setup(x => x.GetUserTokenAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        analysisRepo.Setup(x => x.GetByIdAsync(analysisId, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo.Setup(x => x.FailAsync(analysisId, token, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        analysisRepo.Setup(x => x.CompleteAsync(analysisId, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), analysisId, token, "ghp_test_token", It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\TempRepo");

        return (analysis, token, analysisRepo, commitRepo, sourceService, credentialService);
    }

    [Fact]
    public async Task TD002_Test1_WhenDatabaseAnalyzerFindsNoDbContext_CompletesWithInfoAndPreservesOtherResults()
    {
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(111);

        var csMock = new Mock<IAnalyzer>();
        csMock.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
        csMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding { Rule = "CS001", Message = "Large Class", Severity = Severity.Warning }]);

        var dbMock = new Mock<IAnalyzer>();
        dbMock.SetupGet(a => a.Type).Returns(AnalyzerType.Database);
        dbMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding
            {
                Rule = DatabaseAnalyzerRules.MissingDbContext,
                Message = "В проекте не найдено контекстов данных Entity Framework Core (DbContext). Проверки схемы базы данных пропущены.",
                Severity = Severity.Info
            }]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [csMock.Object, dbMock.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.True(success);
        analysisRepo.Verify(x => x.CompleteAsync(
            analysis.Id,
            token,
            It.Is<IReadOnlyList<AnalysisResult>>(r =>
                r.Count == 2 &&
                r.Any(x => x.Rule == "CS001" && x.AnalyzerType == AnalyzerType.CSharp) &&
                r.Any(x => x.Rule == DatabaseAnalyzerRules.MissingDbContext && x.Severity == Severity.Info && x.AnalyzerType == AnalyzerType.Database)),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_Test2_WhenDatabaseAnalyzerThrowsException_CompletesWithWarningAndPreservesOtherResults()
    {
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(112);

        var csMock = new Mock<IAnalyzer>();
        csMock.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
        csMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding { Rule = "CS001", Message = "Large Class", Severity = Severity.Warning }]);

        var dbMock = new Mock<IAnalyzer>();
        dbMock.SetupGet(a => a.Type).Returns(AnalyzerType.Database);
        dbMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Fatal database engine crash"));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [csMock.Object, dbMock.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.True(success);
        analysisRepo.Verify(x => x.CompleteAsync(
            analysis.Id,
            token,
            It.Is<IReadOnlyList<AnalysisResult>>(r =>
                r.Count == 2 &&
                r.Any(x => x.Rule == "CS001" && x.AnalyzerType == AnalyzerType.CSharp) &&
                r.Any(x => x.Rule == DatabaseAnalyzerRules.InternalAnalyzerError &&
                           x.Severity == Severity.Warning &&
                           x.Message.Contains("Fatal database engine crash") &&
                           x.AnalyzerType == AnalyzerType.Database)),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_Test3_WhenDatabaseContainerFails_CompletesWithWarningAndPreservesOtherResults()
    {
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(113);

        var csMock = new Mock<IAnalyzer>();
        csMock.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
        csMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding { Rule = "CS001", Message = "Large Class", Severity = Severity.Warning }]);

        var dbMock = new Mock<IAnalyzer>();
        dbMock.SetupGet(a => a.Type).Returns(AnalyzerType.Database);
        dbMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding
            {
                Rule = DatabaseAnalyzerRules.ContainerError,
                Message = "Ошибка выполнения изолированного анализатора базы данных: non-zero exit code 1",
                Severity = Severity.Warning,
                Recommendation = "Проверьте доступность контейнерной среды и корректность структуры моделей."
            }]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [csMock.Object, dbMock.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.True(success);
        analysisRepo.Verify(x => x.CompleteAsync(
            analysis.Id,
            token,
            It.Is<IReadOnlyList<AnalysisResult>>(r =>
                r.Count == 2 &&
                r.Any(x => x.Rule == "CS001" && x.AnalyzerType == AnalyzerType.CSharp) &&
                r.Any(x => x.Rule == DatabaseAnalyzerRules.ContainerError && x.Severity == Severity.Warning && x.AnalyzerType == AnalyzerType.Database)),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_Test4_WhenDatabaseAnalyzerTimesOut_CompletesWithWarningAndPreservesOtherResults()
    {
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(114);

        var csMock = new Mock<IAnalyzer>();
        csMock.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
        csMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding { Rule = "CS001", Message = "Large Class", Severity = Severity.Warning }]);

        var dbMock = new Mock<IAnalyzer>();
        dbMock.SetupGet(a => a.Type).Returns(AnalyzerType.Database);
        dbMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding
            {
                Rule = DatabaseAnalyzerRules.ContainerTimeout,
                Message = "Превышено максимальное время выполнения изолированного анализатора базы данных (таймаут 60 с).",
                Severity = Severity.Warning,
                Recommendation = "Увеличьте лимиты времени или проверьте размер и сложность анализируемой модели базы данных."
            }]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [csMock.Object, dbMock.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.True(success);
        analysisRepo.Verify(x => x.CompleteAsync(
            analysis.Id,
            token,
            It.Is<IReadOnlyList<AnalysisResult>>(r =>
                r.Count == 2 &&
                r.Any(x => x.Rule == "CS001" && x.AnalyzerType == AnalyzerType.CSharp) &&
                r.Any(x => x.Rule == DatabaseAnalyzerRules.ContainerTimeout && x.Severity == Severity.Warning && x.AnalyzerType == AnalyzerType.Database)),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_Test5_WhenDatabaseAnalyzerReportsPartialMetadata_PreservesValidResultsAndDiagnostics()
    {
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(115);

        var csMock = new Mock<IAnalyzer>();
        csMock.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
        csMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding { Rule = "CS001", Message = "Large Class", Severity = Severity.Warning }]);

        var dbMock = new Mock<IAnalyzer>();
        dbMock.SetupGet(a => a.Type).Returns(AnalyzerType.Database);
        dbMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new AnalyzerFinding { Rule = "DB01", Message = "Primary Key Required", Severity = Severity.Warning },
                new AnalyzerFinding { Rule = DatabaseAnalyzerRules.RoslynWarning, Message = "Неполные метаданные для IncompleteEntity", Severity = Severity.Warning }
            ]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [csMock.Object, dbMock.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.True(success);
        analysisRepo.Verify(x => x.CompleteAsync(
            analysis.Id,
            token,
            It.Is<IReadOnlyList<AnalysisResult>>(r =>
                r.Count == 3 &&
                r.Any(x => x.Rule == "CS001") &&
                r.Any(x => x.Rule == "DB01") &&
                r.Any(x => x.Rule == DatabaseAnalyzerRules.RoslynWarning && x.Severity == Severity.Warning)),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_Test6_WhenAllAnalyzersFail_MarksAnalysisFailed()
    {
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(116);

        var dbMock = new Mock<IAnalyzer>();
        dbMock.SetupGet(a => a.Type).Returns(AnalyzerType.Database);
        dbMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Solo database analyzer crashed"));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [dbMock.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(
            analysis.Id,
            token,
            It.Is<string?>(s => s != null && s.Contains("Все анализаторы завершились ошибкой")),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_Test7_WhenGlobalCancellationTriggered_RethrowsAndDoesNotConvertToWarning()
    {
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(117);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var dbMock = new Mock<IAnalyzer>();
        dbMock.SetupGet(a => a.Type).Returns(AnalyzerType.Database);
        dbMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [dbMock.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteAnalysisAsync(analysis.Id, token, cts.Token));

        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepo.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_Test8_WhenAllAnalyzersSucceed_CompletesWithAllResultsAndNoExtraWarnings()
    {
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(118);

        var csMock = new Mock<IAnalyzer>();
        csMock.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
        csMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding { Rule = "CS001", Message = "Large Class", Severity = Severity.Warning }]);

        var dbMock = new Mock<IAnalyzer>();
        dbMock.SetupGet(a => a.Type).Returns(AnalyzerType.Database);
        dbMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding { Rule = "DB01", Message = "Missing PK", Severity = Severity.Warning }]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [csMock.Object, dbMock.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.True(success);
        analysisRepo.Verify(x => x.CompleteAsync(
            analysis.Id,
            token,
            It.Is<IReadOnlyList<AnalysisResult>>(r =>
                r.Count == 2 &&
                r.Any(x => x.Rule == "CS001") &&
                r.Any(x => x.Rule == "DB01") &&
                !r.Any(x => x.Rule == DatabaseAnalyzerRules.ContainerTimeout ||
                            x.Rule == DatabaseAnalyzerRules.ContainerError ||
                            x.Rule == DatabaseAnalyzerRules.InternalAnalyzerError)),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_Test9_WhenDatabaseAnalyzerReturnsDB01AlongsideContainerError_SoloAnalyzer_MarksAnalysisFailed()
    {
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(119);

        var dbMock = new Mock<IAnalyzer>();
        dbMock.SetupGet(a => a.Type).Returns(AnalyzerType.Database);
        dbMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new AnalyzerFinding { Rule = "DB01", Message = "Missing PK", Severity = Severity.Warning },
                new AnalyzerFinding { Rule = DatabaseAnalyzerRules.ContainerError, Message = "Container execution failed", Severity = Severity.Warning }
            ]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [dbMock.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(
            analysis.Id,
            token,
            It.Is<string?>(s => s != null && s.Contains("Все анализаторы завершились ошибкой")),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_Test10_WhenDatabaseAnalyzerReturnsDB01AlongsideContainerError_WithOtherAnalyzer_CompletesWithAllFindings()
    {
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(120);

        var csMock = new Mock<IAnalyzer>();
        csMock.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
        csMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding { Rule = "CS001", Message = "Large Class", Severity = Severity.Warning }]);

        var dbMock = new Mock<IAnalyzer>();
        dbMock.SetupGet(a => a.Type).Returns(AnalyzerType.Database);
        dbMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new AnalyzerFinding { Rule = "DB01", Message = "Missing PK", Severity = Severity.Warning },
                new AnalyzerFinding { Rule = DatabaseAnalyzerRules.ContainerError, Message = "Container execution failed", Severity = Severity.Warning }
            ]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [csMock.Object, dbMock.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.True(success);
        analysisRepo.Verify(x => x.CompleteAsync(
            analysis.Id,
            token,
            It.Is<IReadOnlyList<AnalysisResult>>(r =>
                r.Count == 3 &&
                r.Any(x => x.Rule == "CS001" && x.AnalyzerType == AnalyzerType.CSharp) &&
                r.Any(x => x.Rule == "DB01" && x.AnalyzerType == AnalyzerType.Database) &&
                r.Any(x => x.Rule == DatabaseAnalyzerRules.ContainerError && x.AnalyzerType == AnalyzerType.Database)),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_Test11_WhenDatabaseAnalyzerImplementsIDatabaseAnalyzer_PartialSuccess_CountsAsSuccessful()
    {
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(121);

        var dbMock = new Mock<IDatabaseAnalyzer>();
        dbMock.SetupGet(a => a.Type).Returns(AnalyzerType.Database);
        dbMock.SetupGet(a => a.ExecutionStatus).Returns(DatabaseAnalysisExecutionStatus.PartialSuccess);
        dbMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new AnalyzerFinding { Rule = "DB01", Message = "Primary Key Required", Severity = Severity.Warning },
                new AnalyzerFinding { Rule = DatabaseAnalyzerRules.RoslynWarning, Message = "Partial metadata warning", Severity = Severity.Info }
            ]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [dbMock.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.True(success);
        analysisRepo.Verify(x => x.CompleteAsync(
            analysis.Id,
            token,
            It.Is<IReadOnlyList<AnalysisResult>>(r =>
                r.Count == 2 &&
                r.Any(x => x.Rule == "DB01") &&
                r.Any(x => x.Rule == DatabaseAnalyzerRules.RoslynWarning)),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_Test12_WhenDatabaseAnalyzerImplementsIDatabaseAnalyzer_ContainerError_CountsAsFailed()
    {
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(122);

        var dbMock = new Mock<IDatabaseAnalyzer>();
        dbMock.SetupGet(a => a.Type).Returns(AnalyzerType.Database);
        dbMock.SetupGet(a => a.ExecutionStatus).Returns(DatabaseAnalysisExecutionStatus.ContainerError);
        dbMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new AnalyzerFinding { Rule = "DB01", Message = "Primary Key Required", Severity = Severity.Warning },
                new AnalyzerFinding { Rule = DatabaseAnalyzerRules.ContainerError, Message = "Container crashed", Severity = Severity.Warning }
            ]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [dbMock.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(
            analysis.Id,
            token,
            It.Is<string?>(s => s != null && s.Contains("Все анализаторы завершились ошибкой")),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_Test13_WhenAnalyzersPassedAsSinglePassEnumerable_ExecutesSafelyWithoutMultipleEnumeration()
    {
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(123);

        var csMock = new Mock<IAnalyzer>();
        csMock.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
        csMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding { Rule = "CS001", Message = "Large Class", Severity = Severity.Warning }]);

        static IEnumerable<IAnalyzer> CreateSinglePassSequence(IAnalyzer item)
        {
            var enumerated = 0;
            if (Interlocked.Increment(ref enumerated) > 1)
            {
                throw new InvalidOperationException("Single-pass sequence was enumerated more than once!");
            }

            yield return item;
        }

        var service = new AnalysisService(
            analysisRepo.Object,
            commitRepo.Object,
            sourceService.Object,
            CreateSinglePassSequence(csMock.Object),
            credentialService.Object,
            new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.True(success);
        analysisRepo.Verify(x => x.CompleteAsync(
            analysis.Id,
            token,
            It.Is<IReadOnlyList<AnalysisResult>>(r => r.Count == 1 && r[0].Rule == "CS001"),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TD002_Test14_WhenDatabaseAnalyzerReturnsMissingDbContext_AsSoloAnalyzer_MarksAnalysisFailed()
    {
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(124);

        var dbMock = new Mock<IDatabaseAnalyzer>();
        dbMock.SetupGet(a => a.Type).Returns(AnalyzerType.Database);
        dbMock.SetupGet(a => a.ExecutionStatus).Returns(DatabaseAnalysisExecutionStatus.InformationSkipped);
        dbMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding
            {
                Rule = DatabaseAnalyzerRules.MissingDbContext,
                Message = "В проекте не найдено контекстов данных Entity Framework Core (DbContext). Проверки схемы базы данных пропущены.",
                Severity = Severity.Info
            }]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [dbMock.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(
            analysis.Id,
            token,
            It.Is<string?>(s => s != null && s.Contains("Все анализаторы завершились ошибкой")),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_Test15_WhenLeaseLostDuringDatabaseAnalyzerExecution_ReturnsFalseWithoutCallingFailAsyncOrCompleteAsync_AndDoesNotConvertToWarningOrTimeout()
    {
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(125);

        var guardMock = new Mock<IAnalysisLeaseGuard>();
        guardMock.SetupGet(g => g.IsAcquired).Returns(true);
        var validationCount = 0;
        guardMock.Setup(g => g.ValidateOwnershipAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                validationCount++;
                // Начальная проверка владения успешна, при последующих (после потери аренды) возвращает false
                return validationCount == 1;
            });

        var guardProviderMock = new Mock<IAnalysisLeaseGuardProvider>();
        guardProviderMock.Setup(p => p.AcquireGuardAsync(125, token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(guardMock.Object);

        var dbMock = new Mock<IDatabaseAnalyzer>();
        dbMock.SetupGet(a => a.Type).Returns(AnalyzerType.Database);
        dbMock.SetupGet(a => a.ExecutionStatus).Returns(DatabaseAnalysisExecutionStatus.NotStarted);
        dbMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("Workspace lease lost during database execution."));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [dbMock.Object], credentialService.Object, guardProviderMock.Object);

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.False(success);
        // Не перезаписываем чужую задачу при потере аренды
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepo.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_Test16_RealDatabaseAnalyzer_ResetsStatus_AndExecutesCorrectlyThroughAnalysisService()
    {
        var tempRepoPath = Path.Combine(Path.GetTempPath(), "NetScope_RealDbTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRepoPath);
        try
        {
            var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(126);
            sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 126, token, "ghp_test_token", It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(tempRepoPath);

            var mockExtractor = new Mock<DatabaseAnalyzer.Roslyn.IRoslynMetadataExtractor>();

            // Создаём реальный экземпляр DatabaseAnalyzer
            var realAnalyzer = new DatabaseAnalyzer.DatabaseAnalyzer(mockExtractor.Object);

            // Проверяем начальное состояние до запуска
            Assert.Equal(DatabaseAnalysisExecutionStatus.NotStarted, realAnalyzer.ExecutionStatus);

            // Настройка экстрактора: нет контекстов данных
            mockExtractor
                .Setup(x => x.ExtractAsync(tempRepoPath, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DatabaseAnalyzer.Roslyn.RoslynModelExtractionResult());

            var csMock = new Mock<IAnalyzer>();
            csMock.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
            csMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([new AnalyzerFinding { Rule = "CS001", Message = "Large Class", Severity = Severity.Warning }]);

            var service = new AnalysisService(
                analysisRepo.Object,
                commitRepo.Object,
                sourceService.Object,
                [csMock.Object, realAnalyzer],
                credentialService.Object,
                new InMemoryAnalysisLeaseGuardProvider());

            var success = await service.ExecuteAnalysisAsync(analysis.Id, token);

            Assert.True(success);
            Assert.Equal(DatabaseAnalysisExecutionStatus.InformationSkipped, realAnalyzer.ExecutionStatus);
            analysisRepo.Verify(x => x.CompleteAsync(
                analysis.Id,
                token,
                It.Is<IReadOnlyList<AnalysisResult>>(r =>
                    r.Count == 2 &&
                    r.Any(x => x.Rule == "CS001" && x.AnalyzerType == AnalyzerType.CSharp) &&
                    r.Any(x => x.Rule == DatabaseAnalyzerRules.MissingDbContext && x.Severity == Severity.Info && x.AnalyzerType == AnalyzerType.Database)),
                It.IsAny<IAnalysisLeaseGuard>(),
                It.IsAny<CancellationToken>()), Times.Once);
            analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);

            // Повторный запуск того же экземпляра realAnalyzer при ошибке извлечения:
            // статус должен сброситься и установиться в ExtractionError
            mockExtractor
                .Setup(x => x.ExtractAsync(tempRepoPath, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Extraction crashed"));

            var (analysis2, token2, analysisRepo2, commitRepo2, sourceService2, credentialService2) = CreateAnalysisPipelineContext(127);
            sourceService2.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 127, token2, "ghp_test_token", It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(tempRepoPath);

            var service2 = new AnalysisService(
                analysisRepo2.Object,
                commitRepo2.Object,
                sourceService2.Object,
                [realAnalyzer],
                credentialService2.Object,
                new InMemoryAnalysisLeaseGuardProvider());

            var success2 = await service2.ExecuteAnalysisAsync(analysis2.Id, token2);

            Assert.False(success2);
            Assert.Equal(DatabaseAnalysisExecutionStatus.ExtractionError, realAnalyzer.ExecutionStatus);
            analysisRepo2.Verify(x => x.FailAsync(analysis2.Id, token2, It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
            analysisRepo2.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            if (Directory.Exists(tempRepoPath))
            {
                Directory.Delete(tempRepoPath, recursive: true);
            }
        }
    }

    #region TD-002: Mandatory Scenarios A-G

    [Fact]
    public async Task TD002_ScenarioA_DatabaseAnalyzerSoloInformationSkipped_MarksAnalysisFailed()
    {
        // Сценарий A: DatabaseAnalyzer один в конвейере возвращает InformationSkipped (отсутствие DbContext)
        // -> анализ завершается Failed (не было полезной работы), CompleteAsync НЕ вызывается, вызывается FailAsync.
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(201);

        var dbMock = new Mock<IDatabaseAnalyzer>();
        dbMock.SetupGet(a => a.Type).Returns(AnalyzerType.Database);
        dbMock.SetupGet(a => a.ExecutionStatus).Returns(DatabaseAnalysisExecutionStatus.InformationSkipped);
        dbMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding
            {
                Rule = DatabaseAnalyzerRules.MissingDbContext,
                Message = "В проекте не найдено контекстов данных Entity Framework Core (DbContext).",
                Severity = Severity.Info
            }]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [dbMock.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(
            analysis.Id,
            token,
            It.Is<string?>(s => s != null && s.Contains("Все анализаторы завершились ошибкой")),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_ScenarioB_DatabaseAnalyzerInformationSkipped_WithSuccessfulCodeAnalyzer_CompletesSuccessfullyWithBothResults()
    {
        // Сценарий B: DatabaseAnalyzer возвращает InformationSkipped, а CodeAnalyzer успешно находит замечания
        // -> анализ завершается Completed, вызывается CompleteAsync, сохраняются результаты обоих анализаторов (включая Info-запись DbContext).
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(202);

        var csMock = new Mock<IAnalyzer>();
        csMock.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
        csMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding { Rule = "CS001", Message = "Large Class", Severity = Severity.Warning }]);

        var dbMock = new Mock<IDatabaseAnalyzer>();
        dbMock.SetupGet(a => a.Type).Returns(AnalyzerType.Database);
        dbMock.SetupGet(a => a.ExecutionStatus).Returns(DatabaseAnalysisExecutionStatus.InformationSkipped);
        dbMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding
            {
                Rule = DatabaseAnalyzerRules.MissingDbContext,
                Message = "В проекте не найдено контекстов данных Entity Framework Core (DbContext).",
                Severity = Severity.Info
            }]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [csMock.Object, dbMock.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.True(success);
        analysisRepo.Verify(x => x.CompleteAsync(
            analysis.Id,
            token,
            It.Is<IReadOnlyList<AnalysisResult>>(r =>
                r.Count == 2 &&
                r.Any(x => x.Rule == "CS001" && x.AnalyzerType == AnalyzerType.CSharp) &&
                r.Any(x => x.Rule == DatabaseAnalyzerRules.MissingDbContext && x.Severity == Severity.Info && x.AnalyzerType == AnalyzerType.Database)),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_ScenarioC_DatabaseAnalyzerContainerErrorOrException_WithSuccessfulCodeAnalyzer_CompletesWithWarningAndPreservesResults()
    {
        // Сценарий C: DatabaseAnalyzer завершается с ошибкой (ContainerError или выбрасывает исключение), а CodeAnalyzer успешен
        // -> анализ Completed, вызывается CompleteAsync, сохраняются замечания CodeAnalyzer + Warning о сбое контейнера (с исходным сообщением об ошибке контейнера).
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(203);

        var csMock = new Mock<IAnalyzer>();
        csMock.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
        csMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding { Rule = "CS001", Message = "Large Class", Severity = Severity.Warning }]);

        const string containerErrorMessage = "Сбой контейнера: non-zero exit code 137 (OOMKilled)";
        var dbMock = new Mock<IDatabaseAnalyzer>();
        dbMock.SetupGet(a => a.Type).Returns(AnalyzerType.Database);
        dbMock.SetupGet(a => a.ExecutionStatus).Returns(DatabaseAnalysisExecutionStatus.ContainerError);
        dbMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding
            {
                Rule = DatabaseAnalyzerRules.ContainerError,
                Message = containerErrorMessage,
                Severity = Severity.Warning
            }]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [csMock.Object, dbMock.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.True(success);
        analysisRepo.Verify(x => x.CompleteAsync(
            analysis.Id,
            token,
            It.Is<IReadOnlyList<AnalysisResult>>(r =>
                r.Count == 2 &&
                r.Any(x => x.Rule == "CS001" && x.AnalyzerType == AnalyzerType.CSharp) &&
                r.Any(x => x.Rule == DatabaseAnalyzerRules.ContainerError &&
                           x.Severity == Severity.Warning &&
                           x.Message.Contains(containerErrorMessage) &&
                           x.AnalyzerType == AnalyzerType.Database)),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_ScenarioD_DatabaseAnalyzerContainerTimeout_WithSuccessfulCodeAnalyzer_CompletesWithWarningAndPreservesResults()
    {
        // Сценарий D: DatabaseAnalyzer завершается по таймауту контейнера (ContainerTimeout), а CodeAnalyzer успешен
        // -> анализ Completed, сохраняются замечания CodeAnalyzer + Warning о таймауте контейнера.
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(204);

        var csMock = new Mock<IAnalyzer>();
        csMock.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
        csMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding { Rule = "CS001", Message = "Large Class", Severity = Severity.Warning }]);

        var dbMock = new Mock<IDatabaseAnalyzer>();
        dbMock.SetupGet(a => a.Type).Returns(AnalyzerType.Database);
        dbMock.SetupGet(a => a.ExecutionStatus).Returns(DatabaseAnalysisExecutionStatus.ContainerTimeout);
        dbMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding
            {
                Rule = DatabaseAnalyzerRules.ContainerTimeout,
                Message = "Превышено максимальное время выполнения изолированного анализатора базы данных.",
                Severity = Severity.Warning
            }]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [csMock.Object, dbMock.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.True(success);
        analysisRepo.Verify(x => x.CompleteAsync(
            analysis.Id,
            token,
            It.Is<IReadOnlyList<AnalysisResult>>(r =>
                r.Count == 2 &&
                r.Any(x => x.Rule == "CS001" && x.AnalyzerType == AnalyzerType.CSharp) &&
                r.Any(x => x.Rule == DatabaseAnalyzerRules.ContainerTimeout &&
                           x.Severity == Severity.Warning &&
                           x.AnalyzerType == AnalyzerType.Database)),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_ScenarioE_ThirdPartyOperationCanceledException_WithoutLeaseLoss_MarksAnalysisFailed()
    {
        // Сценарий E: Сторонняя отмена (сторонний OperationCanceledException без отмены cancellationToken и без потери lease) в анализаторе
        // -> анализ помечается Failed (вызывается FailAsync), задача НЕ зависает в Running.
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(205);

        var failingAnalyzer = new Mock<IAnalyzer>();
        failingAnalyzer.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
        failingAnalyzer.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("Third-party internal timeout/cancellation."));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [failingAnalyzer.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(
            analysis.Id,
            token,
            It.Is<string?>(s => s != null && s.Contains("Операция анализа была отменена")),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_ScenarioF_CallerCancellation_RethrowsAndNeverCallsFailAsyncOrCompleteAsync()
    {
        // Сценарий F: Вызывающая отмена (cancellationToken.IsCancellationRequested)
        // -> пробрасывается исключение, FailAsync НЕ вызывается, статус задачи в БД НЕ меняется (не помечается Failed).
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(206);

        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteAnalysisAsync(analysis.Id, token, callerCts.Token));

        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepo.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_ScenarioG_ConfirmedLeaseLoss_ReturnsFalseWithoutCallingFailAsyncOrCompleteAsync()
    {
        // Сценарий G: Потеря владения арендой (lease loss подтверждён)
        // -> сервис возвращает false, FailAsync НЕ вызывается, статус задачи в БД НЕ перезаписывается.
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(207);

        var guardMock = new Mock<IAnalysisLeaseGuard>();
        guardMock.SetupGet(g => g.IsAcquired).Returns(true);
        var validationCount = 0;
        guardMock.Setup(g => g.ValidateOwnershipAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                validationCount++;
                // Первая проверка владения (начальная) успешна, последующие проверки - аренда потеряна
                return validationCount == 1;
            });

        var guardProviderMock = new Mock<IAnalysisLeaseGuardProvider>();
        guardProviderMock.Setup(p => p.AcquireGuardAsync(207, token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(guardMock.Object);

        var dbMock = new Mock<IDatabaseAnalyzer>();
        dbMock.SetupGet(a => a.Type).Returns(AnalyzerType.Database);
        dbMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("Lease expired"));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [dbMock.Object], credentialService.Object, guardProviderMock.Object);

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepo.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region TD-002: Specific Lease Renewal Lifecycle and Ownership Validation Tests

    [Fact]
    public async Task TD002_LeaseRenewal_WhenRenewLeaseFails_WhileOwnershipStillConfirmed_MarksTaskFailed()
    {
        // Ошибка RenewLeaseAsync при сохранении владения (ValidateOwnershipAsync == true):
        // не классифицируется как потеря владения другому узлу, помечается Failed, не зависает в Running
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(208);

        var guardMock = new Mock<IAnalysisLeaseGuard>();
        guardMock.SetupGet(g => g.IsAcquired).Returns(true);
        guardMock.Setup(g => g.ValidateOwnershipAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true); // Владение подтверждено!

        var guardProviderMock = new Mock<IAnalysisLeaseGuardProvider>();
        guardProviderMock.Setup(p => p.AcquireGuardAsync(208, token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(guardMock.Object);

        // RenewLeaseAsync выбрасывает исключение (сбой продления аренды в БД из-за таймаута сети)
        analysisRepo.Setup(x => x.RenewLeaseAsync(208, token, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("PostgreSQL renewal timeout"));

        var csMock = new Mock<IAnalyzer>();
        csMock.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
        csMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns<AnalyzerContext, CancellationToken>(async (_, ct) =>
            {
                var tcs = new TaskCompletionSource<IReadOnlyList<AnalyzerFinding>>();
                using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
                return await tcs.Task;
            });

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [csMock.Object], credentialService.Object, guardProviderMock.Object);

        // Передаем короткую длительность аренды, чтобы фоновый цикл сработал быстро
        var success = await service.ExecuteAnalysisAsync(analysis.Id, token, default, TimeSpan.FromMilliseconds(60));

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(
            analysis.Id,
            token,
            It.Is<string?>(msg => msg != null && msg.Contains("аренд")),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_LeaseRenewal_WhenValidateOwnershipReturnsFalse_DoesNotCallFailAsyncOrCompleteAsync()
    {
        // Отрицательный результат ValidateOwnershipAsync в цикле renewal (подтверждённая потеря владения):
        // ни CompleteAsync, ни FailAsync не вызываются, очистка каталога не производится
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(209);

        var guardMock = new Mock<IAnalysisLeaseGuard>();
        guardMock.SetupGet(g => g.IsAcquired).Returns(true);
        var validationCount = 0;
        guardMock.Setup(g => g.ValidateOwnershipAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                validationCount++;
                return validationCount == 1; // 1-й вызов (начальный) true, в цикле renewal false
            });

        var guardProviderMock = new Mock<IAnalysisLeaseGuardProvider>();
        guardProviderMock.Setup(p => p.AcquireGuardAsync(209, token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(guardMock.Object);

        var csMock = new Mock<IAnalyzer>();
        csMock.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
        csMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns<AnalyzerContext, CancellationToken>(async (_, ct) =>
            {
                var tcs = new TaskCompletionSource<IReadOnlyList<AnalyzerFinding>>();
                using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
                return await tcs.Task;
            });

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [csMock.Object], credentialService.Object, guardProviderMock.Object);

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token, default, TimeSpan.FromMilliseconds(60));

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepo.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        sourceService.Verify(x => x.CleanupAsync(209, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_LeaseRenewal_WhenValidateOwnershipThrowsException_DoesNotCallFailAsyncOrCompleteAsync()
    {
        // Исключение при проверке владения в цикле renewal (состояние владения неизвестно):
        // выполнение прекращается, операции CompleteAsync, FailAsync и CleanupAsync запрещены
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(210);

        var guardMock = new Mock<IAnalysisLeaseGuard>();
        guardMock.SetupGet(g => g.IsAcquired).Returns(true);
        var validationCount = 0;
        guardMock.Setup(g => g.ValidateOwnershipAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .Returns<int[], CancellationToken>((_, _) =>
            {
                validationCount++;
                if (validationCount == 1)
                {
                    return Task.FromResult(true);
                }

                throw new InvalidOperationException("PostgreSQL connection abruptly terminated");
            });

        var guardProviderMock = new Mock<IAnalysisLeaseGuardProvider>();
        guardProviderMock.Setup(p => p.AcquireGuardAsync(210, token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(guardMock.Object);

        var csMock = new Mock<IAnalyzer>();
        csMock.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
        csMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns<AnalyzerContext, CancellationToken>(async (_, ct) =>
            {
                var tcs = new TaskCompletionSource<IReadOnlyList<AnalyzerFinding>>();
                using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
                return await tcs.Task;
            });

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [csMock.Object], credentialService.Object, guardProviderMock.Object);

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token, default, TimeSpan.FromMilliseconds(60));

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepo.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        sourceService.Verify(x => x.CleanupAsync(210, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_LeaseRenewal_WhenExternalCancellationRequested_RethrowsAndNeverCallsFailAsync()
    {
        // Внешняя отмена (cancellationToken.IsCancellationRequested):
        // пробрасывает исключение, FailAsync НЕ вызывается
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(211);

        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExecuteAnalysisAsync(analysis.Id, token, callerCts.Token));

        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepo.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_LeaseRenewal_WhenExternalCancellationTriggeredDuringExecution_RethrowsAndNeverCallsFailAsyncOrCompleteAsync()
    {
        // Внешняя отмена непосредственно во время выполнения анализа (после старта анализатора):
        // 1. Создать действующий CancellationTokenSource.
        // 2. Запустить AnalysisService.ExecuteAnalysisAsync.
        // 3. Использовать TaskCompletionSource, чтобы тест точно знал, что анализатор начал выполнение.
        // 4. Дождаться сигнала начала выполнения анализатора.
        // 5. Вызвать Cancel() у внешнего CancellationTokenSource.
        // 6. Дождаться завершения ExecuteAnalysisAsync.
        // 7. Проверить получение OperationCanceledException.
        // 8. Проверить, что FailAsync не вызывался.
        // 9. Проверить, что CompleteAsync не вызывался.
        // 10. Проверить корректное завершение фонового Lease Renewal.
        // 11. Проверить безопасное поведение CleanupAsync с учётом подтверждённого владения.
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(213);

        var guardMock = new Mock<IAnalysisLeaseGuard>();
        guardMock.SetupGet(g => g.IsAcquired).Returns(true);
        guardMock.Setup(g => g.ValidateOwnershipAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true); // Владение подтверждено

        var guardProviderMock = new Mock<IAnalysisLeaseGuardProvider>();
        guardProviderMock.Setup(p => p.AcquireGuardAsync(213, token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(guardMock.Object);

        using var externalCts = new CancellationTokenSource();
        var analyzerStartedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var dbMock = new Mock<IDatabaseAnalyzer>();
        dbMock.SetupGet(a => a.Type).Returns(AnalyzerType.Database);
        dbMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns<AnalyzerContext, CancellationToken>(async (_, ct) =>
            {
                // Сигнализируем тесту, что анализатор начал выполнение
                analyzerStartedTcs.TrySetResult(true);

                // Ожидаем отмены токена ct (который связан с externalCts)
                var tcs = new TaskCompletionSource<IReadOnlyList<AnalyzerFinding>>();
                using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
                return await tcs.Task;
            });

        var service = new AnalysisService(
            analysisRepo.Object,
            commitRepo.Object,
            sourceService.Object,
            [dbMock.Object],
            credentialService.Object,
            guardProviderMock.Object);

        // 1-2. Запускаем выполнение анализа с активным (не отменённым) токеном
        var executionTask = service.ExecuteAnalysisAsync(analysis.Id, token, externalCts.Token);

        // 3-4. Детерминированно ожидаем подтверждения, что анализатор начал работу
        await analyzerStartedTcs.Task;

        // 5. Отменяем внешний токен во время активной работы анализатора
        externalCts.Cancel();

        // 6-7. Дожидаемся завершения и проверяем получение OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executionTask);

        // 8. Проверяем, что FailAsync не вызывался
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);

        // 9. Проверяем, что CompleteAsync не вызывался
        analysisRepo.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);

        // 10-11. Проверяем безопасное выполнение CleanupAsync, поскольку владение было подтверждено
        sourceService.Verify(x => x.CleanupAsync(213, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TD002_LeaseRenewal_NormalStopAfterSuccessfulAnalysis_CompletesSuccessfullyWithoutRenewalErrors()
    {
        // Штатная остановка renewal после успешного завершения анализа:
        // CompleteAsync вызывается, FailAsync не вызывается
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(212);

        var csMock = new Mock<IAnalyzer>();
        csMock.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
        csMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding { Rule = "CS001", Message = "Large Class", Severity = Severity.Warning }]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [csMock.Object], credentialService.Object, new InMemoryAnalysisLeaseGuardProvider());

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.True(success);
        analysisRepo.Verify(x => x.CompleteAsync(analysis.Id, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        sourceService.Verify(x => x.CleanupAsync(212, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TD002_LeaseRenewal_RenewalExceptionDuringAnalyzerThatIgnoresCancellation_DoesNotCompleteSuccessfully()
    {
        // Тест 1: RenewalException во время работы анализатора, игнорирующего отмену:
        // не маскируется успешным результатом анализатора, CompleteAsync не вызывается, помечается Failed
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(214);

        var guardMock = new Mock<IAnalysisLeaseGuard>();
        guardMock.SetupGet(g => g.IsAcquired).Returns(true);
        guardMock.Setup(g => g.ValidateOwnershipAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true); // Владение подтверждено

        var guardProviderMock = new Mock<IAnalysisLeaseGuardProvider>();
        guardProviderMock.Setup(p => p.AcquireGuardAsync(214, token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(guardMock.Object);

        var analyzerStartedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalFailedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        analysisRepo.Setup(x => x.RenewLeaseAsync(214, token, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<int, Guid, TimeSpan, CancellationToken>((_, _, _, _) =>
            {
                renewalFailedTcs.TrySetResult(true);
                throw new InvalidOperationException("PostgreSQL renewal timeout during test");
            });

        var csMock = new Mock<IAnalyzer>();
        csMock.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
        csMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns<AnalyzerContext, CancellationToken>(async (_, ct) =>
            {
                analyzerStartedTcs.TrySetResult(true);
                await renewalFailedTcs.Task;
                await Task.Yield();
                // Игнорируем отмену и возвращаем полезный результат
                return [new AnalyzerFinding { Rule = "CS001", Message = "Some finding", Severity = Severity.Warning }];
            });

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [csMock.Object], credentialService.Object, guardProviderMock.Object);

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token, default, TimeSpan.FromMilliseconds(20));

        Assert.False(success);
        analysisRepo.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepo.Verify(x => x.FailAsync(
            analysis.Id,
            token,
            It.Is<string?>(s => s != null && s.Contains("аренд")),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        sourceService.Verify(x => x.CleanupAsync(214, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TD002_LeaseRenewal_RenewalExceptionDuringDatabaseAnalyzer_DoesNotBecomeInternalAnalyzerWarning()
    {
        // Тест 2: RenewalException во время работы DatabaseAnalyzer, который затем выбрасывает исключение:
        // критическая ошибка Lease Renewal не превращается во внутреннее предупреждение анализатора
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(215);

        var guardMock = new Mock<IAnalysisLeaseGuard>();
        guardMock.SetupGet(g => g.IsAcquired).Returns(true);
        guardMock.Setup(g => g.ValidateOwnershipAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var guardProviderMock = new Mock<IAnalysisLeaseGuardProvider>();
        guardProviderMock.Setup(p => p.AcquireGuardAsync(215, token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(guardMock.Object);

        var dbStartedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalFailedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        analysisRepo.Setup(x => x.RenewLeaseAsync(215, token, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<int, Guid, TimeSpan, CancellationToken>((_, _, _, _) =>
            {
                renewalFailedTcs.TrySetResult(true);
                throw new InvalidOperationException("Database renewal failure");
            });

        var dbMock = new Mock<IDatabaseAnalyzer>();
        dbMock.SetupGet(a => a.Type).Returns(AnalyzerType.Database);
        dbMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns<AnalyzerContext, CancellationToken>(async (_, ct) =>
            {
                dbStartedTcs.TrySetResult(true);
                await renewalFailedTcs.Task;
                await Task.Yield();
                throw new InvalidOperationException("Container crashed inside DatabaseAnalyzer");
            });

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [dbMock.Object], credentialService.Object, guardProviderMock.Object);

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token, default, TimeSpan.FromMilliseconds(20));

        Assert.False(success);
        analysisRepo.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepo.Verify(x => x.FailAsync(
            analysis.Id,
            token,
            It.Is<string?>(s => s != null && s.Contains("аренд")),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        sourceService.Verify(x => x.CleanupAsync(215, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TD002_LeaseRenewal_RenewalExceptionDuringAnalyzerReturningEmptyResults_DoesNotBecomeNormalStop()
    {
        // Тест 3: RenewalException при анализаторе, возвращающем пустой список (usefulWorkCompleted остаётся false):
        // не перезаписывается значением NormalStop, сообщение указывает на сбой аренды
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(216);

        var guardMock = new Mock<IAnalysisLeaseGuard>();
        guardMock.SetupGet(g => g.IsAcquired).Returns(true);
        guardMock.Setup(g => g.ValidateOwnershipAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var guardProviderMock = new Mock<IAnalysisLeaseGuardProvider>();
        guardProviderMock.Setup(p => p.AcquireGuardAsync(216, token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(guardMock.Object);

        var analyzerStartedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalFailedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        analysisRepo.Setup(x => x.RenewLeaseAsync(216, token, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<int, Guid, TimeSpan, CancellationToken>((_, _, _, _) =>
            {
                renewalFailedTcs.TrySetResult(true);
                throw new InvalidOperationException("DB lease renewal error");
            });

        var dbMock = new Mock<IDatabaseAnalyzer>();
        dbMock.SetupGet(a => a.Type).Returns(AnalyzerType.Database);
        dbMock.SetupGet(a => a.ExecutionStatus).Returns(DatabaseAnalysisExecutionStatus.InformationSkipped);
        dbMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns<AnalyzerContext, CancellationToken>(async (_, ct) =>
            {
                analyzerStartedTcs.TrySetResult(true);
                await renewalFailedTcs.Task;
                await Task.Yield();
                return [];
            });

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [dbMock.Object], credentialService.Object, guardProviderMock.Object);

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token, default, TimeSpan.FromMilliseconds(20));

        Assert.False(success);
        analysisRepo.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepo.Verify(x => x.FailAsync(
            analysis.Id,
            token,
            It.Is<string?>(s => s != null && s.Contains("аренд") && !s.Contains("Все анализаторы")),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        sourceService.Verify(x => x.CleanupAsync(216, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TD002_LeaseRenewal_NormalStop_DoesNotOverwriteTerminalStopReason()
    {
        // Тест 4: Нормальная остановка (NormalStop) не перезаписывает уже установленную терминальную причину (RenewalException):
        // наблюдаемое поведение конвейера завершается с ошибкой аренды, CompleteAsync не вызывается
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(217);

        var guardMock = new Mock<IAnalysisLeaseGuard>();
        guardMock.SetupGet(g => g.IsAcquired).Returns(true);
        guardMock.Setup(g => g.ValidateOwnershipAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var guardProviderMock = new Mock<IAnalysisLeaseGuardProvider>();
        guardProviderMock.Setup(p => p.AcquireGuardAsync(217, token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(guardMock.Object);

        var analyzerStartedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalFailedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        analysisRepo.Setup(x => x.RenewLeaseAsync(217, token, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<int, Guid, TimeSpan, CancellationToken>((_, _, _, _) =>
            {
                renewalFailedTcs.TrySetResult(true);
                throw new InvalidOperationException("Terminal lease failure during execution");
            });

        var csMock = new Mock<IAnalyzer>();
        csMock.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
        csMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns<AnalyzerContext, CancellationToken>(async (_, ct) =>
            {
                analyzerStartedTcs.TrySetResult(true);
                await renewalFailedTcs.Task;
                await Task.Yield();
                return [new AnalyzerFinding { Rule = "SEC001", Message = "Security check passed", Severity = Severity.Warning }];
            });

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [csMock.Object], credentialService.Object, guardProviderMock.Object);

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token, default, TimeSpan.FromMilliseconds(20));

        Assert.False(success);
        analysisRepo.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepo.Verify(x => x.FailAsync(
            analysis.Id,
            token,
            It.Is<string?>(s => s != null && s.Contains("аренд")),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        sourceService.Verify(x => x.CleanupAsync(217, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TD002_LeaseRenewal_RenewalException_WhenOwnershipCannotBeConfirmed_DoesNotCallFailCompleteOrCleanup()
    {
        // Тест 5: Ошибка RenewalException и неподтверждённое владение:
        // FailAsync, CompleteAsync и CleanupAsync не вызываются, возвращается false
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(218);

        var guardMock = new Mock<IAnalysisLeaseGuard>();
        guardMock.SetupGet(g => g.IsAcquired).Returns(true);
        var validationCount = 0;
        guardMock.Setup(g => g.ValidateOwnershipAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                validationCount++;
                return validationCount == 1; // 1-й вызов true, последующие false
            });

        var guardProviderMock = new Mock<IAnalysisLeaseGuardProvider>();
        guardProviderMock.Setup(p => p.AcquireGuardAsync(218, token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(guardMock.Object);

        analysisRepo.Setup(x => x.RenewLeaseAsync(218, token, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("PostgreSQL renewal socket closed"));

        var csMock = new Mock<IAnalyzer>();
        csMock.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
        csMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns<AnalyzerContext, CancellationToken>(async (_, ct) =>
            {
                var tcs = new TaskCompletionSource<IReadOnlyList<AnalyzerFinding>>();
                using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
                return await tcs.Task;
            });

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [csMock.Object], credentialService.Object, guardProviderMock.Object);

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token, default, TimeSpan.FromMilliseconds(20));

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        analysisRepo.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        sourceService.Verify(x => x.CleanupAsync(218, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TD002_LeaseRenewal_RenewalException_WhenOwnershipConfirmed_MarksTaskFailedWithoutCompletion()
    {
        // Тест 6: Ошибка RenewalException при подтверждённом владении:
        // FailAsync вызывается с ошибкой аренды, CompleteAsync не вызывается, CleanupAsync выполняется
        var (analysis, token, analysisRepo, commitRepo, sourceService, credentialService) = CreateAnalysisPipelineContext(219);

        var guardMock = new Mock<IAnalysisLeaseGuard>();
        guardMock.SetupGet(g => g.IsAcquired).Returns(true);
        guardMock.Setup(g => g.ValidateOwnershipAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var guardProviderMock = new Mock<IAnalysisLeaseGuardProvider>();
        guardProviderMock.Setup(p => p.AcquireGuardAsync(219, token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(guardMock.Object);

        analysisRepo.Setup(x => x.RenewLeaseAsync(219, token, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("PostgreSQL renewal timeout"));

        var csMock = new Mock<IAnalyzer>();
        csMock.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
        csMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns<AnalyzerContext, CancellationToken>(async (_, ct) =>
            {
                var tcs = new TaskCompletionSource<IReadOnlyList<AnalyzerFinding>>();
                using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
                return await tcs.Task;
            });

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [csMock.Object], credentialService.Object, guardProviderMock.Object);

        var success = await service.ExecuteAnalysisAsync(analysis.Id, token, default, TimeSpan.FromMilliseconds(20));

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(
            analysis.Id,
            token,
            It.Is<string?>(s => s != null && s.Contains("аренд")),
            It.IsAny<IAnalysisLeaseGuard>(),
            It.IsAny<CancellationToken>()), Times.Once);
        analysisRepo.Verify(x => x.CompleteAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        sourceService.Verify(x => x.CleanupAsync(219, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #endregion
}
