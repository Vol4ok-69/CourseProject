using Application.DTOs;
using Application.Exceptions;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace Backend.Tests.Integration;

public sealed class PostgreSqlGitHubSyncIntegrationTests : IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlFixture _fixture;

    public PostgreSqlGitHubSyncIntegrationTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TR17_TwoIndependentDbContexts_RowLockingAndCheckpointReread_PreventsDuplicateSyncAndEnforcesSerialization()
    {
        await _fixture.ResetDatabaseAsync();

        var options1 = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        var options2 = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        int projectId;
        const string initialHead = "init_hash_11111111111111111111111111111111";

        // 1. Инициализируем проект с начальной контрольной точкой
        await using (var seedContext = new AppDbContext(options1))
        {
            var user = new User
            {
                Username = "concurrency_tester",
                PasswordHash = "hash",
                RoleId = 2
            };
            seedContext.Users.Add(user);
            await seedContext.SaveChangesAsync();

            var project = new Project
            {
                Name = "ConcurrentSyncProject",
                RepoUrl = "https://github.com/test/concurrent",
                OwnerId = user.Id,
                LastSyncedCommitHash = initialHead,
                LastSyncedBranch = "main",
                LastSyncedRepositoryId = "repo_999",
                LastSyncedRepositoryHost = "api.github.com",
                LastSyncedAt = DateTime.UtcNow.AddHours(-1)
            };
            seedContext.Projects.Add(project);

            var initCommit = new Commit
            {
                Project = project,
                CommitHash = initialHead,
                CommitDate = DateTime.UtcNow.AddHours(-1),
                ParentHashes = [],
                IsVerified = true
            };
            seedContext.Commits.Add(initCommit);

            await seedContext.SaveChangesAsync();
            projectId = project.Id;
        }

        // 2. Создаем два независимых экземпляра DbContext и репозиториев
        await using var context1 = new AppDbContext(options1);
        await using var context2 = new AppDbContext(options2);

        var repo1 = new CommitRepository(context1);
        var repo2 = new CommitRepository(context2);

        var snapshot = new ProjectCheckpointSnapshotDto(initialHead, "main", "repo_999", "api.github.com");

        const string headTx1 = "tx1_hash_22222222222222222222222222222222";
        const string headTx2 = "tx2_hash_33333333333333333333333333333333";

        var checkpoint1 = new ProjectCheckpointDto(headTx1, "main", "repo_999", "api.github.com", DateTime.UtcNow);
        var checkpoint2 = new ProjectCheckpointDto(headTx2, "main", "repo_999", "api.github.com", DateTime.UtcNow);

        var commitsTx1 = new List<Commit>
        {
            new() { CommitHash = headTx1, ParentHashes = [initialHead], CommitDate = DateTime.UtcNow }
        };

        var commitsTx2 = new List<Commit>
        {
            new() { CommitHash = headTx2, ParentHashes = [initialHead], CommitDate = DateTime.UtcNow }
        };

        // 3. Первый контекст открывает транзакцию и захватывает строковую блокировку SELECT ... FOR UPDATE
        await using var tx1 = await context1.Database.BeginTransactionAsync();
        await context1.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Projects\" WHERE \"Id\" = {projectId} FOR UPDATE");

        var conn1 = context1.Database.GetDbConnection();
        await using var pidCmd = conn1.CreateCommand();
        pidCmd.Transaction = context1.Database.CurrentTransaction?.GetDbTransaction();
        pidCmd.CommandText = "SELECT pg_backend_pid();";
        var blockingBackendPid = Convert.ToInt32(await pidCmd.ExecuteScalarAsync());

        // Фиксируем PID второго PostgreSQL backend.
        // Соединение context2 должно оставаться открытым до завершения конкурентной операции.
        await context2.Database.OpenConnectionAsync();
        var conn2 = context2.Database.GetDbConnection();
        await using var waitingPidCmd = conn2.CreateCommand();
        waitingPidCmd.CommandText = "SELECT pg_backend_pid();";
        var waitingBackendPid = Convert.ToInt32(await waitingPidCmd.ExecuteScalarAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<SyncBatchResult>? tx2Task = null;

        try
        {
            // 4. Запускаем второй контекст в фоновом потоке: он попытается захватить блокировку строки проекта,
            // но будет заблокирован в PostgreSQL на уровне ядра СУБД до завершения tx1.
            tx2Task = Task.Run(async () =>
            {
                return await repo2.SaveSyncBatchAsync(projectId, commitsTx2, [], checkpoint2, snapshot, cts.Token);
            }, cts.Token);

            // Детерминированное подтверждение ожидания блокировки через системный каталог pg_stat_activity и pg_blocking_pids
            await WaitForTransactionLockWaitAsync(
                _fixture.ConnectionString,
                blockingBackendPid,
                waitingBackendPid,
                TimeSpan.FromSeconds(15),
                cts.Token);

            // Второй поток гарантированно ожидает освобождения блокировки строки в ядре PostgreSQL именно процессом blockingBackendPid
            Assert.False(tx2Task.IsCompleted);

            // 5. Первый контекст сохраняет коммиты, обновляет checkpoint и коммитит транзакцию
            var result1 = await repo1.SaveSyncBatchAsync(projectId, commitsTx1, [], checkpoint1, snapshot, cts.Token);
            Assert.Contains(headTx1, result1.InsertedHashes);
            await tx1.CommitAsync(cts.Token);

            // 6. Второй поток разблокируется, перечитывает контрольную точку под блокировкой FOR NO KEY UPDATE,
            // обнаруживает изменение checkpoint и обязан выбросить SyncCheckpointChangedException.
            var ex = await Assert.ThrowsAsync<SyncCheckpointChangedException>(() => tx2Task);
            Assert.Contains("изменена другим процессом", ex.Message);
        }
        finally
        {
            if (tx2Task != null && !tx2Task.IsCompleted)
            {
                await cts.CancelAsync();
                try
                {
                    await tx2Task;
                }
                catch
                {
                    // Игнорируем исключение отмены в блоке очистки, сохраняя исходный результат теста
                }
            }
        }

        // 7. Проверяем состояние БД PostgreSQL: сохранен ровно один коммит tx1, а коммит tx2 отсутствует (откатан)
        await using (var verifyContext = new AppDbContext(options1))
        {
            var project = await verifyContext.Projects.FirstAsync(p => p.Id == projectId);
            Assert.Equal(headTx1, project.LastSyncedCommitHash);

            var dbCommits = await verifyContext.Commits.Where(c => c.ProjectId == projectId).ToListAsync();
            Assert.Equal(2, dbCommits.Count); // initialHead + headTx1
            Assert.Contains(dbCommits, c => c.CommitHash == initialHead);
            Assert.Contains(dbCommits, c => c.CommitHash == headTx1);
            Assert.DoesNotContain(dbCommits, c => c.CommitHash == headTx2);
        }
    }

    [Fact]
    public async Task PostgreSql_SaveSyncBatchAsync_AtomicRollback_WhenExceptionThrown_PreservesState()
    {
        await _fixture.ResetDatabaseAsync();

        const string initialHead = "init_atomic_head";
        const string existingCommitHash = "seed_existing_commit_1";
        const string newCommitHash = "new_commit_during_sync_2";

        var interceptor = new ControlledRollbackTransactionInterceptor();
        var testOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;

        var cleanOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        int projectId;

        // 1. Исходное состояние: проект и существующий неверифицированный коммит
        await using (var seedContext = new AppDbContext(cleanOptions))
        {
            var user = new User { Username = "atomic_tester", PasswordHash = "hash", RoleId = 2 };
            seedContext.Users.Add(user);
            await seedContext.SaveChangesAsync();

            var project = new Project
            {
                Name = "AtomicProject",
                RepoUrl = "https://github.com/test/atomic",
                OwnerId = user.Id,
                LastSyncedCommitHash = initialHead,
                LastSyncedBranch = "main",
                LastSyncedRepositoryId = "repo_atomic",
                LastSyncedRepositoryHost = "api.github.com",
                LastSyncedAt = DateTime.UtcNow
            };
            seedContext.Projects.Add(project);

            var seedCommit = new Commit
            {
                Project = project,
                CommitHash = existingCommitHash,
                CommitDate = DateTime.UtcNow.AddMinutes(-10),
                ParentHashes = ["seed_parent_old"],
                IsVerified = false
            };
            seedContext.Commits.Add(seedCommit);

            await seedContext.SaveChangesAsync();
            projectId = project.Id;
        }

        // 2. Выполняем операцию с перехватчиком, который позволяет SaveChangesAsync завершить реальную
        // запись в PostgreSQL (INSERT нового коммита, UPDATE существующего, UPDATE проекта), но перед
        // фиксацией транзакции инициирует контролируемый сбой (TransactionCommittingAsync).
        await using (var context = new AppDbContext(testOptions))
        {
            var repo = new CommitRepository(context);

            var snapshot = new ProjectCheckpointSnapshotDto(initialHead, "main", "repo_atomic", "api.github.com");
            var checkpoint = new ProjectCheckpointDto(newCommitHash, "main", "repo_atomic", "api.github.com", DateTime.UtcNow);

            var newCommits = new List<Commit>
            {
                new() { CommitHash = newCommitHash, ParentHashes = [existingCommitHash], CommitDate = DateTime.UtcNow }
            };
            var existingToUpdate = new List<CommitUpdateDto>
            {
                new(existingCommitHash, ["updated_parent_during_sync"])
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repo.SaveSyncBatchAsync(projectId, newCommits, existingToUpdate, checkpoint, snapshot));

            Assert.Contains("Controlled error right before transaction commit", ex.Message);
        }

        // 3. Проверяем состояние через отдельный независимый DbContext
        await using (var verifyContext = new AppDbContext(cleanOptions))
        {
            // Чекпоинт проекта не изменился
            var project = await verifyContext.Projects.FirstAsync(p => p.Id == projectId);
            Assert.Equal(initialHead, project.LastSyncedCommitHash);

            // Новый коммит отсутствует
            var newCommit = await verifyContext.Commits.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.CommitHash == newCommitHash);
            Assert.Null(newCommit);

            // Изменения существующего коммита откачены (не сохранены)
            var existingCommit = await verifyContext.Commits.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.CommitHash == existingCommitHash);
            Assert.NotNull(existingCommit);
            Assert.False(existingCommit.IsVerified);
            Assert.Equal(["seed_parent_old"], existingCommit.ParentHashes);

            // Общее количество коммитов в БД строго соответствует исходному состоянию
            var totalCount = await verifyContext.Commits.CountAsync(c => c.ProjectId == projectId);
            Assert.Equal(1, totalCount);
        }
    }

    [Fact]
    public async Task TR15_PostgreSql_SaveSyncBatchAsync_Unique23505_RecoversViaSavepointAndEnforcesVerification()
    {
        await _fixture.ResetDatabaseAsync();

        int projectId;
        const string initialHead = "init_head_23505";
        const string conflictingSha = "conflict_pg_23505";

        var seedOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        await using (var seedContext = new AppDbContext(seedOptions))
        {
            var user = new User { Username = "pg_23505_user", PasswordHash = "hash", RoleId = 2 };
            seedContext.Users.Add(user);
            await seedContext.SaveChangesAsync();

            var project = new Project
            {
                Name = "Pg23505Project",
                RepoUrl = "https://github.com/test/pg23505",
                OwnerId = user.Id,
                LastSyncedCommitHash = initialHead,
                LastSyncedBranch = "main",
                LastSyncedRepositoryId = "repo_pg23505",
                LastSyncedRepositoryHost = "api.github.com",
                LastSyncedAt = DateTime.UtcNow
            };
            seedContext.Projects.Add(project);
            await seedContext.SaveChangesAsync();
            projectId = project.Id;
        }

        var interceptor = new PostgreSqlCommitRaceInterceptor(conflictingSha, projectId, _fixture.ConnectionString);
        var testOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using var context = new AppDbContext(testOptions);
        var repo = new CommitRepository(context);

        var snapshot = new ProjectCheckpointSnapshotDto(initialHead, "main", "repo_pg23505", "api.github.com");
        var checkpoint = new ProjectCheckpointDto(conflictingSha, "main", "repo_pg23505", "api.github.com", DateTime.UtcNow);

        var newCommits = new List<Commit>
        {
            new() { CommitHash = conflictingSha, ParentHashes = ["parent_from_sync"], CommitDate = DateTime.UtcNow }
        };

        // В момент выборки commit отсутствует в БД, но интерцептор на независимом физическом соединении
        // параллельно вставляет его как unverified.
        // EF Core генерирует INSERT, PostgreSQL генерирует реальный 23505 (IX_Commits_ProjectId_CommitHash).
        // SaveSyncBatchAsync обязан откатить до savepoint, перечитать строку, перевести в IsVerified = true и успешно закоммитить.
        var result = await repo.SaveSyncBatchAsync(projectId, newCommits, [], checkpoint, snapshot);

        Assert.Empty(result.InsertedHashes);
        Assert.Contains(conflictingSha, result.UpdatedHashes);

        await using (var verifyContext = new AppDbContext(seedOptions))
        {
            var commit = await verifyContext.Commits.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.CommitHash == conflictingSha);
            Assert.NotNull(commit);
            Assert.True(commit.IsVerified);
            Assert.Equal(["parent_from_sync"], commit.ParentHashes);

            var project = await verifyContext.Projects.FirstAsync(p => p.Id == projectId);
            Assert.Equal(conflictingSha, project.LastSyncedCommitHash);
        }
    }

    [Fact]
    public async Task TR15_PostgreSql_SaveSyncBatchAsync_Repeated23505Conflict_RollsBackEntireTransactionAndPreservesState()
    {
        await _fixture.ResetDatabaseAsync();

        int projectId;
        const string initialHead = "init_head_rep23505";
        const string existingUnverifiedSha = "seed_unverified_existing_rep23505";
        const string conflictSha1 = "conflict_rep23505_sha1";
        const string conflictSha2 = "conflict_rep23505_sha2";
        var initialAt = DateTime.UtcNow.AddHours(-1);

        var seedOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        await using (var seedContext = new AppDbContext(seedOptions))
        {
            var user = new User { Username = "pg_rep23505_user", PasswordHash = "hash", RoleId = 2 };
            seedContext.Users.Add(user);
            await seedContext.SaveChangesAsync();

            var project = new Project
            {
                Name = "PgRepeated23505Project",
                RepoUrl = "https://github.com/test/pgrep23505",
                OwnerId = user.Id,
                LastSyncedCommitHash = initialHead,
                LastSyncedBranch = "main",
                LastSyncedRepositoryId = "repo_rep23505",
                LastSyncedRepositoryHost = "api.github.com",
                LastSyncedAt = initialAt
            };
            seedContext.Projects.Add(project);

            var initCommit = new Commit
            {
                Project = project,
                CommitHash = initialHead,
                CommitDate = initialAt,
                ParentHashes = [],
                IsVerified = true
            };
            seedContext.Commits.Add(initCommit);

            var seedUnverified = new Commit
            {
                Project = project,
                CommitHash = existingUnverifiedSha,
                CommitDate = initialAt,
                ParentHashes = ["seed_initial_parent"],
                IsVerified = false
            };
            seedContext.Commits.Add(seedUnverified);

            await seedContext.SaveChangesAsync();
            projectId = project.Id;
        }

        // Интерцептор, который вставляет conflictSha1 во время первой выборки,
        // и conflictSha2 во время второй выборки (в фазе восстановления),
        // провоцируя повторный сбой 23505 на втором SaveChangesAsync.
        var interceptor = new PostgreSqlRepeatedCommitRaceInterceptor(conflictSha1, conflictSha2, projectId, _fixture.ConnectionString);
        var testOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using (var context = new AppDbContext(testOptions))
        {
            var repo = new CommitRepository(context);

            var snapshot = new ProjectCheckpointSnapshotDto(initialHead, "main", "repo_rep23505", "api.github.com");
            var checkpoint = new ProjectCheckpointDto(conflictSha2, "main", "repo_rep23505", "api.github.com", DateTime.UtcNow);

            var newCommits = new List<Commit>
            {
                new() { CommitHash = conflictSha1, ParentHashes = ["app_parent_1"], CommitDate = DateTime.UtcNow },
                new() { CommitHash = conflictSha2, ParentHashes = ["app_parent_2"], CommitDate = DateTime.UtcNow }
            };
            var existingToUpdate = new List<CommitUpdateDto>
            {
                new(existingUnverifiedSha, ["updated_by_app_parent"])
            };

            // Повторный сбой 23505 не должен маскироваться или приводить к частичной фиксации:
            // исключение обязано быть выброшено, а родительская транзакция – полностью откачена.
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                repo.SaveSyncBatchAsync(projectId, newCommits, existingToUpdate, checkpoint, snapshot));

            Assert.Equal(2, interceptor.TriggerCount);
        }

        // Проверяем состояние через независимый DbContext без интерцептора
        await using (var verifyContext = new AppDbContext(seedOptions))
        {
            var project = await verifyContext.Projects
                .AsNoTracking()
                .FirstAsync(p => p.Id == projectId);

            // Контрольная точка не изменилась
            Assert.Equal(initialHead, project.LastSyncedCommitHash);
            Assert.Equal("main", project.LastSyncedBranch);
            Assert.Equal("repo_rep23505", project.LastSyncedRepositoryId);
            Assert.Equal("api.github.com", project.LastSyncedRepositoryHost);

            // Существовавший проверенный коммит остался нетронутым
            var initCommit = await verifyContext.Commits
                .AsNoTracking()
                .FirstAsync(c => c.ProjectId == projectId && c.CommitHash == initialHead);
            Assert.True(initCommit.IsVerified);

            // Существовавший непроверенный коммит НЕ получил обновление из неуспешной пачки
            var unverifiedCommit = await verifyContext.Commits
                .AsNoTracking()
                .FirstAsync(c => c.ProjectId == projectId && c.CommitHash == existingUnverifiedSha);
            Assert.False(unverifiedCommit.IsVerified);
            Assert.Equal(["seed_initial_parent"], unverifiedCommit.ParentHashes);

            // Коммиты, созданные независимым соединением интерцептора, существуют как unverified с пустыми родителями
            var conflictCommits = await verifyContext.Commits
                .AsNoTracking()
                .Where(c => c.ProjectId == projectId && (c.CommitHash == conflictSha1 || c.CommitHash == conflictSha2))
                .ToDictionaryAsync(c => c.CommitHash);

            Assert.Contains(conflictSha1, conflictCommits.Keys);
            Assert.Contains(conflictSha2, conflictCommits.Keys);

            var c1 = conflictCommits[conflictSha1];
            var c2 = conflictCommits[conflictSha2];

            Assert.False(c1.IsVerified);
            Assert.False(c2.IsVerified);
            Assert.Empty(c1.ParentHashes);
            Assert.Empty(c2.ParentHashes);

            // Общее количество коммитов: 2 исходных + 2 независимых от интерцептора = 4
            var totalCount = await verifyContext.Commits.CountAsync(c => c.ProjectId == projectId);
            Assert.Equal(4, totalCount);
        }
    }

    private static async Task WaitForTransactionLockWaitAsync(
        string connectionString,
        int blockingBackendPid,
        int waitingBackendPid,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var connection = new Npgsql.NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();

            command.CommandText = """
                SELECT EXISTS
                (
                    SELECT 1
                    FROM pg_stat_activity AS waiting
                    WHERE waiting.pid = @waitingBackendPid
                      AND waiting.wait_event_type = 'Lock'
                      AND waiting.datname = current_database()
                      AND @blockingBackendPid = ANY(pg_blocking_pids(waiting.pid))
                );
                """;

            command.Parameters.AddWithValue("waitingBackendPid", waitingBackendPid);
            command.Parameters.AddWithValue("blockingBackendPid", blockingBackendPid);

            var isBlockedByExpectedTransaction =
                (bool)(await command.ExecuteScalarAsync(cancellationToken))!;

            if (isBlockedByExpectedTransaction)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        throw new TimeoutException(
            $"PostgreSQL backend PID {waitingBackendPid} did not enter a lock wait blocked by PID {blockingBackendPid} within {timeout}.");
    }

    private sealed class ControlledRollbackTransactionInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> TransactionCommittingAsync(
            System.Data.Common.DbTransaction transaction,
            Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Controlled error right before transaction commit.");
        }
    }

    private sealed class PostgreSqlRepeatedCommitRaceInterceptor(string conflictSha1, string conflictSha2, int projectId, string connectionString)
        : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private int _selectCount;

        public int TriggerCount => _selectCount;

        public override async ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData,
            System.Data.Common.DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase) && command.CommandText.Contains("Commits", StringComparison.OrdinalIgnoreCase))
            {
                if (_selectCount == 0)
                {
                    _selectCount = 1;
                    await using var raceConn = new Npgsql.NpgsqlConnection(connectionString);
                    await raceConn.OpenAsync(cancellationToken);
                    await using var insertCmd = raceConn.CreateCommand();
                    insertCmd.CommandText = $"INSERT INTO \"Commits\" (\"ProjectId\", \"CommitHash\", \"CommitDate\", \"ParentHashes\", \"IsVerified\") VALUES ({projectId}, '{conflictSha1}', NOW(), '{{}}', FALSE);";
                    await insertCmd.ExecuteNonQueryAsync(cancellationToken);
                }
                else if (_selectCount == 1)
                {
                    _selectCount = 2;
                    await using var raceConn = new Npgsql.NpgsqlConnection(connectionString);
                    await raceConn.OpenAsync(cancellationToken);
                    await using var insertCmd = raceConn.CreateCommand();
                    insertCmd.CommandText = $"INSERT INTO \"Commits\" (\"ProjectId\", \"CommitHash\", \"CommitDate\", \"ParentHashes\", \"IsVerified\") VALUES ({projectId}, '{conflictSha2}', NOW(), '{{}}', FALSE);";
                    await insertCmd.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class PostgreSqlCommitRaceInterceptor(string conflictSha, int projectId, string connectionString) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private bool _triggered;

        public override async ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData,
            System.Data.Common.DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!_triggered && command.CommandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase) && command.CommandText.Contains("Commits", StringComparison.OrdinalIgnoreCase))
            {
                _triggered = true;
                await using var raceConn = new Npgsql.NpgsqlConnection(connectionString);
                await raceConn.OpenAsync(cancellationToken);
                await using var insertCmd = raceConn.CreateCommand();
                insertCmd.CommandText = $"INSERT INTO \"Commits\" (\"ProjectId\", \"CommitHash\", \"CommitDate\", \"ParentHashes\", \"IsVerified\") VALUES ({projectId}, '{conflictSha}', NOW(), '{{}}', FALSE);";
                await insertCmd.ExecuteNonQueryAsync(cancellationToken);
            }
            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }
}
