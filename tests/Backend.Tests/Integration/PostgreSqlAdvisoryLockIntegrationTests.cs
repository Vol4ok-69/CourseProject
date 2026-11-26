using Infrastructure.Storage;
using System.Data;
using DatabaseAnalyzer.Runner;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Repository;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using Application.Interfaces;
using Moq;

namespace Backend.Tests.Integration;

/// <summary>
/// Реальные интеграционные тесты PostgreSQL session-level advisory locks (TD-003).
/// Выполняются против реального контейнера PostgreSQL 17 (Testcontainers).
/// </summary>
public sealed class PostgreSqlAdvisoryLockIntegrationTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private readonly PostgreSqlFixture fixture;
    private readonly string _testStorageRoot;
    private readonly WorkspaceFileLockProvider _fileLockProvider;

    public PostgreSqlAdvisoryLockIntegrationTests(PostgreSqlFixture fixture)
    {
        this.fixture = fixture;
        _testStorageRoot = Path.Combine(Path.GetTempPath(), "test_advisory_locks_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testStorageRoot);
        _fileLockProvider = new WorkspaceFileLockProvider(_testStorageRoot);
    }

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

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

    private async Task<Analysis> CreateTestAnalysisAsync(AnalysisStatus status, Guid token, DateTime? leaseExpiration = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        await using var context = new AppDbContext(options);

        // Создаем пользователя и проект для внешних ключей коммита
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
            Name = "TestProject",
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
            CreatedAt = DateTime.UtcNow,
            StartedAt = status == AnalysisStatus.Running ? DateTime.UtcNow : null,
            ExecutionToken = token,
            LeaseExpiration = leaseExpiration
        };
        context.Analyses.Add(analysis);
        await context.SaveChangesAsync();

        return analysis;
    }

    [Fact]
    public async Task AdvisoryLock_FirstWorker_AcquiresSuccessfully()
    {
        var token = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, token, DateTime.UtcNow.AddMinutes(5));

        var provider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);

        await using var guard = await provider.AcquireGuardAsync(analysis.Id, token);

        Assert.NotNull(guard);
        Assert.True(guard.IsAcquired, "First worker must successfully acquire advisory lock!");
        Assert.True(await guard.ValidateOwnershipAsync(), "Ownership validation must succeed!");
    }

	[Fact]
	public async Task AdvisoryLock_FileLockProviderThrows_PostgreSqlLockIsReleased()
	{
		var token = Guid.NewGuid();
		var analysis = await CreateTestAnalysisAsync(
			AnalysisStatus.Running,
			token,
			DateTime.UtcNow.AddMinutes(5));

		var fileLockProviderMock = new Mock<IWorkspaceFileLockProvider>();

		fileLockProviderMock
			.Setup(provider => provider.TryAcquireShared(analysis.Id))
			.Throws(new IOException("Simulated file lock acquisition failure."));

		var provider = new PostgreSqlAnalysisLeaseGuardProvider(
			fixture.ConnectionString,
			fileLockProviderMock.Object);

		await Assert.ThrowsAsync<IOException>(
			() => provider.AcquireGuardAsync(analysis.Id, token));

		// Проверяем, что PostgreSQL не оставил session-level advisory lock.
		await using var checkConnection = new NpgsqlConnection(fixture.ConnectionString);
		await checkConnection.OpenAsync();

		await using var command = checkConnection.CreateCommand();
		command.CommandText = """
			SELECT count(*)
			FROM pg_locks
			WHERE locktype = 'advisory'
			  AND classid = hashtext('netscope_analysis_workspace')
			  AND objid = @analysisId
			  AND objsubid = 2
			  AND granted = true;
			""";

		command.Parameters.AddWithValue("analysisId", analysis.Id);

		var lockCount = (long)(await command.ExecuteScalarAsync())!;

		Assert.Equal(
			0L,
			lockCount);
	}

    [Fact]
    public async Task AdvisoryLock_TwoWorkers_CanAcquireSharedLockForSameAnalysisId()
    {
        var token = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, token, DateTime.UtcNow.AddMinutes(5));

        var provider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);

        var guard1 = await provider.AcquireGuardAsync(analysis.Id, token);
        var guard2 = await provider.AcquireGuardAsync(analysis.Id, token);

        Assert.NotNull(guard1);
        Assert.NotNull(guard2);
        Assert.True(guard1.IsAcquired, "Worker 1 must acquire shared advisory lock!");
        Assert.True(guard2.IsAcquired, "Worker 2 must also acquire shared advisory lock for the same AnalysisId!");
        Assert.True(await guard1.ValidateOwnershipAsync(), "Worker 1 ownership validation must succeed!");
        Assert.True(await guard2.ValidateOwnershipAsync(), "Worker 2 ownership validation must succeed!");

        // Cleaner не может получить PostgreSQL ExclusiveLock, пока Worker удерживают SharedLock
        await using var cleanerConn = new NpgsqlConnection(fixture.ConnectionString);
        await cleanerConn.OpenAsync();

        await using (var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext('netscope_analysis_workspace'), @analysisId);", cleanerConn))
        {
            cmd.Parameters.AddWithValue("analysisId", analysis.Id);
            var cleanerLockAcquired = (bool)(await cmd.ExecuteScalarAsync())!;
            Assert.False(cleanerLockAcquired, "Cleaner must NOT acquire exclusive lock while workers hold shared lock!");
        }

        // После освобождения обоих Worker Cleaner должен получить PostgreSQL ExclusiveLock
        await guard1.DisposeAsync();
        await guard2.DisposeAsync();

        await using (var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext('netscope_analysis_workspace'), @analysisId);", cleanerConn))
        {
            cmd.Parameters.AddWithValue("analysisId", analysis.Id);
            var cleanerLockAcquired = (bool)(await cmd.ExecuteScalarAsync())!;
            Assert.True(cleanerLockAcquired, "Cleaner must acquire exclusive lock after both workers release shared locks!");
        }

        await using (var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtext('netscope_analysis_workspace'), @analysisId);", cleanerConn))
        {
            cmd.Parameters.AddWithValue("analysisId", analysis.Id);
            await cmd.ExecuteScalarAsync();
        }
    }

    [Fact]
    public async Task AdvisoryLock_CleanerCannotAcquireExclusiveLock_WhileWorkerHoldsSharedLock()
    {
        var token = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, token, DateTime.UtcNow.AddMinutes(5));

        var provider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);

        // 1. Worker захватывает shared lock через guard
        var guard = await provider.AcquireGuardAsync(analysis.Id, token);
        Assert.NotNull(guard);
        Assert.True(guard.IsAcquired, "Worker must acquire shared advisory lock!");

        // 2. Очиститель пытается захватить exclusive lock (SELECT pg_try_advisory_lock(...)) -> false
        await using var cleanerConn = new NpgsqlConnection(fixture.ConnectionString);
        await cleanerConn.OpenAsync();

        await using (var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext('netscope_analysis_workspace'), @analysisId);", cleanerConn))
        {
            cmd.Parameters.AddWithValue("analysisId", analysis.Id);
            var cleanerLockAcquired = (bool)(await cmd.ExecuteScalarAsync())!;
            Assert.False(cleanerLockAcquired, "Cleaner must NOT acquire exclusive lock while worker holds shared lock!");
        }

        // 3. Worker освобождает guard (await guard.DisposeAsync())
        await guard.DisposeAsync();

        // 4. Очиститель повторно пытается захватить exclusive lock -> true
        await using (var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext('netscope_analysis_workspace'), @analysisId);", cleanerConn))
        {
            cmd.Parameters.AddWithValue("analysisId", analysis.Id);
            var cleanerLockAcquired = (bool)(await cmd.ExecuteScalarAsync())!;
            Assert.True(cleanerLockAcquired, "Cleaner must successfully acquire exclusive lock after worker released shared lock!");
        }

        // 5. Очиститель освобождает exclusive lock (SELECT pg_advisory_unlock(...))
        await using (var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtext('netscope_analysis_workspace'), @analysisId);", cleanerConn))
        {
            cmd.Parameters.AddWithValue("analysisId", analysis.Id);
            var unlocked = (bool)(await cmd.ExecuteScalarAsync())!;
            Assert.True(unlocked, "Cleaner exclusive lock must be unlocked!");
        }
    }

    [Fact]
    public async Task AdvisoryLock_WorkerWithDifferentAnalysisId_AcquiresSuccessfully()
    {
        var token1 = Guid.NewGuid();
        var token2 = Guid.NewGuid();

        var analysis1 = await CreateTestAnalysisAsync(AnalysisStatus.Running, token1, DateTime.UtcNow.AddMinutes(5));
        var analysis2 = await CreateTestAnalysisAsync(AnalysisStatus.Running, token2, DateTime.UtcNow.AddMinutes(5));

        var provider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);

        // Worker 1 захватывает analysis1
        await using var guard1 = await provider.AcquireGuardAsync(analysis1.Id, token1);
        Assert.True(guard1.IsAcquired);

        // Worker 2 захватывает analysis2 (другой AnalysisId)
        await using var guard2 = await provider.AcquireGuardAsync(analysis2.Id, token2);

        Assert.NotNull(guard2);
        Assert.True(guard2.IsAcquired, "Worker with different AnalysisId must acquire its own lock successfully!");
    }

    [Fact]
    public async Task AdvisoryLock_AfterRelease_SecondWorkerCanAcquire()
    {
        var token1 = Guid.NewGuid();
        var token2 = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, token1, DateTime.UtcNow.AddMinutes(5));

        var provider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);

        var guard1 = await provider.AcquireGuardAsync(analysis.Id, token1);
        Assert.True(guard1.IsAcquired);

        // Освобождаем guard1
        await guard1.DisposeAsync();

        // Обновляем токен в БД для имитации передачи задачи воркеру 2
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;
        await using (var context = new AppDbContext(options))
        {
            var dbAnalysis = await context.Analyses.FindAsync(analysis.Id);
            dbAnalysis!.ExecutionToken = token2;
            await context.SaveChangesAsync();
        }

        // Worker 2 теперь может успешно захватить блокировку
        await using var guard2 = await provider.AcquireGuardAsync(analysis.Id, token2);

        Assert.True(guard2.IsAcquired, "Second worker must be able to acquire lock after first worker releases it!");
    }

    [Fact]
    public async Task AdvisoryLock_AfterConnectionDrop_FreedAutomaticallyByPostgreSql()
    {
        var token = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, token, DateTime.UtcNow.AddMinutes(5));

        // Открываем отдельное «сырое» соединение и захватываем advisory lock
        var rawConn = new NpgsqlConnection(fixture.ConnectionString);
        await rawConn.OpenAsync();
        var backendPid = rawConn.ProcessID;

        await using (var lockCmd = rawConn.CreateCommand())
        {
            lockCmd.CommandText = "SELECT pg_try_advisory_lock(hashtext('netscope_analysis_workspace'), @analysisId);";
            lockCmd.Parameters.AddWithValue("analysisId", analysis.Id);
            var locked = (bool)(await lockCmd.ExecuteScalarAsync())!;
            Assert.True(locked);
        }

        // Проверяем, что второй воркер не может получить блокировку
        var provider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        var guardBlocked = await provider.AcquireGuardAsync(analysis.Id, token);
        Assert.False(guardBlocked.IsAcquired);
        await guardBlocked.DisposeAsync();

        // Аварийный обрыв соединения первого воркера (Kill/Close через pg_terminate_backend)
        await using (var killerConn = new NpgsqlConnection(fixture.ConnectionString))
        {
            await killerConn.OpenAsync();
            await using var killCmd = killerConn.CreateCommand();
            killCmd.CommandText = "SELECT pg_terminate_backend(@pid);";
            killCmd.Parameters.AddWithValue("pid", backendPid);
            await killCmd.ExecuteScalarAsync();
        }

        try
        {
            await rawConn.CloseAsync();
            await rawConn.DisposeAsync();
        }
        catch
        {
            // Соединение разорвано на стороне сервера
        }

        NpgsqlConnection.ClearPool(rawConn);

        // Даем серверу PostgreSQL небольшое время на освобождение ресурсов сессии
        for (var i = 0; i < 20; i++)
        {
            await using var probeGuard = await provider.AcquireGuardAsync(analysis.Id, token);
            if (probeGuard.IsAcquired)
            {
                break;
            }
            await Task.Delay(50);
        }

        // В PostgreSQL после закрытия сессии advisory lock освобождается немедленно
        await using var guardAfterDrop = await provider.AcquireGuardAsync(analysis.Id, token);
        Assert.True(guardAfterDrop.IsAcquired, "PostgreSQL must automatically release advisory lock when connection drops!");
    }

    [Fact]
    public async Task AdvisoryLock_ConnectionWithActiveLock_HeldInPgLocks()
    {
        var token = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, token, DateTime.UtcNow.AddMinutes(5));

        var provider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);

        await using var guard = await provider.AcquireGuardAsync(analysis.Id, token);
        Assert.True(guard.IsAcquired);

        // Проверяем наличие активной блокировки в системном представлении pg_locks
        await using var checkConn = new NpgsqlConnection(fixture.ConnectionString);
        await checkConn.OpenAsync();

        await using var cmd = checkConn.CreateCommand();
        cmd.CommandText = """
            SELECT count(*)
            FROM pg_locks
            WHERE locktype = 'advisory'
              AND classid = hashtext('netscope_analysis_workspace')
              AND objid = @analysisId
              AND granted = true;
            """;
        cmd.Parameters.AddWithValue("analysisId", analysis.Id);

        var lockCount = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(1L, lockCount);
    }

    [Fact]
    public async Task AdvisoryLock_ConnectionWithActiveLock_NotPrematurelyReturnedToPool()
    {
        var token = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, token, DateTime.UtcNow.AddMinutes(5));

        var provider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);

        var guard = await provider.AcquireGuardAsync(analysis.Id, token);
        Assert.True(guard.IsAcquired);

        // Проверяем, что блокировка удерживается в pg_locks
        await using (var checkConn = new NpgsqlConnection(fixture.ConnectionString))
        {
            await checkConn.OpenAsync();
            await using var cmd = checkConn.CreateCommand();
            cmd.CommandText = """
                SELECT count(*) FROM pg_locks
                WHERE locktype = 'advisory'
                  AND classid = hashtext('netscope_analysis_workspace')
                  AND objid = @analysisId
                  AND granted = true;
                """;
            cmd.Parameters.AddWithValue("analysisId", analysis.Id);
            var count = (long)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal(1L, count);
        }

        // Выполняем повторную валидацию владения
        Assert.True(await guard.ValidateOwnershipAsync());

        // Освобождаем guard (только здесь соединение должно закрыться и блокировка сняться)
        await guard.DisposeAsync();

        // Проверяем, что блокировка действительно снята в pg_locks
        await using (var checkConn = new NpgsqlConnection(fixture.ConnectionString))
        {
            await checkConn.OpenAsync();
            await using var cmd = checkConn.CreateCommand();
            cmd.CommandText = """
                SELECT count(*) FROM pg_locks
                WHERE locktype = 'advisory'
                  AND classid = hashtext('netscope_analysis_workspace')
                  AND objid = @analysisId
                  AND granted = true;
                """;
            cmd.Parameters.AddWithValue("analysisId", analysis.Id);
            var count = (long)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal(0L, count);
        }
    }

    [Fact]
    public async Task LeaseGuard_ValidateOwnership_DetectsTokenChangeInDatabase()
    {
        var token1 = Guid.NewGuid();
        var token2 = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, token1, DateTime.UtcNow.AddMinutes(5));

        var provider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);

        await using var guard = await provider.AcquireGuardAsync(analysis.Id, token1);
        Assert.True(guard.IsAcquired);
        Assert.True(await guard.ValidateOwnershipAsync());

        // Другой процесс/воркер изменил ExecutionToken в БД
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;
        await using (var context = new AppDbContext(options))
        {
            var dbAnalysis = await context.Analyses.FindAsync(analysis.Id);
            dbAnalysis!.ExecutionToken = token2;
            await context.SaveChangesAsync();
        }

        // ValidateOwnershipAsync обязана обнаружить смену токена через активный запрос
        var isStillOwner = await guard.ValidateOwnershipAsync();
        Assert.False(isStillOwner, "ValidateOwnershipAsync must return false when ExecutionToken changes in DB!");
    }

    [Fact]
    public async Task Queue_TryAcquireNextPendingOrStale_DoesNotStealTask_WhileAdvisoryLockHeld()
    {
        // Имитация: задача со статусом Running и ИСТЁКШЕЙ арендой (LeaseExpiration < now)
        // Но воркер по-прежнему жив и удерживает advisory lock на рабочем соединении.
        var token = Guid.NewGuid();
        var expiredLease = DateTime.UtcNow.AddMinutes(-5);
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, token, expiredLease);

        var provider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);

        // Воркер удерживает блокировку
        await using var guard = await provider.AcquireGuardAsync(analysis.Id, token);
        Assert.True(guard.IsAcquired);

        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;
        await using var context = new AppDbContext(options);
        var repository = new AnalysisRepository(context);

        // Очередь пытается забрать истёкшую задачу
        var stolenCandidate = await repository.TryAcquireNextPendingOrStaleAsync(TimeSpan.FromMinutes(2));

        // Очередь НЕ должна украсть задачу, пока удерживается активный advisory lock!
        Assert.Null(stolenCandidate);

        // После того как воркер завершает работу и отпускает лок:
        await guard.DisposeAsync();

        // Теперь задача может быть безопасно перехвачена как зависшая
        var acquiredAfterRelease = await repository.TryAcquireNextPendingOrStaleAsync(TimeSpan.FromMinutes(2));
        Assert.NotNull(acquiredAfterRelease);
        Assert.Equal(analysis.Id, acquiredAfterRelease.Id);
    }

    [Fact]
    public async Task ScenarioA_ConcurrentQueueAcquisition_OnlyOneWorkerWins_AndChangesToken()
    {
        // Создаем единственную задачу со статусом Pending
        var pendingAnalysis = await CreateTestAnalysisAsync(AnalysisStatus.Pending, Guid.NewGuid(), null);

        using var barrier = new Barrier(2);

        Task<Analysis?> WorkerAttemptAsync()
        {
            return Task.Run(async () =>
            {
                var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;
                await using var context = new AppDbContext(options);
                var repo = new AnalysisRepository(context);

                barrier.SignalAndWait(TimeSpan.FromSeconds(5));
                return await repo.TryAcquireNextPendingOrStaleAsync(TimeSpan.FromMinutes(2));
            });
        }

        var taskA = WorkerAttemptAsync();
        var taskB = WorkerAttemptAsync();

        var results = await Task.WhenAll(taskA, taskB);

        var winner = results.SingleOrDefault(r => r != null);
        var loser = results.SingleOrDefault(r => r == null);

        Assert.NotNull(winner);
        Assert.Null(loser);
        Assert.Equal(pendingAnalysis.Id, winner.Id);
        Assert.Equal(AnalysisStatus.Running, winner.Status);
        Assert.NotNull(winner.ExecutionToken);

        // Проверяем в БД: токен соответствует токену победителя
        var verifyOptions = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;
        await using var verifyContext = new AppDbContext(verifyOptions);
        var dbAnalysis = await verifyContext.Analyses.FindAsync(pendingAnalysis.Id);

        Assert.NotNull(dbAnalysis);
        Assert.Equal(winner.ExecutionToken, dbAnalysis.ExecutionToken);
        Assert.Equal(AnalysisStatus.Running, dbAnalysis.Status);
    }

    [Fact]
    public async Task ScenarioB_ExpiredLease_WhileAdvisoryLockHeld_WorkerBCannotStealTask_AndWorkerARetainsOwnership()
    {
        var tokenA = Guid.NewGuid();
        var expiredLease = DateTime.UtcNow.AddMinutes(-10); // Просроченная аренда
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenA, expiredLease);

        var provider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);

        // Worker A удерживает session-level advisory lock
        await using var guardA = await provider.AcquireGuardAsync(analysis.Id, tokenA);
        Assert.True(guardA.IsAcquired);

        // Worker B пытается захватить задачу из очереди как stale
        var optionsB = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;
        await using var contextB = new AppDbContext(optionsB);
        var repoB = new AnalysisRepository(contextB);

        var stolenTask = await repoB.TryAcquireNextPendingOrStaleAsync(TimeSpan.FromMinutes(2));

        // Worker B НЕ может перехватить задачу
        Assert.Null(stolenTask);

        // В БД токен НЕ изменился
        await using var verifyContext = new AppDbContext(optionsB);
        var dbAnalysis = await verifyContext.Analyses.FindAsync(analysis.Id);
        Assert.NotNull(dbAnalysis);
        Assert.Equal(tokenA, dbAnalysis.ExecutionToken);

        // Worker A сохраняет действующее владение
        Assert.True(await guardA.ValidateOwnershipAsync());
    }

    [Fact]
    public async Task ScenarioC_ConnectionLoss_ReleasesAdvisoryLock_WorkerACannotProceed_WorkerBCanAcquire()
    {
        var tokenA = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenA, DateTime.UtcNow.AddMinutes(5));

        // Worker A открывает физическое соединение с блокировкой
        var rawConnA = new NpgsqlConnection(fixture.ConnectionString);
        await rawConnA.OpenAsync();
        var backendPidA = rawConnA.ProcessID;

        await using (var lockCmd = rawConnA.CreateCommand())
        {
            lockCmd.CommandText = "SELECT pg_try_advisory_lock(hashtext('netscope_analysis_workspace'), @analysisId);";
            lockCmd.Parameters.AddWithValue("analysisId", analysis.Id);
            Assert.True((bool)(await lockCmd.ExecuteScalarAsync())!);
        }

        var provider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);

        // Worker B пока не может получить блокировку
        var guardBBlocked = await provider.AcquireGuardAsync(analysis.Id, Guid.NewGuid());
        Assert.False(guardBBlocked.IsAcquired);
        await guardBBlocked.DisposeAsync();

        // Принудительно разрываем соединение Worker A (серверный kill сессии)
        await using (var killerConn = new NpgsqlConnection(fixture.ConnectionString))
        {
            await killerConn.OpenAsync();
            await using var killCmd = killerConn.CreateCommand();
            killCmd.CommandText = "SELECT pg_terminate_backend(@pid);";
            killCmd.Parameters.AddWithValue("pid", backendPidA);
            await killCmd.ExecuteScalarAsync();
        }

        try
        {
            await rawConnA.CloseAsync();
            await rawConnA.DisposeAsync();
        }
        catch { }

        NpgsqlConnection.ClearPool(rawConnA);

        // Worker A больше не может выполнять защищённые операции (соединение разорвано)
        var guardAAfterDrop = new PostgreSqlAnalysisLeaseGuard(analysis.Id, tokenA, isAcquired: true, rawConnA, workspaceFileLock: null);
        Assert.False(await guardAAfterDrop.ValidateOwnershipAsync());

        // Делаем задачу stale в БД
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;
        await using (var ctx = new AppDbContext(options))
        {
            var dbAnalysis = await ctx.Analyses.FindAsync(analysis.Id);
            dbAnalysis!.LeaseExpiration = DateTime.UtcNow.AddMinutes(-1);
            await ctx.SaveChangesAsync();
        }

        // Worker B теперь может получить задачу из очереди
        await using (var ctxB = new AppDbContext(options))
        {
            var repoB = new AnalysisRepository(ctxB);
            var acquiredByB = await repoB.TryAcquireNextPendingOrStaleAsync(TimeSpan.FromMinutes(2));

            Assert.NotNull(acquiredByB);
            Assert.Equal(analysis.Id, acquiredByB.Id);
            Assert.NotEqual(tokenA, acquiredByB.ExecutionToken);

            // Worker B может захватить workspace guard с новым токеном
            await using var guardB = await provider.AcquireGuardAsync(analysis.Id, acquiredByB.ExecutionToken!.Value);
            Assert.True(guardB.IsAcquired);
            Assert.True(await guardB.ValidateOwnershipAsync());
        }
    }

    [Fact]
    public async Task ScenarioD_IndependentTasks_CanExecuteConcurrentlyWithoutInterference()
    {
        var token1 = Guid.NewGuid();
        var token2 = Guid.NewGuid();
        var analysis1 = await CreateTestAnalysisAsync(AnalysisStatus.Running, token1, DateTime.UtcNow.AddMinutes(5));
        var analysis2 = await CreateTestAnalysisAsync(AnalysisStatus.Running, token2, DateTime.UtcNow.AddMinutes(5));

        var provider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);

        await using var guard1 = await provider.AcquireGuardAsync(analysis1.Id, token1);
        await using var guard2 = await provider.AcquireGuardAsync(analysis2.Id, token2);

        Assert.True(guard1.IsAcquired);
        Assert.True(guard2.IsAcquired);

        // Одновременная валидация независимых задач
        var validate1 = guard1.ValidateOwnershipAsync();
        var validate2 = guard2.ValidateOwnershipAsync();

        var results = await Task.WhenAll(validate1, validate2);

        Assert.True(results[0]);
        Assert.True(results[1]);
    }

    [Fact]
    public async Task ScenarioE_CompletionAndSaving_ProtectedAgainstInterference_OnlyValidTokenSaved()
    {
        var tokenA = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenA, DateTime.UtcNow.AddMinutes(5));

        var provider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);

        // Worker A удерживает guard во время анализа
        var guardA = await provider.AcquireGuardAsync(analysis.Id, tokenA);
        Assert.True(guardA.IsAcquired);

        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;

        // Попытка завершить анализ с неверным токеном (Worker B / impostor)
        await using (var ctxImpostor = new AppDbContext(options))
        {
            var repoImpostor = new AnalysisRepository(ctxImpostor);
            var impostorGuard = new FakeAnalysisLeaseGuard(analysis.Id, Guid.NewGuid());
            var completedWithWrongToken = await repoImpostor.CompleteAsync(
                analysis.Id,
                Guid.NewGuid(), // Чужой токен
                [new AnalysisResult
                {
                    Rule = "TEST",
                    FilePath = "Test.cs",
                    LineNumber = 1,
                    Severity = Severity.Warning,
                    Message = "Fake",
                    AnalyzerType = AnalyzerType.Database
                }],
                impostorGuard);

            Assert.False(completedWithWrongToken);
        }

        // Worker A штатно сохраняет результаты через CompleteAsync под защитой guardA
        var findings = new List<AnalysisResult>
        {
            new()
            {
                Rule = "DB01",
                FilePath = "User.cs",
                LineNumber = 10,
                Severity = Severity.Warning,
                Message = "Table has no primary key.",
                AnalyzerType = AnalyzerType.Database
            }
        };

        await using (var ctxA = new AppDbContext(options))
        {
            var repoA = new AnalysisRepository(ctxA);
            var completed = await repoA.CompleteAsync(analysis.Id, tokenA, findings, guardA);
            Assert.True(completed);
        }

        // Worker A завершает работу и освобождает guard
        await guardA.DisposeAsync();

        // Проверяем, что результаты сохранены в БД, а статус перешёл в Completed
        await using (var verifyCtx = new AppDbContext(options))
        {
            var dbAnalysis = await verifyCtx.Analyses
                .Include(a => a.Results)
                .FirstOrDefaultAsync(a => a.Id == analysis.Id);

            Assert.NotNull(dbAnalysis);
            Assert.Equal(AnalysisStatus.Completed, dbAnalysis.Status);
            Assert.Null(dbAnalysis.LeaseExpiration);
            Assert.Single(dbAnalysis.Results);
            Assert.Equal("DB01", dbAnalysis.Results.First().Rule);
        }
    }

    [Fact]
    public async Task AdvisoryLock_Contention_ReturnsNotAcquiredInSingleProviderAttempt()
    {
        var token = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, token, DateTime.UtcNow.AddMinutes(5));

        using var exclusiveFileLock = _fileLockProvider.TryAcquireExclusive(analysis.Id);
        Assert.NotNull(exclusiveFileLock);

        var provider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        await using var guard = await provider.AcquireGuardAsync(analysis.Id, token);

        Assert.False(guard.IsAcquired);
        Assert.Equal(1, provider.AcquireAttemptCount);
        Assert.Equal(0, provider.PoolClearCount);
    }

    [Fact]
    public async Task AdvisoryLock_PermanentPostgresError_DoesNotRetryAndReleasesResources()
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = $"nonexistent_td003_{Guid.NewGuid():N}"
        };

        var fileLockProviderMock = new Mock<IWorkspaceFileLockProvider>();
        var provider = new PostgreSqlAnalysisLeaseGuardProvider(builder.ConnectionString, fileLockProviderMock.Object);

        var ex = await Assert.ThrowsAnyAsync<PostgresException>(
            () => provider.AcquireGuardAsync(9001, Guid.NewGuid()));

        Assert.Equal(PostgresErrorCodes.InvalidCatalogName, ex.SqlState);
        Assert.Equal(1, provider.AcquireAttemptCount);
        fileLockProviderMock.Verify(p => p.TryAcquireShared(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task AdvisoryLock_AuthenticationAndConfigurationErrors_DoNotRetry()
    {
        // 1. Ошибка аутентификации PostgreSQL (SQLSTATE 28P01 / 28000)
        var authBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Password = "invalid_td003_password"
        };

        var authProvider = new PostgreSqlAnalysisLeaseGuardProvider(authBuilder.ConnectionString, _fileLockProvider);
        var authEx = await Assert.ThrowsAnyAsync<PostgresException>(
            () => authProvider.AcquireGuardAsync(9002, Guid.NewGuid()));

        Assert.True(
            authEx.SqlState is PostgresErrorCodes.InvalidPassword or PostgresErrorCodes.InvalidAuthorizationSpecification,
            $"Unexpected auth SQLSTATE: {authEx.SqlState}");
        Assert.Equal(1, authProvider.AcquireAttemptCount);

        // 2. Ошибка конфигурации строки подключения
        var invalidConfigProvider = new PostgreSqlAnalysisLeaseGuardProvider("Host=", _fileLockProvider);
        await Assert.ThrowsAnyAsync<Exception>(
            () => invalidConfigProvider.AcquireGuardAsync(9003, Guid.NewGuid()));

        Assert.Equal(1, invalidConfigProvider.AcquireAttemptCount);
    }

    [Fact]
    public async Task AdvisoryLock_WhenLockStateIndeterminate_ClearsPoolReleasesResourcesAndDoesNotRetry()
    {
        var token = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, token, DateTime.UtcNow.AddMinutes(5));

        var provider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider)
        {
            LockCommandExecutorAsync = async (connection, id, ct) =>
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    SELECT pg_try_advisory_lock_shared(
                        hashtext('netscope_analysis_workspace'),
                        @analysisId);
                    """;
                cmd.Parameters.AddWithValue("analysisId", id);
                await cmd.ExecuteScalarAsync(ct);

                // Симулируем сбой после отправки lock-запроса до подтверждения результата провайдером
                throw new PostgresException(
                    "Simulated indeterminate advisory lock error",
                    "FATAL",
                    "FATAL",
                    PostgresErrorCodes.ConnectionException);
            }
        };

        await Assert.ThrowsAsync<PostgresException>(
            () => provider.AcquireGuardAsync(analysis.Id, token));

        Assert.Equal(1, provider.AcquireAttemptCount);
        Assert.Equal(1, provider.PoolClearCount);

        // Файловая и PostgreSQL-блокировки не остались захваченными
        var verifyProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        await using var cleanGuard = await verifyProvider.AcquireGuardAsync(analysis.Id, token);
        Assert.True(cleanGuard.IsAcquired);
        Assert.True(await cleanGuard.ValidateOwnershipAsync());
    }

    [Fact]
    public async Task StartupRecovery_ScenarioA_WorkerHoldsSharedLock_BlocksRecoveryUntilReleased()
    {
        var token = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, token, DateTime.UtcNow.AddMinutes(5));

        var provider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        var guard = await provider.AcquireGuardAsync(analysis.Id, token);
        Assert.True(guard.IsAcquired);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        // Пока воркер удерживает shared lock, Startup Recovery не может получить exclusive lock и не меняет статус
        await using (var recoveryCtx1 = new AppDbContext(options))
        {
            var repo1 = new AnalysisRepository(recoveryCtx1);
            var failedWhileLocked = await repo1.TryFailStartupWithoutGuardAsync(
                analysis.Id,
                token,
                "Should not fail while worker holds shared lock");
            Assert.False(failedWhileLocked);
        }

        await using (var checkCtx = new AppDbContext(options))
        {
            var row = await checkCtx.Analyses.AsNoTracking().SingleAsync(a => a.Id == analysis.Id);
            Assert.Equal(AnalysisStatus.Running, row.Status);
            Assert.Equal(token, row.ExecutionToken);
        }

        // После освобождения shared lock повторная попытка Startup Recovery успешно переводит запись в Failed
        await guard.DisposeAsync();

        await using (var recoveryCtx2 = new AppDbContext(options))
        {
            var repo2 = new AnalysisRepository(recoveryCtx2);
            var failedAfterRelease = await repo2.TryFailStartupWithoutGuardAsync(
                analysis.Id,
                token,
                "Startup failed after shared lock released");
            Assert.True(failedAfterRelease);
        }

        await using (var verifyCtx = new AppDbContext(options))
        {
            var row = await verifyCtx.Analyses.AsNoTracking().SingleAsync(a => a.Id == analysis.Id);
            Assert.Equal(AnalysisStatus.Failed, row.Status);
            Assert.Null(row.LeaseExpiration);
            Assert.Equal("Startup failed after shared lock released", row.ErrorMessage);
        }

        Assert.Equal(0L, await CountWorkspaceAdvisoryLocksAsync(analysis.Id));
    }

    [Fact]
    public async Task StartupRecovery_ScenarioB_RecoveryHoldsExclusiveLock_BlocksWorkerThenWorkerSeesFailed()
    {
        var token = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, token, DateTime.UtcNow.AddMinutes(5));

        var exclusiveLockAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRecoveryToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        await using var recoveryCtx = new AppDbContext(options);
        var repo = new AnalysisRepository(recoveryCtx)
        {
            SyncPointAfterStartupRecoveryLockAsync = async _ =>
            {
                exclusiveLockAcquired.TrySetResult();
                await allowRecoveryToFinish.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        };

        var recoveryTask = repo.TryFailStartupWithoutGuardAsync(
            analysis.Id,
            token,
            "Startup aborted under exclusive lock");

        await exclusiveLockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Пока Startup Recovery удерживает exclusive lock, воркер не может получить shared lock
        var provider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        await using (var blockedGuard = await provider.AcquireGuardAsync(analysis.Id, token))
        {
            Assert.False(blockedGuard.IsAcquired);
        }

        // Разрешаем Startup Recovery завершить UPDATE и освободить exclusive lock
        allowRecoveryToFinish.TrySetResult();
        Assert.True(await recoveryTask);
        Assert.Equal(0L, await CountWorkspaceAdvisoryLocksAsync(analysis.Id));

        // Теперь воркер может получить lock, но обязан увидеть статус Failed и отказаться от выполнения
        await using var workerGuard = await provider.AcquireGuardAsync(analysis.Id, token);
        Assert.True(workerGuard.IsAcquired);
        Assert.False(await workerGuard.ValidateOwnershipAsync([(int)AnalysisStatus.Running]));

        await using var checkCtx = new AppDbContext(options);
        var dbRow = await checkCtx.Analyses.AsNoTracking().SingleAsync(a => a.Id == analysis.Id);
        Assert.Equal(AnalysisStatus.Failed, dbRow.Status);
    }

    [Fact]
    public async Task StartupRecovery_ForeignTokenAndTerminalState_AreNotModified()
    {
        var actualToken = Guid.NewGuid();
        var foreignAttemptToken = Guid.NewGuid();
        var runningAnalysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, actualToken, DateTime.UtcNow.AddMinutes(5));

        var completedToken = Guid.NewGuid();
        var completedAnalysis = await CreateTestAnalysisAsync(AnalysisStatus.Completed, completedToken, null);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        await using (var ctx = new AppDbContext(options))
        {
            var repo = new AnalysisRepository(ctx);
            Assert.False(await repo.TryFailStartupWithoutGuardAsync(runningAnalysis.Id, foreignAttemptToken, "Foreign attempt"));
            Assert.False(await repo.TryFailStartupWithoutGuardAsync(completedAnalysis.Id, completedToken, "Already completed"));
        }

        await using (var verifyCtx = new AppDbContext(options))
        {
            var runningRow = await verifyCtx.Analyses.AsNoTracking().SingleAsync(a => a.Id == runningAnalysis.Id);
            Assert.Equal(AnalysisStatus.Running, runningRow.Status);
            Assert.Equal(actualToken, runningRow.ExecutionToken);

            var completedRow = await verifyCtx.Analyses.AsNoTracking().SingleAsync(a => a.Id == completedAnalysis.Id);
            Assert.Equal(AnalysisStatus.Completed, completedRow.Status);
            Assert.Equal(completedToken, completedRow.ExecutionToken);
        }

        Assert.Equal(0L, await CountWorkspaceAdvisoryLocksAsync(runningAnalysis.Id));
        Assert.Equal(0L, await CountWorkspaceAdvisoryLocksAsync(completedAnalysis.Id));
    }

    [Fact]
    public async Task StartupRecovery_WhenCancelled_ThrowsAndReleasesLocks()
    {
        var token = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, token, DateTime.UtcNow.AddMinutes(5));

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await using var ctx = new AppDbContext(options);
        var repo = new AnalysisRepository(ctx);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repo.TryFailStartupWithoutGuardAsync(analysis.Id, token, "Cancelled", cts.Token));

        await using var verifyCtx = new AppDbContext(options);
        var row = await verifyCtx.Analyses.AsNoTracking().SingleAsync(a => a.Id == analysis.Id);
        Assert.Equal(AnalysisStatus.Running, row.Status);
        Assert.Equal(0L, await CountWorkspaceAdvisoryLocksAsync(analysis.Id));
    }

    private async Task<long> CountWorkspaceAdvisoryLocksAsync(int analysisId)
    {
        await using var checkConnection = new NpgsqlConnection(fixture.ConnectionString);
        await checkConnection.OpenAsync();
        await using var command = checkConnection.CreateCommand();
        command.CommandText = """
            SELECT count(*)
            FROM pg_locks
            WHERE locktype = 'advisory'
              AND classid = hashtext('netscope_analysis_workspace')
              AND objid = @analysisId
              AND objsubid = 2
              AND granted = true;
            """;
        command.Parameters.AddWithValue("analysisId", analysisId);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
