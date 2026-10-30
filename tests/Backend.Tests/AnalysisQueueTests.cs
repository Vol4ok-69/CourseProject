using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Repository;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.Tests;

public sealed class AnalysisQueueTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly AnalysisRepository _repository;

    public AnalysisQueueTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new AppDbContext(options);
        _context.Database.EnsureCreated();
        _repository = new AnalysisRepository(_context);

        SeedBasicData();
    }

    private void SeedBasicData()
    {
        _context.Roles.Add(new Role { Id = 1, Name = "User" });
        _context.Users.Add(new User { Id = 1, Username = "developer", PasswordHash = "hash", RoleId = 1 });
        _context.Projects.Add(new Project { Id = 1, Name = "Repo", RepoUrl = "https://github.com/test/repo", OwnerId = 1 });
        _context.Commits.Add(new Commit { Id = 1, ProjectId = 1, CommitHash = "hash123", CommitDate = DateTime.UtcNow });
        _context.SaveChanges();
    }

    [Fact]
    public async Task TryAcquireNextPendingOrStaleAsync_WithPendingTask_AcquiresAndTransitionsToRunning()
    {
        var task = new Analysis
        {
            Id = 10,
            CommitId = 1,
            Status = AnalysisStatus.Pending,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        await _repository.AddAsync(task);

        var acquired = await _repository.TryAcquireNextPendingOrStaleAsync(TimeSpan.FromMinutes(2));

        Assert.NotNull(acquired);
        Assert.Equal(10, acquired.Id);
        Assert.Equal(AnalysisStatus.Running, acquired.Status);
        Assert.NotNull(acquired.StartedAt);
        Assert.NotNull(acquired.ExecutionToken);
        Assert.NotNull(acquired.LeaseExpiration);
        Assert.True(acquired.LeaseExpiration > DateTime.UtcNow);
    }

    [Fact]
    public async Task TryAcquireNextPendingOrStaleAsync_WithActiveLease_DoesNotAcquire()
    {
        var task = new Analysis
        {
            Id = 11,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            ExecutionToken = Guid.NewGuid(),
            LeaseExpiration = DateTime.UtcNow.AddMinutes(5) // активная аренда
        };
        await _repository.AddAsync(task);

        var acquired = await _repository.TryAcquireNextPendingOrStaleAsync(TimeSpan.FromMinutes(2));

        Assert.Null(acquired);
    }

    [Fact]
    public async Task TryAcquireNextPendingOrStaleAsync_WithExpiredLease_RecoversStaleTask()
    {
        var originalToken = Guid.NewGuid();
        var task = new Analysis
        {
            Id = 12,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            CreatedAt = DateTime.UtcNow.AddMinutes(-15),
            StartedAt = DateTime.UtcNow.AddMinutes(-10),
            ExecutionToken = originalToken,
            LeaseExpiration = DateTime.UtcNow.AddMinutes(-2) // аренда истекла (зависшая задача)
        };
        await _repository.AddAsync(task);

        var acquired = await _repository.TryAcquireNextPendingOrStaleAsync(TimeSpan.FromMinutes(3));

        Assert.NotNull(acquired);
        Assert.Equal(12, acquired.Id);
        Assert.Equal(AnalysisStatus.Running, acquired.Status);
        Assert.NotEqual(originalToken, acquired.ExecutionToken); // назначен новый токен выполнения
        Assert.True(acquired.LeaseExpiration > DateTime.UtcNow);
    }

    [Fact]
    public async Task TryAcquireNextPendingOrStaleAsync_ConcurrentWorkers_CannotAcquireSameTask()
    {
        var task = new Analysis
        {
            Id = 13,
            CommitId = 1,
            Status = AnalysisStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        await _repository.AddAsync(task);

        var worker1 = _repository.TryAcquireNextPendingOrStaleAsync(TimeSpan.FromMinutes(2));
        var worker2 = _repository.TryAcquireNextPendingOrStaleAsync(TimeSpan.FromMinutes(2));

        var results = await Task.WhenAll(worker1, worker2);

        var acquiredCount = results.Count(r => r != null);
        Assert.Equal(1, acquiredCount);
    }

    [Fact]
    public async Task RenewLeaseAsync_WithValidToken_RenewsLeaseExpiration()
    {
        var token = Guid.NewGuid();
        var initialExpiration = DateTime.UtcNow.AddSeconds(10);
        var task = new Analysis
        {
            Id = 14,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            ExecutionToken = token,
            LeaseExpiration = initialExpiration
        };
        await _repository.AddAsync(task);

        var renewed = await _repository.RenewLeaseAsync(14, token, TimeSpan.FromMinutes(5));

        Assert.True(renewed);
        var updated = await _repository.GetByIdAsync(14);
        Assert.NotNull(updated);
        Assert.True(updated.LeaseExpiration > initialExpiration);
    }

    [Fact]
    public async Task RenewLeaseAsync_WithMismatchedToken_ReturnsFalse()
    {
        var validToken = Guid.NewGuid();
        var wrongToken = Guid.NewGuid();
        var task = new Analysis
        {
            Id = 15,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            ExecutionToken = validToken,
            LeaseExpiration = DateTime.UtcNow.AddMinutes(1)
        };
        await _repository.AddAsync(task);

        var renewed = await _repository.RenewLeaseAsync(15, wrongToken, TimeSpan.FromMinutes(5));

        Assert.False(renewed);
    }

    [Fact]
    public async Task CompleteAsync_WithValidToken_SavesResultsAndMarksCompleted()
    {
        var token = Guid.NewGuid();
        var task = new Analysis
        {
            Id = 16,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            CreatedAt = DateTime.UtcNow.AddMinutes(-2),
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            ExecutionToken = token,
            LeaseExpiration = DateTime.UtcNow.AddMinutes(1)
        };
        await _repository.AddAsync(task);

        var results = new List<AnalysisResult>
        {
            new()
            {
                FilePath = "App.cs",
                LineNumber = 42,
                Rule = "SEC001",
                Message = "Secret detected",
                Severity = Severity.Critical,
                AnalyzerType = AnalyzerType.Security
            }
        };

        var completed = await _repository.CompleteAsync(16, token, results);

        Assert.True(completed);
        var updated = await _repository.GetByIdAsync(16);
        Assert.NotNull(updated);
        Assert.Equal(AnalysisStatus.Completed, updated.Status);
        Assert.NotNull(updated.CompletedAt);
        Assert.Null(updated.LeaseExpiration);
        Assert.Single(updated.Results);
    }

    [Fact]
    public async Task CompleteAsync_WithMismatchedToken_DoesNotOverwriteState()
    {
        var actualToken = Guid.NewGuid();
        var staleWorkerToken = Guid.NewGuid();
        var task = new Analysis
        {
            Id = 17,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            CreatedAt = DateTime.UtcNow.AddMinutes(-2),
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            ExecutionToken = actualToken,
            LeaseExpiration = DateTime.UtcNow.AddMinutes(1)
        };
        await _repository.AddAsync(task);

        var completed = await _repository.CompleteAsync(17, staleWorkerToken, []);

        Assert.False(completed);
        var check = await _repository.GetByIdAsync(17);
        Assert.NotNull(check);
        Assert.Equal(AnalysisStatus.Running, check.Status);
        Assert.Null(check.CompletedAt);
    }

    [Fact]
    public async Task FailAsync_WithValidToken_MarksFailed()
    {
        var token = Guid.NewGuid();
        var task = new Analysis
        {
            Id = 18,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            CreatedAt = DateTime.UtcNow.AddMinutes(-2),
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            ExecutionToken = token,
            LeaseExpiration = DateTime.UtcNow.AddMinutes(1)
        };
        await _repository.AddAsync(task);

        var failed = await _repository.FailAsync(18, token);

        Assert.True(failed);
        var updated = await _repository.GetByIdAsync(18);
        Assert.NotNull(updated);
        Assert.Equal(AnalysisStatus.Failed, updated.Status);
        Assert.NotNull(updated.CompletedAt);
        Assert.Null(updated.LeaseExpiration);
    }

    [Fact]
    public async Task FailAsync_WithMismatchedToken_ReturnsFalse()
    {
        var actualToken = Guid.NewGuid();
        var wrongToken = Guid.NewGuid();
        var task = new Analysis
        {
            Id = 19,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            CreatedAt = DateTime.UtcNow.AddMinutes(-2),
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            ExecutionToken = actualToken,
            LeaseExpiration = DateTime.UtcNow.AddMinutes(1)
        };
        await _repository.AddAsync(task);

        var failed = await _repository.FailAsync(19, wrongToken);

        Assert.False(failed);
        var check = await _repository.GetByIdAsync(19);
        Assert.NotNull(check);
        Assert.Equal(AnalysisStatus.Running, check.Status);
    }


    [Fact]
    public async Task RenewLeaseAsync_WithStaleTokenAfterTaskStolen_ReturnsFalseAndPreservesState()
    {
        var token1 = Guid.NewGuid();
        var token2 = Guid.NewGuid();
        var initialExpiration = DateTime.UtcNow.AddMinutes(1);
        var stolenExpiration = DateTime.UtcNow.AddMinutes(5);

        var task = new Analysis
        {
            Id = 20,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            ExecutionToken = token1,
            LeaseExpiration = initialExpiration
        };
        await _repository.AddAsync(task);

        // Имитируем перехват задачи другим воркером после истечения аренды
        task.ExecutionToken = token2;
        task.LeaseExpiration = stolenExpiration;
        await _context.SaveChangesAsync();

        // Устаревший воркер пытается продлить аренду со старым токеном
        var renewed = await _repository.RenewLeaseAsync(20, token1, TimeSpan.FromMinutes(2));

        Assert.False(renewed);

        // Проверяем, что в базе данных состояние задачи не повреждено
        var current = await _repository.GetByIdAsync(20);
        Assert.NotNull(current);
        Assert.Equal(token2, current.ExecutionToken);
        Assert.Equal(stolenExpiration, current.LeaseExpiration);
        Assert.Equal(AnalysisStatus.Running, current.Status);
    }

    [Fact]
    public async Task CompleteAsync_WithStaleTokenAfterTaskStolen_ReturnsFalse_DoesNotModifyStatus_AndDoesNotSaveResults()
    {
        var token1 = Guid.NewGuid();
        var token2 = Guid.NewGuid();

        var task = new Analysis
        {
            Id = 21,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            ExecutionToken = token1,
            LeaseExpiration = DateTime.UtcNow.AddMinutes(1)
        };
        await _repository.AddAsync(task);

        // Другой воркер перехватил задачу
        task.ExecutionToken = token2;
        await _context.SaveChangesAsync();

        var staleResults = new List<AnalysisResult>
        {
            new()
            {
                FilePath = "Stale.cs",
                LineNumber = 10,
                Rule = "SEC001",
                Message = "Stale finding",
                Severity = Severity.Critical,
                AnalyzerType = AnalyzerType.Security
            }
        };

        // Устаревший воркер пытается завершить задачу
        var completed = await _repository.CompleteAsync(21, token1, staleResults);

        Assert.False(completed);

        var current = await _repository.GetByIdAsync(21);
        Assert.NotNull(current);
        Assert.Equal(AnalysisStatus.Running, current.Status);
        Assert.Equal(token2, current.ExecutionToken);
        Assert.Null(current.CompletedAt);
        Assert.Empty(current.Results);
    }

    [Fact]
    public async Task FailAsync_WithStaleTokenAfterTaskStolen_ReturnsFalseAndPreservesRunningStatus()
    {
        var token1 = Guid.NewGuid();
        var token2 = Guid.NewGuid();

        var task = new Analysis
        {
            Id = 22,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            ExecutionToken = token1,
            LeaseExpiration = DateTime.UtcNow.AddMinutes(1)
        };
        await _repository.AddAsync(task);

        // Другой воркер перехватил задачу
        task.ExecutionToken = token2;
        await _context.SaveChangesAsync();

        // Устаревший воркер пытается пометить задачу как сбойную
        var failed = await _repository.FailAsync(22, token1);

        Assert.False(failed);

        var current = await _repository.GetByIdAsync(22);
        Assert.NotNull(current);
        Assert.Equal(AnalysisStatus.Running, current.Status);
        Assert.Equal(token2, current.ExecutionToken);
        Assert.Null(current.CompletedAt);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }
}
