using Application.Interfaces.Repositories;
using DatabaseAnalyzer.Runner;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Repository;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Backend.Tests.Integration;

public sealed class PostgreSqlQueueOperationsTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly string _testStorageRoot;

    public PostgreSqlQueueOperationsTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _testStorageRoot = Path.Combine(Path.GetTempPath(), "test_queue_ops_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testStorageRoot);
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_testStorageRoot))
            {
                Directory.Delete(_testStorageRoot, recursive: true);
            }
        }
        catch
        {
        }
        return Task.CompletedTask;
    }

    private AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        return new AppDbContext(options);
    }

    private async Task<Analysis> CreateTestAnalysisAsync(
        AnalysisStatus status = AnalysisStatus.Pending,
        Guid? executionToken = null,
        DateTime? leaseExpiration = null,
        DateTime? nextAttemptAt = null,
        int attemptCount = 0,
        int maxAttempts = 3)
    {
        await using var context = CreateDbContext();

        var user = new User
        {
            Username = $"user_{Guid.NewGuid():N}"[..16],
            PasswordHash = "hash",
            RoleId = 2
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var project = new Project
        {
            Name = "QueueTestProject",
            RepoUrl = "https://github.com/test/repo",
            OwnerId = user.Id
        };
        context.Projects.Add(project);
        await context.SaveChangesAsync();

        var commit = new Commit
        {
            ProjectId = project.Id,
            CommitHash = Guid.NewGuid().ToString("N"),
            CommitDate = DateTime.UtcNow
        };
        context.Commits.Add(commit);
        await context.SaveChangesAsync();

        var analysis = new Analysis
        {
            CommitId = commit.Id,
            Status = status,
            ExecutionToken = executionToken,
            LeaseExpiration = leaseExpiration,
            NextAttemptAt = nextAttemptAt,
            AttemptCount = attemptCount,
            MaxAttempts = maxAttempts,
            CreatedAt = DateTime.UtcNow
        };
        context.Analyses.Add(analysis);
        await context.SaveChangesAsync();

        return analysis;
    }

    private async Task<(PostgreSqlAnalysisLeaseGuard Guard, NpgsqlConnection Connection)> CreateGuardAsync(int analysisId, Guid token)
    {
        var guardConnection = new NpgsqlConnection(_fixture.ConnectionString);
        await guardConnection.OpenAsync();

        await using (var cmd = guardConnection.CreateCommand())
        {
            cmd.CommandText = "SELECT pg_advisory_lock_shared(hashtext('netscope_analysis_workspace'), @id);";
            cmd.Parameters.AddWithValue("id", analysisId);
            await cmd.ExecuteNonQueryAsync();
        }

        var workspacePath = Path.Combine(_testStorageRoot, "ws_" + analysisId);
        Directory.CreateDirectory(workspacePath);
        var lockFilePath = Path.Combine(workspacePath, ".workspace.lock");
        var fileStream = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

        var guard = new PostgreSqlAnalysisLeaseGuard(analysisId, token, isAcquired: true, guardConnection, fileStream);
        return (guard, guardConnection);
    }

    [Fact]
    public async Task TryAcquireNextPendingOrStaleAsync_IgnoresTasks_WithNextAttemptAtInFuture()
    {
        var futureAttempt = DateTime.UtcNow.AddMinutes(10);
        await CreateTestAnalysisAsync(AnalysisStatus.Pending, nextAttemptAt: futureAttempt);

        await using var context = CreateDbContext();
        var repository = new AnalysisRepository(context);

        var acquired = await repository.TryAcquireNextPendingOrStaleAsync(TimeSpan.FromMinutes(1));

        Assert.Null(acquired);
    }

    [Fact]
    public async Task TryAcquireNextPendingOrStaleAsync_AcquiresTask_WhenNextAttemptAtInPast()
    {
        var pastAttempt = DateTime.UtcNow.AddSeconds(-30);
        var created = await CreateTestAnalysisAsync(AnalysisStatus.Pending, nextAttemptAt: pastAttempt, attemptCount: 0);

        await using var context = CreateDbContext();
        var repository = new AnalysisRepository(context);

        var acquired = await repository.TryAcquireNextPendingOrStaleAsync(TimeSpan.FromMinutes(1));

        Assert.NotNull(acquired);
        Assert.Equal(created.Id, acquired.Id);
        Assert.Equal(AnalysisStatus.Running, acquired.Status);
        Assert.Equal(1, acquired.AttemptCount);
        Assert.Null(acquired.NextAttemptAt);
        Assert.NotNull(acquired.ExecutionToken);
    }

    [Fact]
    public async Task ScheduleRetryAsync_TransitionsRunningTask_ToPendingWithNextAttemptAt_WhenGuardValid()
    {
        var created = await CreateTestAnalysisAsync(AnalysisStatus.Pending);

        await using var context = CreateDbContext();
        var repository = new AnalysisRepository(context);

        var acquired = await repository.TryAcquireNextPendingOrStaleAsync(TimeSpan.FromMinutes(5));
        Assert.NotNull(acquired);

        var (guard, conn) = await CreateGuardAsync(acquired.Id, acquired.ExecutionToken!.Value);
        await using (guard)
        {
            var retryTime = DateTime.UtcNow.AddMinutes(2);
            var scheduled = await repository.ScheduleRetryAsync(
                acquired.Id,
                acquired.ExecutionToken!.Value,
                retryTime,
                "Rate limited: wait 2 minutes",
                guard);

            Assert.True(scheduled);

            await using var verifyContext = CreateDbContext();
            var refreshed = await verifyContext.Analyses.FindAsync(acquired.Id);
            Assert.NotNull(refreshed);
            Assert.Equal(AnalysisStatus.Pending, refreshed.Status);
            Assert.Null(refreshed.ExecutionToken);
            Assert.Null(refreshed.LeaseExpiration);
            Assert.NotNull(refreshed.NextAttemptAt);
            Assert.Equal("Rate limited: wait 2 minutes", refreshed.ErrorMessage);
        }
    }

    [Fact]
    public async Task TryAcquireNextPendingOrStaleAsync_TransitionsStaleTaskWithMaxAttempts_ToFailed_PoisonPill()
    {
        var token = Guid.NewGuid();
        var expiredLease = DateTime.UtcNow.AddMinutes(-5);
        var stalePoison = await CreateTestAnalysisAsync(
            status: AnalysisStatus.Running,
            executionToken: token,
            leaseExpiration: expiredLease,
            attemptCount: 3,
            maxAttempts: 3);

        await using var context = CreateDbContext();
        var repository = new AnalysisRepository(context);

        var acquired = await repository.TryAcquireNextPendingOrStaleAsync(TimeSpan.FromMinutes(1));

        Assert.Null(acquired);

        await using var verifyContext = CreateDbContext();
        var refreshed = await verifyContext.Analyses.FindAsync(stalePoison.Id);
        Assert.NotNull(refreshed);
        Assert.Equal(AnalysisStatus.Failed, refreshed.Status);
        Assert.Null(refreshed.LeaseExpiration);
        Assert.NotNull(refreshed.CompletedAt);
        Assert.Contains("Poison Pill", refreshed.ErrorMessage);
    }

    [Fact]
    public async Task CompleteAsync_Fails_WhenLeaseExpirationIsNull()
    {
        var token = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(
            status: AnalysisStatus.Running,
            executionToken: token,
            leaseExpiration: null);

        var (guard, conn) = await CreateGuardAsync(analysis.Id, token);
        await using (guard)
        {
            await using var context = CreateDbContext();
            var repository = new AnalysisRepository(context);

            var findingResult = new AnalysisResult
            {
                Rule = "SEC001",
                Message = "Test finding",
                Severity = Severity.Critical,
                FilePath = "Program.cs"
            };

            var completed = await repository.CompleteAsync(analysis.Id, token, [findingResult], guard);

            Assert.False(completed);

            await using var verifyContext = CreateDbContext();
            var refreshed = await verifyContext.Analyses
                .Include(a => a.Results)
                .FirstOrDefaultAsync(a => a.Id == analysis.Id);

            Assert.NotNull(refreshed);
            Assert.Equal(AnalysisStatus.Running, refreshed.Status);
            Assert.Null(refreshed.CompletedAt);
            Assert.Empty(refreshed.Results);
        }
    }

    [Fact]
    public async Task CompleteAsync_Fails_WhenLeaseExpiredByPostgreSqlServerClock()
    {
        var token = Guid.NewGuid();
        var expiredLease = DateTime.UtcNow.AddSeconds(-5);
        var analysis = await CreateTestAnalysisAsync(
            status: AnalysisStatus.Running,
            executionToken: token,
            leaseExpiration: expiredLease);

        var (guard, conn) = await CreateGuardAsync(analysis.Id, token);
        await using (guard)
        {
            await using var context = CreateDbContext();
            var repository = new AnalysisRepository(context);

            var findingResult = new AnalysisResult
            {
                Rule = "SEC002",
                Message = "Expired finding",
                Severity = Severity.Warning,
                FilePath = "Program.cs"
            };

            var completed = await repository.CompleteAsync(analysis.Id, token, [findingResult], guard);

            Assert.False(completed);

            await using var verifyContext = CreateDbContext();
            var refreshed = await verifyContext.Analyses
                .Include(a => a.Results)
                .FirstOrDefaultAsync(a => a.Id == analysis.Id);

            Assert.NotNull(refreshed);
            Assert.Equal(AnalysisStatus.Running, refreshed.Status);
            Assert.Null(refreshed.CompletedAt);
            Assert.Empty(refreshed.Results);
        }
    }

    [Fact]
    public async Task FailAsync_Fails_WhenLeaseExpirationIsNull()
    {
        var token = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(
            status: AnalysisStatus.Running,
            executionToken: token,
            leaseExpiration: null);

        var (guard, conn) = await CreateGuardAsync(analysis.Id, token);
        await using (guard)
        {
            await using var context = CreateDbContext();
            var repository = new AnalysisRepository(context);

            var failed = await repository.FailAsync(analysis.Id, token, "Should be rejected", guard);

            Assert.False(failed);

            await using var verifyContext = CreateDbContext();
            var refreshed = await verifyContext.Analyses.FindAsync(analysis.Id);
            Assert.NotNull(refreshed);
            Assert.Equal(AnalysisStatus.Running, refreshed.Status);
            Assert.Null(refreshed.CompletedAt);
            Assert.Null(refreshed.ErrorMessage);
        }
    }

    [Fact]
    public async Task FailAsync_Fails_WhenLeaseExpiredByPostgreSqlServerClock()
    {
        var token = Guid.NewGuid();
        var expiredLease = DateTime.UtcNow.AddSeconds(-5);
        var analysis = await CreateTestAnalysisAsync(
            status: AnalysisStatus.Running,
            executionToken: token,
            leaseExpiration: expiredLease);

        var (guard, conn) = await CreateGuardAsync(analysis.Id, token);
        await using (guard)
        {
            await using var context = CreateDbContext();
            var repository = new AnalysisRepository(context);

            var failed = await repository.FailAsync(analysis.Id, token, "Expired fail", guard);

            Assert.False(failed);

            await using var verifyContext = CreateDbContext();
            var refreshed = await verifyContext.Analyses.FindAsync(analysis.Id);
            Assert.NotNull(refreshed);
            Assert.Equal(AnalysisStatus.Running, refreshed.Status);
            Assert.Null(refreshed.CompletedAt);
            Assert.Null(refreshed.ErrorMessage);
        }
    }

    [Fact]
    public async Task ScheduleRetryAsync_Fails_WhenLeaseExpirationIsNull()
    {
        var token = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(
            status: AnalysisStatus.Running,
            executionToken: token,
            leaseExpiration: null);

        var (guard, conn) = await CreateGuardAsync(analysis.Id, token);
        await using (guard)
        {
            await using var context = CreateDbContext();
            var repository = new AnalysisRepository(context);

            var scheduled = await repository.ScheduleRetryAsync(
                analysis.Id,
                token,
                DateTime.UtcNow.AddMinutes(5),
                "Should not be scheduled",
                guard);

            Assert.False(scheduled);

            await using var verifyContext = CreateDbContext();
            var refreshed = await verifyContext.Analyses.FindAsync(analysis.Id);
            Assert.NotNull(refreshed);
            Assert.Equal(AnalysisStatus.Running, refreshed.Status);
            Assert.Null(refreshed.NextAttemptAt);
            Assert.Null(refreshed.ErrorMessage);
        }
    }

    [Fact]
    public async Task ScheduleRetryAsync_Fails_WhenLeaseExpiredByPostgreSqlServerClock()
    {
        var token = Guid.NewGuid();
        var expiredLease = DateTime.UtcNow.AddSeconds(-5);
        var analysis = await CreateTestAnalysisAsync(
            status: AnalysisStatus.Running,
            executionToken: token,
            leaseExpiration: expiredLease);

        var (guard, conn) = await CreateGuardAsync(analysis.Id, token);
        await using (guard)
        {
            await using var context = CreateDbContext();
            var repository = new AnalysisRepository(context);

            var scheduled = await repository.ScheduleRetryAsync(
                analysis.Id,
                token,
                DateTime.UtcNow.AddMinutes(5),
                "Expired retry",
                guard);

            Assert.False(scheduled);

            await using var verifyContext = CreateDbContext();
            var refreshed = await verifyContext.Analyses.FindAsync(analysis.Id);
            Assert.NotNull(refreshed);
            Assert.Equal(AnalysisStatus.Running, refreshed.Status);
            Assert.Null(refreshed.NextAttemptAt);
            Assert.Null(refreshed.ErrorMessage);
        }
    }

    [Fact]
    public async Task RenewLeaseAsync_Fails_WhenLeaseExpirationIsNull()
    {
        var token = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(
            status: AnalysisStatus.Running,
            executionToken: token,
            leaseExpiration: null);

        await using var context = CreateDbContext();
        var repository = new AnalysisRepository(context);

        var renewed = await repository.RenewLeaseAsync(analysis.Id, token, TimeSpan.FromMinutes(5));

        Assert.False(renewed);

        await using var verifyContext = CreateDbContext();
        var refreshed = await verifyContext.Analyses.FindAsync(analysis.Id);
        Assert.NotNull(refreshed);
        Assert.Null(refreshed.LeaseExpiration);
    }

    [Fact]
    public async Task RenewLeaseAsync_Fails_WhenLeaseExpired()
    {
        var token = Guid.NewGuid();
        var expiredLease = DateTime.UtcNow.AddSeconds(-5);
        var analysis = await CreateTestAnalysisAsync(
            status: AnalysisStatus.Running,
            executionToken: token,
            leaseExpiration: expiredLease);

        await using var context = CreateDbContext();
        var repository = new AnalysisRepository(context);

        var renewed = await repository.RenewLeaseAsync(analysis.Id, token, TimeSpan.FromMinutes(5));

        Assert.False(renewed);

        await using var verifyContext = CreateDbContext();
        var refreshed = await verifyContext.Analyses.FindAsync(analysis.Id);
        Assert.NotNull(refreshed);
        Assert.True(refreshed.LeaseExpiration < DateTime.UtcNow);
    }

    [Fact]
    public async Task RenewLeaseAsync_Succeeds_WhenLeaseActive()
    {
        var token = Guid.NewGuid();
        var activeLease = DateTime.UtcNow.AddMinutes(2);
        var analysis = await CreateTestAnalysisAsync(
            status: AnalysisStatus.Running,
            executionToken: token,
            leaseExpiration: activeLease);

        await using var context = CreateDbContext();
        var repository = new AnalysisRepository(context);

        var renewed = await repository.RenewLeaseAsync(analysis.Id, token, TimeSpan.FromMinutes(10));

        Assert.True(renewed);

        await using var verifyContext = CreateDbContext();
        var refreshed = await verifyContext.Analyses.FindAsync(analysis.Id);
        Assert.NotNull(refreshed);
        Assert.True(refreshed.LeaseExpiration > DateTime.UtcNow.AddMinutes(8));
    }

    [Fact]
    public async Task CompleteAsync_Succeeds_WhenGuardAndLeaseValid()
    {
        var token = Guid.NewGuid();
        var activeLease = DateTime.UtcNow.AddMinutes(5);
        var analysis = await CreateTestAnalysisAsync(
            status: AnalysisStatus.Running,
            executionToken: token,
            leaseExpiration: activeLease);

        var (guard, conn) = await CreateGuardAsync(analysis.Id, token);
        await using (guard)
        {
            await using var context = CreateDbContext();
            var repository = new AnalysisRepository(context);

            var findingResult = new AnalysisResult
            {
                Rule = "SEC001",
                Message = "Test finding",
                Severity = Severity.Critical,
                FilePath = "Program.cs"
            };

            var completed = await repository.CompleteAsync(analysis.Id, token, [findingResult], guard);

            Assert.True(completed);

            await using var verifyContext = CreateDbContext();
            var refreshed = await verifyContext.Analyses
                .Include(a => a.Results)
                .FirstOrDefaultAsync(a => a.Id == analysis.Id);

            Assert.NotNull(refreshed);
            Assert.Equal(AnalysisStatus.Completed, refreshed.Status);
            Assert.Null(refreshed.LeaseExpiration);
            Assert.NotNull(refreshed.CompletedAt);
            Assert.Single(refreshed.Results);
            Assert.Equal("SEC001", refreshed.Results.First().Rule);
        }
    }

    [Fact]
    public async Task FailAsync_Succeeds_WhenGuardAndLeaseValid()
    {
        var token = Guid.NewGuid();
        var activeLease = DateTime.UtcNow.AddMinutes(5);
        var analysis = await CreateTestAnalysisAsync(
            status: AnalysisStatus.Running,
            executionToken: token,
            leaseExpiration: activeLease);

        var (guard, conn) = await CreateGuardAsync(analysis.Id, token);
        await using (guard)
        {
            await using var context = CreateDbContext();
            var repository = new AnalysisRepository(context);

            var failed = await repository.FailAsync(analysis.Id, token, "Valid error message", guard);

            Assert.True(failed);

            await using var verifyContext = CreateDbContext();
            var refreshed = await verifyContext.Analyses.FindAsync(analysis.Id);
            Assert.NotNull(refreshed);
            Assert.Equal(AnalysisStatus.Failed, refreshed.Status);
            Assert.Null(refreshed.LeaseExpiration);
            Assert.NotNull(refreshed.CompletedAt);
            Assert.Equal("Valid error message", refreshed.ErrorMessage);
        }
    }

    [Fact]
    public async Task CompleteAsync_Fails_AndDatabaseUntouched_WhenGuardConnectionSeveredBeforeWrite()
    {
        var token = Guid.NewGuid();
        var futureLease = DateTime.UtcNow.AddMinutes(5);
        var analysis = await CreateTestAnalysisAsync(
            status: AnalysisStatus.Running,
            executionToken: token,
            leaseExpiration: futureLease);

        var (guard, conn) = await CreateGuardAsync(analysis.Id, token);

        // Разрываем физическое соединение guard до операции записи
        await conn.CloseAsync();
        await conn.DisposeAsync();

        await using var context = CreateDbContext();
        var repository = new AnalysisRepository(context);

        var findingResult = new AnalysisResult
        {
            Rule = "SEC001",
            Message = "Test finding",
            Severity = Severity.Critical,
            FilePath = "Program.cs"
        };

        var completed = await repository.CompleteAsync(analysis.Id, token, [findingResult], guard);

        Assert.False(completed);

        // Проверяем, что состояние базы данных полностью нетронуто
        await using var verifyContext = CreateDbContext();
        var refreshed = await verifyContext.Analyses
            .Include(a => a.Results)
            .FirstOrDefaultAsync(a => a.Id == analysis.Id);

        Assert.NotNull(refreshed);
        Assert.Equal(AnalysisStatus.Running, refreshed.Status);
        Assert.Null(refreshed.CompletedAt);
        Assert.Empty(refreshed.Results);
    }

    [Fact]
    public async Task FailAsync_Fails_AndDatabaseUntouched_WhenGuardConnectionSeveredBeforeWrite()
    {
        var token = Guid.NewGuid();
        var futureLease = DateTime.UtcNow.AddMinutes(5);
        var analysis = await CreateTestAnalysisAsync(
            status: AnalysisStatus.Running,
            executionToken: token,
            leaseExpiration: futureLease);

        var (guard, conn) = await CreateGuardAsync(analysis.Id, token);

        // Разрываем физическое соединение guard
        await conn.CloseAsync();
        await conn.DisposeAsync();

        await using var context = CreateDbContext();
        var repository = new AnalysisRepository(context);

        var failed = await repository.FailAsync(analysis.Id, token, "Should not be written", guard);

        Assert.False(failed);

        await using var verifyContext = CreateDbContext();
        var refreshed = await verifyContext.Analyses.FindAsync(analysis.Id);
        Assert.NotNull(refreshed);
        Assert.Equal(AnalysisStatus.Running, refreshed.Status);
        Assert.Null(refreshed.CompletedAt);
        Assert.Null(refreshed.ErrorMessage);
    }

    [Fact]
    public async Task ScheduleRetryAsync_Fails_AndDatabaseUntouched_WhenGuardConnectionSeveredBeforeWrite()
    {
        var token = Guid.NewGuid();
        var futureLease = DateTime.UtcNow.AddMinutes(5);
        var analysis = await CreateTestAnalysisAsync(
            status: AnalysisStatus.Running,
            executionToken: token,
            leaseExpiration: futureLease);

        var (guard, conn) = await CreateGuardAsync(analysis.Id, token);

        // Разрываем физическое соединение guard
        await conn.CloseAsync();
        await conn.DisposeAsync();

        await using var context = CreateDbContext();
        var repository = new AnalysisRepository(context);

        var scheduled = await repository.ScheduleRetryAsync(
            analysis.Id,
            token,
            DateTime.UtcNow.AddMinutes(10),
            "Should not be scheduled",
            guard);

        Assert.False(scheduled);

        await using var verifyContext = CreateDbContext();
        var refreshed = await verifyContext.Analyses.FindAsync(analysis.Id);
        Assert.NotNull(refreshed);
        Assert.Equal(AnalysisStatus.Running, refreshed.Status);
        Assert.Equal(token, refreshed.ExecutionToken);
        Assert.Null(refreshed.NextAttemptAt);
    }

    [Fact]
    public async Task CompleteAsync_Fails_WhenCleanerAcquiresExclusiveLock_AfterGuardConnectionLost()
    {
        var token = Guid.NewGuid();
        var futureLease = DateTime.UtcNow.AddMinutes(5);
        var analysis = await CreateTestAnalysisAsync(
            status: AnalysisStatus.Running,
            executionToken: token,
            leaseExpiration: futureLease);

        var (guard, conn) = await CreateGuardAsync(analysis.Id, token);

        // Воркер теряет соединение (пул очищается для немедленного закрытия физической сессии в PostgreSQL)
        await conn.CloseAsync();
        NpgsqlConnection.ClearPool(conn);
        await conn.DisposeAsync();

        // Cleaner перехватывает задачу и захватывает эксклюзивный advisory lock на отдельном соединении
        await using var cleanerConn = new NpgsqlConnection(_fixture.ConnectionString);
        await cleanerConn.OpenAsync();
        await using (var cmd = cleanerConn.CreateCommand())
        {
            cmd.CommandText = "SELECT pg_try_advisory_lock(hashtext('netscope_analysis_workspace'), @id);";
            cmd.Parameters.AddWithValue("id", analysis.Id);
            var cleanerLockAcquired = (bool)(await cmd.ExecuteScalarAsync())!;
            Assert.True(cleanerLockAcquired);
        }

        // Старый воркер пытается записать результат
        await using var context = CreateDbContext();
        var repository = new AnalysisRepository(context);

        var completed = await repository.CompleteAsync(analysis.Id, token, [], guard);

        Assert.False(completed);

        await using var verifyContext = CreateDbContext();
        var refreshed = await verifyContext.Analyses.FindAsync(analysis.Id);
        Assert.NotNull(refreshed);
        Assert.Equal(AnalysisStatus.Running, refreshed.Status);
    }

    [Fact]
    public async Task CompleteAsync_Fails_WhenExecutionTokenMismatch()
    {
        var oldToken = Guid.NewGuid();
        var newToken = Guid.NewGuid();
        var futureLease = DateTime.UtcNow.AddMinutes(5);
        var analysis = await CreateTestAnalysisAsync(
            status: AnalysisStatus.Running,
            executionToken: newToken,
            leaseExpiration: futureLease);

        var (guard, conn) = await CreateGuardAsync(analysis.Id, oldToken);
        await using (guard)
        {
            await using var context = CreateDbContext();
            var repository = new AnalysisRepository(context);

            var completed = await repository.CompleteAsync(analysis.Id, oldToken, [], guard);

            Assert.False(completed);

            await using var verifyContext = CreateDbContext();
            var refreshed = await verifyContext.Analyses.FindAsync(analysis.Id);
            Assert.NotNull(refreshed);
            Assert.Equal(AnalysisStatus.Running, refreshed.Status);
            Assert.Equal(newToken, refreshed.ExecutionToken);
        }
    }

    [Fact]
    public async Task Operations_Reject_NullOrUnacquiredGuard()
    {
        var token = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(
            status: AnalysisStatus.Running,
            executionToken: token,
            leaseExpiration: DateTime.UtcNow.AddMinutes(5));

        await using var context = CreateDbContext();
        var repository = new AnalysisRepository(context);

        // Null guard обязан отклоняться
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            repository.CompleteAsync(analysis.Id, token, [], null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            repository.FailAsync(analysis.Id, token, "err", null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            repository.ScheduleRetryAsync(analysis.Id, token, DateTime.UtcNow.AddMinutes(1), "err", null!));

        // Unacquired guard обязан возвращать false
        var unacquiredGuard = new PostgreSqlAnalysisLeaseGuard(analysis.Id, token, isAcquired: false, null, null);
        Assert.False(await repository.CompleteAsync(analysis.Id, token, [], unacquiredGuard));
        Assert.False(await repository.FailAsync(analysis.Id, token, "err", unacquiredGuard));
        Assert.False(await repository.ScheduleRetryAsync(analysis.Id, token, DateTime.UtcNow.AddMinutes(1), "err", unacquiredGuard));
    }
}
