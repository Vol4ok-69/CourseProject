using Application.Analyzers;
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

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object]);

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

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object]);

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

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var analyzer = new Mock<IAnalyzer>();

        analysisRepo.Setup(x => x.GetByIdAsync(50, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo.Setup(x => x.CompleteAsync(50, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        sourceService.Setup(x => x.DownloadAndExtractAsync("https://github.com/test/repo", "hash123", 50, It.IsAny<CancellationToken>()))
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

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object]);

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

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var analyzer = new Mock<IAnalyzer>();

        analysisRepo.Setup(x => x.GetByIdAsync(51, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo.Setup(x => x.FailAsync(51, token, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 51, It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\TempRepo");

        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Roslyn crash"));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object]);

        var success = await service.ExecuteAnalysisAsync(51, token);

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(51, token, It.IsAny<CancellationToken>()), Times.Once);
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

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var analyzer = new Mock<IAnalyzer>();

        analysisRepo.Setup(x => x.GetByIdAsync(52, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        analysisRepo.Setup(x => x.CompleteAsync(52, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 52, It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\TempRepo");
        sourceService.Setup(x => x.CleanupAsync(52, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Directory locked by another process"));

        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object]);

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

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object]);

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
        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 77, It.IsAny<CancellationToken>()))
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
            var analyzer = new Mock<IAnalyzer>();

            analyzer.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
            analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            string? requestedRepoUrl = null;
            sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 1, It.IsAny<CancellationToken>()))
                .Callback<string, string, int, CancellationToken>((url, _, _, _) => requestedRepoUrl = url)
                .ReturnsAsync("C:\\TempExtracted");

            var service = new AnalysisService(analysisRepo, commitRepo, sourceService.Object, [analyzer.Object]);

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
                Project = new Project { RepoUrl = "https://github.com/repo" }
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var analyzer = new Mock<IAnalyzer>();

        analysisRepo.Setup(x => x.GetByIdAsync(88, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);

        using var shutdownCts = new CancellationTokenSource();

        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 88, It.IsAny<CancellationToken>()))
            .Returns<string, string, int, CancellationToken>(async (_, _, _, ct) =>
            {
                shutdownCts.Cancel(); // Имитируем остановку приложения во время скачивания
                ct.ThrowIfCancellationRequested();
                await Task.CompletedTask;
                return "C:\\Temp";
            });

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExecuteAnalysisAsync(88, token, shutdownCts.Token));

        // Гарантируем, что задача НЕ помечена как сбойная (Failed)
        analysisRepo.Verify(x => x.FailAsync(88, token, It.IsAny<CancellationToken>()), Times.Never);

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
        analysisRepo.Setup(x => x.FailAsync(91, token, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object]);

        var success = await service.ExecuteAnalysisAsync(91, token);

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(91, token, It.IsAny<CancellationToken>()), Times.Once);
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
        analysisRepo.Setup(x => x.FailAsync(92, token, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object]);

        var success = await service.ExecuteAnalysisAsync(92, token);

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(92, token, It.IsAny<CancellationToken>()), Times.Once);
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
        analysisRepo.Setup(x => x.FailAsync(93, token, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object]);

        var success = await service.ExecuteAnalysisAsync(93, token);

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(93, token, It.IsAny<CancellationToken>()), Times.Once);
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
                Project = new Project { RepoUrl = "https://github.com/repo" }
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var analyzer = new Mock<IAnalyzer>();

        analysisRepo.Setup(x => x.GetByIdAsync(94, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        // Симулируем, что продление аренды возвращает false (аренда потеряна)
        analysisRepo.Setup(x => x.RenewLeaseAsync(94, token, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 94, It.IsAny<CancellationToken>()))
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

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object]);

        // Передаем короткую длительность аренды (60ms), чтобы цикл продления сработал через 20ms
        var success = await service.ExecuteAnalysisAsync(94, token, default, TimeSpan.FromMilliseconds(60));

        Assert.False(success);
        // Не изменяем задачу чужого воркера
        analysisRepo.Verify(x => x.FailAsync(94, token, It.IsAny<CancellationToken>()), Times.Never);
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
                Project = new Project { RepoUrl = "https://github.com/repo" }
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var analyzer = new Mock<IAnalyzer>();

        analysisRepo.Setup(x => x.GetByIdAsync(95, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        // Задача была перехвачена в БД, поэтому CompleteAsync возвращает false
        analysisRepo.Setup(x => x.CompleteAsync(95, token, It.IsAny<IReadOnlyList<AnalysisResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 95, It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\Temp");
        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object]);

        var success = await service.ExecuteAnalysisAsync(95, token);

        Assert.False(success);
        // Не удаляем временные файлы, так как ими уже владеет другой воркер
        sourceService.Verify(x => x.CleanupAsync(95, It.IsAny<CancellationToken>()), Times.Never);
        analysisRepo.Verify(x => x.FailAsync(95, token, It.IsAny<CancellationToken>()), Times.Never);
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
                Project = new Project { RepoUrl = "https://github.com/repo" }
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var analyzer = new Mock<IAnalyzer>();

        analysisRepo.Setup(x => x.GetByIdAsync(96, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 96, It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\Temp");
        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Analysis error"));

        // Задача перехвачена в БД другим воркером, поэтому FailAsync возвращает false
        analysisRepo.Setup(x => x.FailAsync(96, token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object]);

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
                Project = new Project { RepoUrl = "https://github.com/repo" }
            }
        };

        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var sourceService = new Mock<IRepositorySourceService>();
        var analyzer = new Mock<IAnalyzer>();

        analysisRepo.Setup(x => x.GetByIdAsync(97, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        sourceService.Setup(x => x.DownloadAndExtractAsync(It.IsAny<string>(), It.IsAny<string>(), 97, It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\Temp");
        analyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Fatal analysis error"));

        analysisRepo.Setup(x => x.FailAsync(97, token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Исключение при очистке
        sourceService.Setup(x => x.CleanupAsync(97, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Access denied during cleanup"));

        var service = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, [analyzer.Object]);

        var success = await service.ExecuteAnalysisAsync(97, token);

        Assert.False(success);
        analysisRepo.Verify(x => x.FailAsync(97, token, It.IsAny<CancellationToken>()), Times.Once);
        sourceService.Verify(x => x.CleanupAsync(97, It.IsAny<CancellationToken>()), Times.Once);
    }
}
