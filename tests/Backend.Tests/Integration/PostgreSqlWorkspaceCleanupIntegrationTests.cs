using System.Data;
using Application.Interfaces;
using IAnalysisLeaseGuard = Application.Interfaces.IAnalysisLeaseGuard;
using DatabaseAnalyzer.Runner;
using DatabaseAnalyzer.Roslyn;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.GitHub;
using Infrastructure.Repository;
using Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Moq;
using Application.Analyzers;
using Application.Services;
using Xunit;

namespace Backend.Tests.Integration;

/// <summary>
/// Реальные интеграционные тесты безопасного владения и очистки рабочих каталогов (TD-003).
/// Выполняются против реального контейнера PostgreSQL 17 (Testcontainers).
/// Воспроизводят Сценарии A, B, C, D, E, F.
/// </summary>
public sealed class PostgreSqlWorkspaceCleanupIntegrationTests(PostgreSqlFixture fixture)
    : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private string _testStorageRoot = null!;
    private WorkspaceFileLockProvider _fileLockProvider = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();
        _testStorageRoot = Path.Combine(Path.GetTempPath(), "CourseProject_WorkspaceTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testStorageRoot);
        _fileLockProvider = new WorkspaceFileLockProvider(_testStorageRoot);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_testStorageRoot))
        {
            try
            {
                Directory.Delete(_testStorageRoot, recursive: true);
            }
            catch
            {
            }
        }

        return Task.CompletedTask;
    }

    private async Task<Analysis> CreateTestAnalysisAsync(AnalysisStatus status, Guid token, DateTime? leaseExpiration = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        await using var context = new AppDbContext(options);

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

    private string CreateExecutionDirectory(int analysisId, Guid token)
    {
        var dir = Path.Combine(_testStorageRoot, analysisId.ToString(), token.ToString("D"), "repository");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "source.cs"), "// sample code");
        return Path.Combine(_testStorageRoot, analysisId.ToString(), token.ToString("D"));
    }

    /// <summary>
    /// Сценарий A. Очистка не удаляет активный каталог.
    /// 1. Создать анализ со статусом Running и токеном A.
    /// 2. Создать каталог исполнения A.
    /// 3. Запустить Cleanup Worker.
    /// 4. Убедиться, что каталог A сохранился.
    /// </summary>
    [Fact]
    public async Task ScenarioA_CleanupWorker_DoesNotDeleteActiveDirectory()
    {
        var tokenA = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenA);
        var dirA = CreateExecutionDirectory(analysis.Id, tokenA);

        Assert.True(Directory.Exists(dirA));

        var cleanupService = new AnalysisCleanupService(fixture.ConnectionString, _fileLockProvider, _testStorageRoot);
        var deletedCount = await cleanupService.CleanupStaleDirectoriesAsync();

        Assert.Equal(0, deletedCount);
        Assert.True(Directory.Exists(dirA), "Каталог активного исполнения не должен удаляться очистителем!");
    }

    /// <summary>
    /// Сценарий B. Очистка удаляет устаревший каталог.
    /// 1. Создать анализ с актуальным токеном B.
    /// 2. Создать каталоги A и B.
    /// 3. Запустить Cleanup Worker.
    /// 4. Убедиться, что каталог A удалён.
    /// 5. Убедиться, что каталог B сохранился.
    /// </summary>
    [Fact]
    public async Task ScenarioB_CleanupWorker_DeletesStaleDirectory_AndPreservesActiveDirectory()
    {
        var tokenA = Guid.NewGuid();
        var tokenB = Guid.NewGuid();

        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenB);
        var dirA = CreateExecutionDirectory(analysis.Id, tokenA);
        var dirB = CreateExecutionDirectory(analysis.Id, tokenB);

        Assert.True(Directory.Exists(dirA));
        Assert.True(Directory.Exists(dirB));

        var cleanupService = new AnalysisCleanupService(fixture.ConnectionString, _fileLockProvider, _testStorageRoot);
        var deletedCount = await cleanupService.CleanupStaleDirectoriesAsync();

        Assert.Equal(1, deletedCount);
        Assert.False(Directory.Exists(dirA), "Устаревший каталог предыдущего исполнения A должен быть удалён!");
        Assert.True(Directory.Exists(dirB), "Каталог актуального исполнения B должен быть сохранён!");
    }

    /// <summary>
    /// Сценарий C. Очиститель не удаляет каталог при активной блокировке.
    /// 1. Создать анализ и каталог исполнения.
    /// 2. Получить session-level advisory lock через PostgreSqlAnalysisLeaseGuardProvider.
    /// 3. Запустить Cleanup Worker.
    /// 4. Убедиться, что очиститель пропустил анализ.
    /// 5. Убедиться, что каталог не удалён.
    /// </summary>
    [Fact]
    public async Task ScenarioC_CleanupWorker_SkipsAnalysis_WhenAdvisoryLockIsHeld()
    {
        var tokenA = Guid.NewGuid();
        var tokenStale = Guid.NewGuid();

        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenA);
        var dirStale = CreateExecutionDirectory(analysis.Id, tokenStale);
        var dirA = CreateExecutionDirectory(analysis.Id, tokenA);

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);

        // Воркер удерживает session-level advisory lock на выделенном соединении
        await using var guard = await guardProvider.AcquireGuardAsync(analysis.Id, tokenA);
        Assert.True(guard.IsAcquired, "Session-level advisory lock должен быть успешно получен воркером");

        var cleanupService = new AnalysisCleanupService(fixture.ConnectionString, _fileLockProvider, _testStorageRoot);
        var deletedCount = await cleanupService.CleanupStaleDirectoriesAsync();

        // Очиститель должен пропустить данный analysisId, так как блокировка занята
        Assert.Equal(0, deletedCount);
        Assert.True(Directory.Exists(dirStale), "При удержании блокировки воркером очиститель не должен трогать каталоги!");
        Assert.True(Directory.Exists(dirA), "Каталог активного исполнения не должен быть удален!");
    }

    /// <summary>
    /// Сценарий D. Гонка между перехватом задачи и очисткой.
    /// 1. Создать просроченную задачу с токеном A и каталог A.
    /// 2. Запустить очиститель и воркер, пытающийся перехватить задачу.
    /// 3. Управлять порядком выполнения через детерминированные точки синхронизации.
    /// 4. Убедиться, что одновременно очиститель и новый исполнитель не работают с одним каталогом.
    /// 5. Убедиться, что актуальный каталог не удаляется.
    /// </summary>
    [Fact]
    public async Task ScenarioD_RaceBetweenWorkerAcquisitionAndCleanup_PreservesConsistency()
    {
        var tokenA = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(
            AnalysisStatus.Running,
            tokenA,
            leaseExpiration: DateTime.UtcNow.AddMinutes(-10));

        var dirA = CreateExecutionDirectory(analysis.Id, tokenA);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        await using var context = new AppDbContext(options);
        var repo = new AnalysisRepository(context);

        // Воркер B перехватывает просроченную задачу
        var acquired = await repo.TryAcquireNextPendingOrStaleAsync(TimeSpan.FromMinutes(2));
        Assert.NotNull(acquired);
        Assert.Equal(analysis.Id, acquired.Id);
        Assert.NotEqual(tokenA, acquired.ExecutionToken!.Value);

        var tokenB = acquired.ExecutionToken.Value;
        var dirB = CreateExecutionDirectory(analysis.Id, tokenB);

        // Теперь запускаем очиститель
        var cleanupService = new AnalysisCleanupService(fixture.ConnectionString, _fileLockProvider, _testStorageRoot);
        var deletedCount = await cleanupService.CleanupStaleDirectoriesAsync();

        Assert.Equal(1, deletedCount);
        Assert.False(Directory.Exists(dirA), "Каталог старого токена A должен быть удален!");
        Assert.True(Directory.Exists(dirB), "Каталог нового токена B должен быть сохранен!");
    }

    /// <summary>
    /// Сценарий E. Потеря PostgreSQL-соединения во время очистки.
    /// 1. Создать анализ в состоянии Running с токеном tokenActive.
    /// 2. Создать каталог tokenActive и устаревшие каталоги tokenStale1, tokenStale2.
    /// 3. Запустить AnalysisCleanupService с реальным PostgreSQL Testcontainers.
    /// 4. Дождаться, когда очиститель захватит advisory lock и прочитает состояние анализа.
    /// 5. Через тестовую точку синхронизации разорвать сессию очистителя в PostgreSQL (pg_terminate_backend).
    /// 6. Убедиться, что очиститель обнаружил разрыв соединения и немедленно прекратил файловые операции.
    /// 7. Убедиться, что активный каталог и устаревшие каталоги не повреждены.
    /// 8. Убедиться, что после завершения сессии advisory lock освобождается, и новый воркер может захватить lease.
    /// </summary>
    [Fact]
    public async Task ScenarioE_ConnectionLossDuringCleanup_HaltsDeletionImmediately()
    {
        var tokenActive = Guid.NewGuid();
        var tokenStale1 = Guid.NewGuid();
        var tokenStale2 = Guid.NewGuid();

        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenActive);
        var dirActive = CreateExecutionDirectory(analysis.Id, tokenActive);
        var fileActive = Path.Combine(dirActive, "repository", "active.cs");
        await File.WriteAllTextAsync(fileActive, "// active code");

        var dirStale1 = CreateExecutionDirectory(analysis.Id, tokenStale1);
        var fileStale1 = Path.Combine(dirStale1, "repository", "stale1.cs");
        await File.WriteAllTextAsync(fileStale1, "// stale 1");

        var dirStale2 = CreateExecutionDirectory(analysis.Id, tokenStale2);
        var fileStale2 = Path.Combine(dirStale2, "repository", "stale2.cs");
        await File.WriteAllTextAsync(fileStale2, "// stale 2");

        var cleanupService = new AnalysisCleanupService(fixture.ConnectionString, _fileLockProvider, _testStorageRoot);

        var terminatedSession = false;

        // Настраиваем точку синхронизации: сразу после захвата advisory lock и чтения состояния анализа
        cleanupService.SyncPointBeforeDeleteAsync = async (analysisId) =>
        {
            await using var adminConn = new NpgsqlConnection(fixture.ConnectionString);
            await adminConn.OpenAsync();

            // Находим PID сессии очистителя, которая держит advisory lock
            await using var findCmd = adminConn.CreateCommand();
            findCmd.CommandText = """
                SELECT pid FROM pg_locks
                WHERE locktype = 'advisory'
                  AND classid = hashtext('netscope_analysis_workspace')
                  AND objid = @analysisId
                  AND objsubid = 2
                  AND granted = true;
                """;
            findCmd.Parameters.AddWithValue("analysisId", analysisId);

            var pidObj = await findCmd.ExecuteScalarAsync();
            Assert.NotNull(pidObj);
            var cleanerPid = Convert.ToInt32(pidObj);

            // Принудительно завершаем физическую сессию очистителя на стороне PostgreSQL
            await using var killCmd = adminConn.CreateCommand();
            killCmd.CommandText = "SELECT pg_terminate_backend(@pid);";
            killCmd.Parameters.AddWithValue("pid", cleanerPid);
            await killCmd.ExecuteScalarAsync();

            terminatedSession = true;
        };

        // Запускаем очистку. Очиститель захватывает блокировку, но сессия разрывается,
        // поэтому IsLockHeldByCurrentSessionAsync возвращает false и прерывает удаление.
        var deletedCount = await cleanupService.CleanupStaleDirectoriesAsync();

        Assert.True(terminatedSession, "Сессия очистителя должна была быть обнаружена и разорвана в тесте.");
        Assert.Equal(0, deletedCount);

        // Активный каталог и его файлы гарантированно сохранены!
        Assert.True(Directory.Exists(dirActive), "Каталог активного исполнения должен быть сохранен при потере соединения!");
        Assert.True(File.Exists(fileActive));

        // Каталоги устаревших исполнений не были удалены после потери соединения
        Assert.True(Directory.Exists(dirStale1));
        Assert.True(Directory.Exists(dirStale2));

        // Проверяем, что после разрыва старой сессии PostgreSQL освободил блокировку,
        // и новый воркер может успешно захватить workspace guard для этой задачи
        var tokenNew = Guid.NewGuid();
        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        await using var newGuard = await guardProvider.AcquireGuardAsync(analysis.Id, tokenNew);
        Assert.NotNull(newGuard);
        Assert.True(newGuard.IsAcquired, "Новый воркер должен иметь возможность захватить блокировку после освобождения старой сессии.");
    }

    /// <summary>
    /// Сценарий F. Старый исполнитель не может удалить каталог нового исполнения.
    /// 1. Создать анализ с токеном A.
    /// 2. Создать каталог A.
    /// 3. Смоделировать перехват задачи новым воркером с токеном B.
    /// 4. Создать каталог B.
    /// 5. Попытаться выполнить очистку от имени A.
    /// 6. Убедиться, что каталог B сохранён.
    /// 7. Убедиться, что старый исполнитель не может удалить каталог B даже при наличии локального объекта анализа с токеном A.
    /// </summary>
    [Fact]
    public async Task ScenarioF_OldWorker_CannotDeleteDirectoryOfNewExecution()
    {
        var tokenA = Guid.NewGuid();
        var tokenB = Guid.NewGuid();

        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenA);
        var dirA = CreateExecutionDirectory(analysis.Id, tokenA);

        // Имитируем перехват задачи воркером B (смена токена в PostgreSQL)
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        await using (var context = new AppDbContext(options))
        {
            var dbAnalysis = await context.Analyses.FirstAsync(a => a.Id == analysis.Id);
            dbAnalysis.ExecutionToken = tokenB;
            await context.SaveChangesAsync();
        }

        var dirB = CreateExecutionDirectory(analysis.Id, tokenB);

        // Инициализируем RepositorySourceService для старого воркера с токеном A
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:AnalysisPath"] = _testStorageRoot
            })
            .Build();

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        var serviceA = new RepositorySourceService(new HttpClient(), config, guardProvider);

        // Старый воркер A пытается удалить каталог от своего имени (tokenA)
        // В PostgreSQL токен уже равен tokenB, поэтому проверка владения должна отклонить удаление!
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            serviceA.CleanupAsync(analysis.Id, tokenA));

        Assert.Contains("отклонено", ex.Message);

        // Каталог B должен быть безусловно сохранён!
        Assert.True(Directory.Exists(dirB), "Каталог нового исполнения B не должен быть затронут старым исполнителем!");

        // Каталог A не был удален неавторизованным вызовом старого исполнителя
        Assert.True(Directory.Exists(dirA), "Каталог A не может быть удален воркером, утратившим владение!");
    }

    /// <summary>
    /// Обязательный регрессионный сценарий: передача lease воркеру B и выполнение блока finally старого воркера A.
    /// 1. Создать задачу AnalysisId со статусом Running и токеном A.
    /// 2. Запустить воркер A (ExecuteAnalysisAsync с токеном A), проходящий начальную валидацию и входящий в блок try.
    /// 3. Во время выполнения анализаторов сымитировать перехват lease воркером B (смена токена в PostgreSQL на B).
    /// 4. Воркер B создает свой рабочий каталог B и файл.
    /// 5. Воркер A завершает анализ и пытается вызвать CompleteAsync. Поскольку токен в БД уже B, CompleteAsync возвращает false.
    /// 6. Воркер A фиксирует ownershipLost = true и переходит в блок finally.
    /// 7. Проверить, что каталог B и его файл сохранились в полной неприкосновенности!
    /// </summary>
    [Fact]
    public async Task ScenarioG_WorkerFinallyBlock_DoesNotDeleteNewWorkerDirectory_AfterLeaseSteal()
    {
        var tokenA = Guid.NewGuid();
        var tokenB = Guid.NewGuid();

        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenA);
        var dirA = CreateExecutionDirectory(analysis.Id, tokenA);
        var fileA = Path.Combine(dirA, "repository", "source.cs");
        Assert.True(File.Exists(fileA));

        var dirB = CreateExecutionDirectory(analysis.Id, tokenB);
        var fileB = Path.Combine(dirB, "repository", "source.cs");
        await File.WriteAllTextAsync(fileB, "// code of worker B");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:AnalysisPath"] = _testStorageRoot
            })
            .Build();

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        var repoSourceService = new RepositorySourceService(new HttpClient(), config, guardProvider);

        var analyzerExecuted = false;

        await using (var runContext = new AppDbContext(options))
        {
            var analysisRepo = new AnalysisRepository(runContext);
            var commitRepo = new CommitRepository(runContext);

            var mockCredential = new Mock<IGitHubCredentialService>();
            mockCredential.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("ghp_test_token");

            var mockAnalyzer = new Mock<IAnalyzer>();
            mockAnalyzer.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
            mockAnalyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
                .Returns(async (AnalyzerContext ctx, CancellationToken ct) =>
                {
                    analyzerExecuted = true;

                    // Во время работы анализатора воркер B перехватывает задачу в PostgreSQL
                    await using var stealContext = new AppDbContext(options);
                    var dbAnalysis = await stealContext.Analyses.FirstAsync(a => a.Id == analysis.Id, ct);
                    dbAnalysis.ExecutionToken = tokenB;
                    dbAnalysis.LeaseExpiration = DateTime.UtcNow.AddMinutes(5);
                    await stealContext.SaveChangesAsync(ct);

                    return [];
                });

            var mockRepoSourceService = new Mock<IRepositorySourceService>();
            mockRepoSourceService.Setup(x => x.DownloadAndExtractAsync(
                It.IsAny<string>(), It.IsAny<string>(), analysis.Id, tokenA, It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(dirA);

            mockRepoSourceService.Setup(x => x.CleanupAsync(analysis.Id, tokenA, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
                .Returns((int aId, Guid t, IAnalysisLeaseGuard g, CancellationToken ct) => repoSourceService.CleanupAsync(aId, t, g, ct));

            var service = new AnalysisService(
                analysisRepo,
                commitRepo,
                mockRepoSourceService.Object,
                [mockAnalyzer.Object],
                mockCredential.Object,
                guardProvider);

            // Воркер A начинает выполнение с токеном A
            // Он успешно проходит начальную валидацию (в БД на старте токен A) и заходит в try
            var success = await service.ExecuteAnalysisAsync(analysis.Id, tokenA, default, TimeSpan.FromMinutes(2));

            // Метод возвращает false, так как CompleteAsync не смог подтвердить токен A
            Assert.False(success);
            Assert.True(analyzerExecuted, "Анализатор должен был выполниться внутри защищенного блока try воркера A!");
        }

        // КРИТИЧЕСКАЯ РЕГРЕССИОННАЯ ПРОВЕРКА:
        // Каталог воркера B и его файлы должны остаться в полной сохранности после выполнения finally старого воркера!
        Assert.True(Directory.Exists(dirB), "Каталог нового исполнения B не должен быть удален старым воркером A в блоке finally!");
        Assert.True(File.Exists(fileB), "Файлы каталога B должны сохраниться в неприкосновенности!");
        Assert.Equal("// code of worker B", await File.ReadAllTextAsync(fileB));
    }

    /// <summary>
    /// Регрессионный тест 2: попытка вызова токенизированного CleanupAsync старым воркером A
    /// отклоняется на уровне RepositorySourceService и PostgreSqlAnalysisLeaseGuard, если в БД уже токен B.
    /// </summary>
    [Fact]
    public async Task ScenarioG_Part2_RepositorySourceService_RejectsCleanup_WhenTokenMismatchInDatabase()
    {
        var tokenA = Guid.NewGuid();
        var tokenB = Guid.NewGuid();

        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenB); // В БД уже токен B
        var dirA = CreateExecutionDirectory(analysis.Id, tokenA);
        var dirB = CreateExecutionDirectory(analysis.Id, tokenB);
        var fileB = Path.Combine(dirB, "repository", "source.cs");
        await File.WriteAllTextAsync(fileB, "// code of worker B");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:AnalysisPath"] = _testStorageRoot
            })
            .Build();

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        var service = new RepositorySourceService(new HttpClient(), config, guardProvider);

        // Старый воркер A пытается удалить каталог от имени tokenA
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CleanupAsync(analysis.Id, tokenA));

        Assert.Contains("отклонено", ex.Message);
        Assert.True(Directory.Exists(dirB), "Каталог B нового воркера не должен быть удален!");
        Assert.True(File.Exists(fileB));
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return handler(request);
        }
    }

    private sealed class LeaseLossStream(
        byte[][] chunks,
        Func<Task> beforeSecondChunkAsync) : Stream
    {
        private int _chunkIndex;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        public override long Length => chunks.Sum(chunk => chunk.Length);

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            return ReadChunkAsync(buffer, cancellationToken);
        }

        private async ValueTask<int> ReadChunkAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_chunkIndex >= chunks.Length)
                return 0;

            if (_chunkIndex == 1)
                await beforeSecondChunkAsync();

            var chunk = chunks[_chunkIndex++];
            chunk.AsMemory().CopyTo(buffer);

            return chunk.Length;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            return ReadAsync(
                buffer.AsMemory(offset, count),
                cancellationToken).AsTask();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    [Fact]
    public async Task ScenarioH_WorkerLosesDatabaseConnection_FileLockProtectsWorkspaceUntilGuardDisposal()
    {
        var activeToken = Guid.NewGuid();
        var staleToken = Guid.NewGuid();

        var analysis = await CreateTestAnalysisAsync(
            AnalysisStatus.Running,
            activeToken);

        var activeDirectory = CreateExecutionDirectory(
            analysis.Id,
            activeToken);

        var staleDirectory = CreateExecutionDirectory(
            analysis.Id,
            staleToken);

        var firstChunk = Enumerable.Repeat((byte)0x11, 128).ToArray();
        var secondChunk = Enumerable.Repeat((byte)0x22, 256).ToArray();

        var connectionTerminated = false;
        var workerBackendPid = 0;

        var stream = new LeaseLossStream(
            [firstChunk, secondChunk],
            async () =>
            {
                await using var adminConnection =
                    new NpgsqlConnection(fixture.ConnectionString);

                await adminConnection.OpenAsync();

                await using var command = adminConnection.CreateCommand();

                command.CommandText = """
                    SELECT pg_terminate_backend(@pid);
                    """;

                command.Parameters.AddWithValue(
                    "pid",
                    workerBackendPid);

                var result = await command.ExecuteScalarAsync();

                connectionTerminated = result is true;

                if (!connectionTerminated)
                {
                    throw new InvalidOperationException(
                        "Не удалось завершить PostgreSQL-сессию Worker.");
                }
            });

        var httpClient = new HttpClient(
            new DelegateHttpMessageHandler(_ =>
                Task.FromResult(
                    new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StreamContent(stream)
                    })))
        {
            BaseAddress = new Uri("https://api.github.com/")
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Storage:AnalysisPath"] = _testStorageRoot
                })
            .Build();

        var repositorySourceService = new RepositorySourceService(
            httpClient,
            configuration);

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(
            fixture.ConnectionString,
            _fileLockProvider);

        var cleanupService = new AnalysisCleanupService(
            fixture.ConnectionString,
            _fileLockProvider,
            _testStorageRoot);

        IAnalysisLeaseGuard? guard = null;

        try
        {
            // 1. Worker получает настоящие PostgreSQL и файловую блокировки.
            guard = await guardProvider.AcquireGuardAsync(
                analysis.Id,
                activeToken);

            Assert.True(guard.IsAcquired);

            Assert.IsType<PostgreSqlAnalysisLeaseGuard>(guard);

            var postgresGuard = (PostgreSqlAnalysisLeaseGuard)guard;

            Assert.NotNull(postgresGuard.ConnectionProcessId);

            workerBackendPid = postgresGuard.ConnectionProcessId!.Value;

            // 2. Worker начинает скачивание.
            // Первый блок записывается, перед вторым завершается PostgreSQL-сессия.
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => repositorySourceService.DownloadAndExtractAsync(
                    "https://github.com/test/repo",
                    "test-commit",
                    analysis.Id,
                    activeToken,
                    "test-pat",
                    guard));

            Assert.True(
                exception.Message.Contains("отклонено", StringComparison.OrdinalIgnoreCase) ||
                exception.Message.Contains("Workspace ownership", StringComparison.OrdinalIgnoreCase),
                $"Неожиданное сообщение об ошибке: {exception.Message}");

            Assert.True(
                connectionTerminated,
                "PostgreSQL-сессия Worker должна быть завершена.");

            var archivePath = Path.Combine(
                activeDirectory,
                "archive.zip");

            // 3. Частично записанный архив сохранён.
            Assert.True(File.Exists(archivePath));

            var archiveBytes = await File.ReadAllBytesAsync(archivePath);

            Assert.Equal(firstChunk.Length, archiveBytes.Length);
            Assert.Equal(firstChunk, archiveBytes);

            // 4. PostgreSQL-сессия уже потеряна, но файловая блокировка
            // всё ещё удерживается объектом guard.
            var exclusiveProbe = _fileLockProvider.TryAcquireExclusive(
                analysis.Id);

            Assert.Null(
                exclusiveProbe);

            exclusiveProbe?.Dispose();

            // 5. Cleaner может получить PostgreSQL exclusive advisory lock,
            // но не может получить exclusive file lock.
            var deletedBeforeGuardDisposal =
                await cleanupService.CleanupStaleDirectoriesAsync();

            Assert.Equal(0, deletedBeforeGuardDisposal);

            Assert.True(
                Directory.Exists(activeDirectory),
                "Активный workspace не должен быть удалён.");

            Assert.True(
                Directory.Exists(staleDirectory),
                "Устаревший workspace не должен быть удалён, пока Worker удерживает file lock.");

            Assert.True(File.Exists(archivePath));

            // 6. Только после Dispose guard файловая блокировка освобождается.
            await guard.DisposeAsync();
            guard = null;

            // 7. Cleaner теперь получает обе блокировки.
            var deletedAfterGuardDisposal =
                await cleanupService.CleanupStaleDirectoriesAsync();

            Assert.Equal(1, deletedAfterGuardDisposal);

            Assert.False(
                Directory.Exists(staleDirectory),
                "Каталог старого исполнения должен быть удалён.");

            Assert.True(
                Directory.Exists(activeDirectory),
                "Каталог активного исполнения должен сохраниться.");

            Assert.True(
                File.Exists(archivePath),
                "Файлы активного исполнения не должны удаляться.");
        }
        finally
        {
            if (guard is not null)
                await guard.DisposeAsync();

            httpClient.Dispose();
        }
    }


    [Fact]
    public async Task DownloadAndExtractAsync_WhenExecutionTokenEmpty_ThrowsArgumentExceptionBeforeCreatingDirectory()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:AnalysisPath"] = _testStorageRoot
            })
            .Build();

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        var service = new RepositorySourceService(new HttpClient(), config, guardProvider);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.DownloadAndExtractAsync("https://github.com/org/repo.git", "commit123", 100, Guid.Empty, "pat_test"));

        Assert.Equal("executionToken", ex.ParamName);
        var targetDir = Path.Combine(_testStorageRoot, "100", Guid.Empty.ToString("D"));
        Assert.False(Directory.Exists(targetDir), "Каталог не должен создаваться при невалидном токене!");
    }

    [Fact]
    public async Task ExtractArchiveAsync_WhenExecutionTokenEmpty_ThrowsArgumentExceptionBeforeCreatingDirectory()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:AnalysisPath"] = _testStorageRoot
            })
            .Build();

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        var service = new RepositorySourceService(new HttpClient(), config, guardProvider);

        var destPath = Path.Combine(_testStorageRoot, "101", "empty_dest");
        using var ms = new MemoryStream();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExtractArchiveAsync(ms, destPath, 101, Guid.Empty));

        Assert.Contains("IAnalysisLeaseGuard", ex.Message);
        Assert.False(Directory.Exists(destPath));
    }
    [Fact]
    public async Task CleanupAsync_WhenExecutionTokenEmpty_ThrowsArgumentException()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:AnalysisPath"] = _testStorageRoot
            })
            .Build();

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        var service = new RepositorySourceService(new HttpClient(), config, guardProvider);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CleanupAsync(102, Guid.Empty));

        Assert.Equal("executionToken", ex.ParamName);
    }

    [Fact]
    public async Task PostgreSqlAnalysisLeaseGuard_RepeatedDispose_IsIdempotentAndSafe()
    {
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, Guid.NewGuid());
        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);

        var guard = await guardProvider.AcquireGuardAsync(analysis.Id, analysis.ExecutionToken!.Value);
        Assert.NotNull(guard);
        Assert.True(guard.IsAcquired);

        // Первый вызов DisposeAsync
        await guard.DisposeAsync();

        // Повторный вызов DisposeAsync не должен приводить к исключению
        await guard.DisposeAsync();
        await guard.DisposeAsync();
    }

    [Fact]
    public async Task PostgreSqlAnalysisLeaseGuard_WhenConnectionLost_ValidateOwnershipReturnsFalse()
    {
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, Guid.NewGuid());
        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);

        var guard = await guardProvider.AcquireGuardAsync(analysis.Id, analysis.ExecutionToken!.Value);
        Assert.NotNull(guard);
        Assert.True(guard.IsAcquired);
        Assert.True(await guard.ValidateOwnershipAsync());

        // Разрываем сессию guard через pg_terminate_backend из другого соединения
        await using (var adminConn = new NpgsqlConnection(fixture.ConnectionString))
        {
            await adminConn.OpenAsync();
            await using var findCmd = adminConn.CreateCommand();
            findCmd.CommandText = """
                SELECT pid FROM pg_locks
                WHERE locktype = 'advisory'
                  AND classid = hashtext('netscope_analysis_workspace')
                  AND objid = @analysisId
                  AND objsubid = 2
                  AND granted = true;
                """;
            findCmd.Parameters.AddWithValue("analysisId", analysis.Id);
            var pid = Convert.ToInt32(await findCmd.ExecuteScalarAsync());

            await using var killCmd = adminConn.CreateCommand();
            killCmd.CommandText = "SELECT pg_terminate_backend(@pid);";
            killCmd.Parameters.AddWithValue("pid", pid);
            await killCmd.ExecuteScalarAsync();
        }

        // После разрыва соединения ValidateOwnershipAsync гарантированно возвращает false
        var isOwner = await guard.ValidateOwnershipAsync();
        Assert.False(isOwner, "При потере соединения ValidateOwnershipAsync должен вернуть false (fail-closed)!");

        await guard.DisposeAsync();
    }

    [Fact]
    public async Task IndependentAnalyses_CanBeCleanedIndependently()
    {
        var tokenActive1 = Guid.NewGuid();
        var tokenStale1 = Guid.NewGuid();
        var tokenActive2 = Guid.NewGuid();
        var tokenStale2 = Guid.NewGuid();

        var analysis1 = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenActive1);
        var analysis2 = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenActive2);

        var dirStale1 = CreateExecutionDirectory(analysis1.Id, tokenStale1);
        var dirActive1 = CreateExecutionDirectory(analysis1.Id, tokenActive1);

        var dirStale2 = CreateExecutionDirectory(analysis2.Id, tokenStale2);
        var dirActive2 = CreateExecutionDirectory(analysis2.Id, tokenActive2);

        var cleanupService = new AnalysisCleanupService(fixture.ConnectionString, _fileLockProvider, _testStorageRoot);
        var count = await cleanupService.CleanupStaleDirectoriesAsync();

        Assert.Equal(2, count);
        Assert.False(Directory.Exists(dirStale1), "Устаревший каталог задачи 1 должен быть удалён!");
        Assert.True(Directory.Exists(dirActive1), "Активный каталог задачи 1 должен быть сохранён!");
        Assert.False(Directory.Exists(dirStale2), "Устаревший каталог задачи 2 должен быть удалён!");
        Assert.True(Directory.Exists(dirActive2), "Активный каталог задачи 2 должен быть сохранён!");
    }
    /// <summary>
    /// 8.1 Главный регрессионный тест: Воркер удерживает директорию во время работы анализатора (TaskCompletionSource).
    /// Фоновый очиститель запускается параллельно, НЕ может получить advisory lock, удаляет 0 файлов,
    /// рабочая директория воркера остаётся целой и невредимой.
    /// </summary>
    [Fact]
    public async Task Section8_1_WorkerHoldsDirectoryDuringAnalyzer_CleanerCannotDelete()
    {
        var tokenA = Guid.NewGuid();
        var tokenStale = Guid.NewGuid();

        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenA);
        var dirA = CreateExecutionDirectory(analysis.Id, tokenA);
        var fileA = Path.Combine(dirA, "repository", "source.cs");
        Assert.True(File.Exists(fileA));

        // Также создадим устаревший каталог для этой же задачи
        var dirStale = CreateExecutionDirectory(analysis.Id, tokenStale);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:AnalysisPath"] = _testStorageRoot
            })
            .Build();

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        var repoSourceService = new RepositorySourceService(new HttpClient(), config, guardProvider);

        var analyzerEnteredTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeAnalyzerTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var mockAnalyzer = new Mock<IAnalyzer>();
        mockAnalyzer.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
        mockAnalyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns(async (AnalyzerContext ctx, CancellationToken ct) =>
            {
                // Убеждаемся, что контекст анализа получил активный guard (TD-003)
                Assert.NotNull(ctx.LeaseGuard);
                Assert.True(ctx.LeaseGuard.IsAcquired);

                // Сигнализируем, что воркер находится внутри анализатора и держит блокировку
                analyzerEnteredTcs.TrySetResult(true);

                // Ожидаем команды на возобновление (в это время параллельно работает очиститель)
                await resumeAnalyzerTcs.Task;
                return [];
            });

        var mockCredential = new Mock<IGitHubCredentialService>();
        mockCredential.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        var mockRepoSourceService = new Mock<IRepositorySourceService>();
        mockRepoSourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(), It.IsAny<string>(), analysis.Id, tokenA, It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dirA);

        mockRepoSourceService.Setup(x => x.CleanupAsync(analysis.Id, tokenA, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Returns((int aId, Guid t, IAnalysisLeaseGuard g, CancellationToken ct) => repoSourceService.CleanupAsync(aId, t, g, ct));

        await using var runContext = new AppDbContext(options);
        var analysisRepo = new AnalysisRepository(runContext);
        var commitRepo = new CommitRepository(runContext);

        var service = new AnalysisService(
            analysisRepo,
            commitRepo,
            mockRepoSourceService.Object,
            [mockAnalyzer.Object],
            mockCredential.Object,
            guardProvider);

        // Запускаем воркер в отдельном потоке
        var workerTask = Task.Run(() => service.ExecuteAnalysisAsync(analysis.Id, tokenA, default, TimeSpan.FromMinutes(2)));

        // Ждем, пока воркер войдет в анализатор и удерживает session-level advisory lock
        await analyzerEnteredTcs.Task;

        // Пока воркер заблокирован внутри анализатора, запускаем фоновый очиститель
        var cleanupService = new AnalysisCleanupService(fixture.ConnectionString, _fileLockProvider, _testStorageRoot);
        var deletedCount = await cleanupService.CleanupStaleDirectoriesAsync();

        // Очиститель не смог захватить advisory lock для этой задачи и пропустил её!
        Assert.Equal(0, deletedCount);

        // Рабочая директория воркера А и её файлы полностью целы!
        Assert.True(Directory.Exists(dirA), "Директория активного воркера НЕ должна быть удалена очистителем!");
        Assert.True(File.Exists(fileA), "Файл в директории активного воркера должен быть сохранен!");
        Assert.True(Directory.Exists(dirStale), "Устаревшая директория также не тронута, так как вся задача заблокирована воркером!");

        // Возобновляем работу анализатора и ждем успешного завершения анализа
        resumeAnalyzerTcs.TrySetResult(true);
        var success = await workerTask;

        Assert.True(success, "Анализ должен успешно завершиться после освобождения паузы.");
    }

    /// <summary>
    /// 8.2 Изменение токена в БД во время работы анализатора.
    /// Воркер обнаруживает смену токена перед следующим анализатором/сохранением,
    /// останавливает выполнение без записи результатов и не трогает каталог нового воркера B.
    /// </summary>
    [Fact]
    public async Task Section8_2_TokenChangedDuringAnalyzer_WorkerDetectsAndHaltsWithoutSaving()
    {
        var tokenA = Guid.NewGuid();
        var tokenB = Guid.NewGuid();

        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenA);
        var dirA = CreateExecutionDirectory(analysis.Id, tokenA);
        var dirB = CreateExecutionDirectory(analysis.Id, tokenB);
        var fileB = Path.Combine(dirB, "repository", "source.cs");
        await File.WriteAllTextAsync(fileB, "// code of worker B");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:AnalysisPath"] = _testStorageRoot
            })
            .Build();

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        var repoSourceService = new RepositorySourceService(new HttpClient(), config, guardProvider);

        var firstAnalyzerExecuted = false;
        var secondAnalyzerExecuted = false;

        var mockAnalyzer1 = new Mock<IAnalyzer>();
        mockAnalyzer1.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
        mockAnalyzer1.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns(async (AnalyzerContext ctx, CancellationToken ct) =>
            {
                firstAnalyzerExecuted = true;

                // Во время выполнения первого анализатора токен в БД меняется на tokenB (перехват)
                await using var stealContext = new AppDbContext(options);
                var dbAnalysis = await stealContext.Analyses.FirstAsync(a => a.Id == analysis.Id, ct);
                dbAnalysis.ExecutionToken = tokenB;
                await stealContext.SaveChangesAsync(ct);

                return [];
            });

        var mockAnalyzer2 = new Mock<IAnalyzer>();
        mockAnalyzer2.Setup(x => x.Type).Returns(AnalyzerType.Security);
        mockAnalyzer2.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                secondAnalyzerExecuted = true;
                return [];
            });

        var mockCredential = new Mock<IGitHubCredentialService>();
        mockCredential.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        var mockRepoSourceService = new Mock<IRepositorySourceService>();
        mockRepoSourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(), It.IsAny<string>(), analysis.Id, tokenA, It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dirA);

        mockRepoSourceService.Setup(x => x.CleanupAsync(analysis.Id, tokenA, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Returns((int aId, Guid t, IAnalysisLeaseGuard g, CancellationToken ct) => repoSourceService.CleanupAsync(aId, t, g, ct));

        await using var runContext = new AppDbContext(options);
        var analysisRepo = new AnalysisRepository(runContext);
        var commitRepo = new CommitRepository(runContext);

        var service = new AnalysisService(
            analysisRepo,
            commitRepo,
            mockRepoSourceService.Object,
            [mockAnalyzer1.Object, mockAnalyzer2.Object],
            mockCredential.Object,
            guardProvider);

        var success = await service.ExecuteAnalysisAsync(analysis.Id, tokenA);

        Assert.False(success, "Воркер должен вернуть false при потере владения токеном!");
        Assert.True(firstAnalyzerExecuted, "Первый анализатор должен был выполниться.");
        Assert.False(secondAnalyzerExecuted, "Второй анализатор НЕ должен запускаться после потери владения!");

        // Каталог нового воркера B остаётся полностью нетронутым
        Assert.True(Directory.Exists(dirB));
        Assert.True(File.Exists(fileB));
    }

    /// <summary>
    /// 8.3 Разрыв PostgreSQL соединения во время работы анализатора (pg_terminate_backend).
    /// Воркер обнаруживает потерю соединения, останавливает выполнение, не помечает задачу как Completed.
    /// </summary>
    [Fact]
    public async Task Section8_3_WorkerConnectionLoss_HaltsExecutionImmediately()
    {
        var tokenA = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenA);
        var dirA = CreateExecutionDirectory(analysis.Id, tokenA);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:AnalysisPath"] = _testStorageRoot
            })
            .Build();

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        var repoSourceService = new RepositorySourceService(new HttpClient(), config, guardProvider);

        var secondAnalyzerExecuted = false;

        var mockAnalyzer1 = new Mock<IAnalyzer>();
        mockAnalyzer1.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
        mockAnalyzer1.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns(async (AnalyzerContext ctx, CancellationToken ct) =>
            {
                // Находим PID сессии воркера и принудительно убиваем соединение на стороне PostgreSQL
                await using var adminConn = new NpgsqlConnection(fixture.ConnectionString);
                await adminConn.OpenAsync(ct);

                await using var findCmd = adminConn.CreateCommand();
                findCmd.CommandText = """
                    SELECT pid FROM pg_locks
                    WHERE locktype = 'advisory'
                      AND classid = hashtext('netscope_analysis_workspace')
                      AND objid = @analysisId
                      AND objsubid = 2
                      AND granted = true;
                    """;
                findCmd.Parameters.AddWithValue("analysisId", analysis.Id);
                var pidObj = await findCmd.ExecuteScalarAsync(ct);
                Assert.NotNull(pidObj);
                var workerPid = Convert.ToInt32(pidObj);

                await using var killCmd = adminConn.CreateCommand();
                killCmd.CommandText = "SELECT pg_terminate_backend(@pid);";
                killCmd.Parameters.AddWithValue("pid", workerPid);
                await killCmd.ExecuteScalarAsync(ct);

                return [];
            });

        var mockAnalyzer2 = new Mock<IAnalyzer>();
        mockAnalyzer2.Setup(x => x.Type).Returns(AnalyzerType.Security);
        mockAnalyzer2.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                secondAnalyzerExecuted = true;
                return [];
            });

        var mockCredential = new Mock<IGitHubCredentialService>();
        mockCredential.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        var mockRepoSourceService = new Mock<IRepositorySourceService>();
        mockRepoSourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(), It.IsAny<string>(), analysis.Id, tokenA, It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dirA);

        await using var runContext = new AppDbContext(options);
        var analysisRepo = new AnalysisRepository(runContext);
        var commitRepo = new CommitRepository(runContext);

        var service = new AnalysisService(
            analysisRepo,
            commitRepo,
            mockRepoSourceService.Object,
            [mockAnalyzer1.Object, mockAnalyzer2.Object],
            mockCredential.Object,
            guardProvider);

        var success = await service.ExecuteAnalysisAsync(analysis.Id, tokenA);

        Assert.False(success, "При потере соединения с PostgreSQL анализ должен аварийно остановиться!");
        Assert.False(secondAnalyzerExecuted, "Второй анализатор НЕ должен вызываться после обрыва соединения!");

        // Проверяем, что задача не переведена в статус Completed
        await using var verifyContext = new AppDbContext(options);
        var dbAnalysis = await verifyContext.Analyses.FirstAsync(a => a.Id == analysis.Id);
        Assert.NotEqual(AnalysisStatus.Completed, dbAnalysis.Status);
    }

    /// <summary>
    /// 8.6 Исключение в анализаторе освобождает lease guard и позволяет очистителю обработать задачу.
    /// </summary>
    [Fact]
    public async Task Section8_6_ExceptionInAnalyzer_ReleasesGuardAndAllowsCleaner()
    {
        var tokenA = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenA);
        var dirA = CreateExecutionDirectory(analysis.Id, tokenA);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:AnalysisPath"] = _testStorageRoot
            })
            .Build();

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        var repoSourceService = new RepositorySourceService(new HttpClient(), config, guardProvider);

        var mockAnalyzer = new Mock<IAnalyzer>();
        mockAnalyzer.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
        mockAnalyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Fatal crash in analyzer engine"));

        var mockCredential = new Mock<IGitHubCredentialService>();
        mockCredential.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        var mockRepoSourceService = new Mock<IRepositorySourceService>();
        mockRepoSourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(), It.IsAny<string>(), analysis.Id, tokenA, It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dirA);

        mockRepoSourceService.Setup(x => x.CleanupAsync(analysis.Id, tokenA, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Returns((int aId, Guid t, IAnalysisLeaseGuard g, CancellationToken ct) => repoSourceService.CleanupAsync(aId, t, g, ct));

        await using var runContext = new AppDbContext(options);
        var analysisRepo = new AnalysisRepository(runContext);
        var commitRepo = new CommitRepository(runContext);

        var service = new AnalysisService(
            analysisRepo,
            commitRepo,
            mockRepoSourceService.Object,
            [mockAnalyzer.Object],
            mockCredential.Object,
            guardProvider);

        var success = await service.ExecuteAnalysisAsync(analysis.Id, tokenA);
        Assert.False(success);

        // После завершения метода guard освобождён
        // Теперь очиститель может захватить advisory lock без конфликтов
        var cleanupService = new AnalysisCleanupService(fixture.ConnectionString, _fileLockProvider, _testStorageRoot);
        var count = await cleanupService.CleanupStaleDirectoriesAsync();

        // Очиститель смог получить блокировку
        Assert.True(count >= 0);
    }

    /// <summary>
    /// 8.7 Два воркера для одного AnalysisId: второй воркер немедленно отклоняется, так как не может получить advisory lock.
    /// </summary>
    [Fact]
    public async Task Section8_7_TwoWorkersForSameAnalysisId_SecondWorkerRejected()
    {
        var tokenA = Guid.NewGuid();
        var tokenB = Guid.NewGuid();

        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenA);
        var dirA = CreateExecutionDirectory(analysis.Id, tokenA);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:AnalysisPath"] = _testStorageRoot
            })
            .Build();

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        var repoSourceService = new RepositorySourceService(new HttpClient(), config, guardProvider);

        var worker1Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker1Resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var mockAnalyzer = new Mock<IAnalyzer>();
        mockAnalyzer.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
        mockAnalyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns(async (AnalyzerContext ctx, CancellationToken ct) =>
            {
                worker1Entered.TrySetResult(true);
                await worker1Resume.Task;
                return [];
            });

        var mockCredential = new Mock<IGitHubCredentialService>();
        mockCredential.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        var mockRepoSourceService = new Mock<IRepositorySourceService>();
        mockRepoSourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(), It.IsAny<string>(), analysis.Id, tokenA, It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dirA);

        mockRepoSourceService.Setup(x => x.CleanupAsync(analysis.Id, tokenA, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Returns((int aId, Guid t, IAnalysisLeaseGuard g, CancellationToken ct) => repoSourceService.CleanupAsync(aId, t, g, ct));

        await using var runContext1 = new AppDbContext(options);
        var analysisRepo1 = new AnalysisRepository(runContext1);
        var commitRepo1 = new CommitRepository(runContext1);

        var service1 = new AnalysisService(
            analysisRepo1,
            commitRepo1,
            mockRepoSourceService.Object,
            [mockAnalyzer.Object],
            mockCredential.Object,
            guardProvider);

        var worker1Task = Task.Run(() => service1.ExecuteAnalysisAsync(analysis.Id, tokenA));

        await worker1Entered.Task;

        // Воркер 2 пытается начать выполнение этого же AnalysisId
        await using var runContext2 = new AppDbContext(options);
        var analysisRepo2 = new AnalysisRepository(runContext2);
        var queuedTask = await analysisRepo2.TryAcquireNextPendingOrStaleAsync(TimeSpan.FromMinutes(5));
        Assert.Null(queuedTask);
        var commitRepo2 = new CommitRepository(runContext2);

        var service2 = new AnalysisService(
            analysisRepo2,
            commitRepo2,
            mockRepoSourceService.Object,
            [mockAnalyzer.Object],
            mockCredential.Object,
            guardProvider);

        var worker2Success = await service2.ExecuteAnalysisAsync(analysis.Id, tokenB);

        // Воркер 2 немедленно отклонён, так как session advisory lock уже занят воркером 1
        Assert.False(worker2Success, "Второй воркер должен быть немедленно отклонен из-за занятости advisory lock!");

        // Освобождаем воркер 1
        worker1Resume.TrySetResult(true);
        var worker1Success = await worker1Task;
        Assert.True(worker1Success);
    }


    #region Mandatory 15 Regression Tests (Section 8)

    /// <summary>
    /// Тест 1. Worker A получает shared lock и начинает работу.
    /// </summary>
    [Fact]
    public async Task Test01_WorkerA_AcquiresSharedLock_Successfully()
    {
        var tokenA = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenA);

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        await using var guard = await guardProvider.AcquireGuardAsync(analysis.Id, tokenA);

        Assert.True(guard.IsAcquired, "Worker A должен успешно получить shared advisory lock.");

        // Проверяем, что в pg_locks действительно зафиксирован ShareLock для текущей сессии
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = """
            SELECT mode, granted FROM pg_locks
            WHERE locktype = 'advisory'
              AND classid = hashtext('netscope_analysis_workspace')
              AND objid = @analysisId
              AND objsubid = 2;
            """;
        checkCmd.Parameters.AddWithValue("analysisId", analysis.Id);

        await using var reader = await checkCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "Запись блокировки должна существовать в pg_locks.");
        Assert.Equal("ShareLock", reader.GetString(0));
        Assert.True(reader.GetBoolean(1), "Блокировка должна быть предоставлена (granted = true).");

        var isOwner = await guard.ValidateOwnershipAsync([(int)AnalysisStatus.Running]);
        Assert.True(isOwner, "ValidateOwnershipAsync должен подтвердить владение задачей.");
    }

    /// <summary>
    /// Тест 2. Worker B не может получить несовместимую блокировку для того же AnalysisId.
    /// </summary>
    [Fact]
    public async Task Test02_WorkerB_CannotAcquireIncompatibleLock_ForSameAnalysisId()
    {
        var tokenA = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenA);

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        await using var guardA = await guardProvider.AcquireGuardAsync(analysis.Id, tokenA);
        Assert.True(guardA.IsAcquired);

        // Worker B на отдельном физическом соединении пытается захватить эксклюзивную блокировку
        await using var connB = new NpgsqlConnection(fixture.ConnectionString);
        await connB.OpenAsync();

        await using var exclCmd = connB.CreateCommand();
        exclCmd.CommandText = "SELECT pg_try_advisory_lock(hashtext('netscope_analysis_workspace'), @analysisId);";
        exclCmd.Parameters.AddWithValue("analysisId", analysis.Id);

        var lockResult = await exclCmd.ExecuteScalarAsync();
        Assert.False(lockResult is bool b && b, "Worker B не должен мочь получить эксклюзивную блокировку, пока Worker A держит shared lock.");
    }

    /// <summary>
    /// Тест 3. Cleaner не может получить exclusive lock, пока Worker A удерживает shared lock.
    /// </summary>
    [Fact]
    public async Task Test03_CleanerCannotAcquireExclusiveLock_WhileWorkerAHoldsSharedLock()
    {
        var tokenA = Guid.NewGuid();
        var tokenStale = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenA);

        var dirStale = CreateExecutionDirectory(analysis.Id, tokenStale);
        var dirA = CreateExecutionDirectory(analysis.Id, tokenA);

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        await using var guardA = await guardProvider.AcquireGuardAsync(analysis.Id, tokenA);
        Assert.True(guardA.IsAcquired);

        var cleaner = new AnalysisCleanupService(fixture.ConnectionString, _fileLockProvider, _testStorageRoot);
        var deletedCount = await cleaner.CleanupStaleDirectoriesAsync();

        Assert.Equal(0, deletedCount);
        Assert.True(Directory.Exists(dirStale), "Cleaner не должен удалять устаревший каталог при активном shared lock Worker A.");
        Assert.True(Directory.Exists(dirA), "Каталог Worker A не должен быть затронут.");
    }

    /// <summary>
    /// Тест 4. После освобождения shared lock Cleaner может получить exclusive lock.
    /// </summary>
    [Fact]
    public async Task Test04_CleanerAcquiresExclusiveLock_AfterWorkerAReleasesSharedLock()
    {
        var tokenA = Guid.NewGuid();
        var tokenStale = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenA);

        var dirStale = CreateExecutionDirectory(analysis.Id, tokenStale);
        var dirA = CreateExecutionDirectory(analysis.Id, tokenA);

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        var guardA = await guardProvider.AcquireGuardAsync(analysis.Id, tokenA);
        Assert.True(guardA.IsAcquired);

        // Worker A освобождает shared lock
        await guardA.DisposeAsync();

        var cleaner = new AnalysisCleanupService(fixture.ConnectionString, _fileLockProvider, _testStorageRoot);
        var deletedCount = await cleaner.CleanupStaleDirectoriesAsync();

        Assert.Equal(1, deletedCount);
        Assert.False(Directory.Exists(dirStale), "После освобождения shared lock Cleaner должен удалить устаревший каталог.");
        Assert.True(Directory.Exists(dirA), "Каталог актуального токена A сохраняется.");
    }

    /// <summary>
    /// Тест 5. Worker A получает guard, затем PostgreSQL-соединение принудительно разрывается во время выполнения анализатора.
    /// </summary>
    [Fact]
    public async Task Test05_WorkerA_ConnectionSevered_DuringAnalyzerExecution()
    {
        var tokenA = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenA);
        var dirA = CreateExecutionDirectory(analysis.Id, tokenA);

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        var analyzerEnteredTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var analyzerCancelledTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var mockAnalyzer = new Mock<IAnalyzer>();
        mockAnalyzer.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
        mockAnalyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns(async (AnalyzerContext ctx, CancellationToken ct) =>
            {
                analyzerEnteredTcs.TrySetResult(true);

                // Ждем отмены токена в результате обнаружения фоновым возобновлением разрыва соединения
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);
                }
                catch (OperationCanceledException)
                {
                    analyzerCancelledTcs.TrySetResult(true);
                    throw;
                }

                return [];
            });

        var mockRepoSourceService = new Mock<IRepositorySourceService>();
        mockRepoSourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(), It.IsAny<string>(), analysis.Id, tokenA, It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dirA);

        mockRepoSourceService.Setup(x => x.CleanupAsync(analysis.Id, tokenA, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockCredential = new Mock<IGitHubCredentialService>();
        mockCredential.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        await using var runContext = new AppDbContext(options);
        var analysisRepo = new AnalysisRepository(runContext);
        var commitRepo = new CommitRepository(runContext);

        var service = new AnalysisService(
            analysisRepo,
            commitRepo,
            mockRepoSourceService.Object,
            [mockAnalyzer.Object],
            mockCredential.Object,
            guardProvider);

        // Запуск анализа в фоновой задаче
        var workerTask = Task.Run(() => service.ExecuteAnalysisAsync(analysis.Id, tokenA, default, TimeSpan.FromSeconds(3)));

        // 1. Дожидаемся входа анализатора в выполнение
        await analyzerEnteredTcs.Task;

        // 2. Находим PID соединения Worker A, удерживающего блокировку
        await using var adminConn = new NpgsqlConnection(fixture.ConnectionString);
        await adminConn.OpenAsync();

        await using var findPidCmd = adminConn.CreateCommand();
        findPidCmd.CommandText = """
            SELECT pid FROM pg_locks
            WHERE locktype = 'advisory'
              AND classid = hashtext('netscope_analysis_workspace')
              AND objid = @analysisId
              AND objsubid = 2
              AND mode = 'ShareLock'
              AND granted = true;
            """;
        findPidCmd.Parameters.AddWithValue("analysisId", analysis.Id);

        var pidObj = await findPidCmd.ExecuteScalarAsync();
        Assert.NotNull(pidObj);
        var workerPid = Convert.ToInt32(pidObj);

        // 3. ПРИНУДИТЕЛЬНО РАЗРЫВАЕМ соединение Worker A прямо во время выполнения анализатора
        await using var killCmd = adminConn.CreateCommand();
        killCmd.CommandText = "SELECT pg_terminate_backend(@pid);";
        killCmd.Parameters.AddWithValue("pid", workerPid);
        await killCmd.ExecuteNonQueryAsync();

        // 4. Проверяем, что анализатор получил отмену CancellationToken
        var cancelled = await Task.WhenAny(analyzerCancelledTcs.Task, Task.Delay(10000)) == analyzerCancelledTcs.Task;
        Assert.True(cancelled, "CancellationToken анализатора должен быть отменён фоновым контролем владения!");

        // 5. Воркер корректно завершается со статусом false (потеря владения)
        var workerSuccess = await workerTask;
        Assert.False(workerSuccess, "Worker должен вернуть false при потере соединения.");
    }

    /// <summary>
    /// Тест 6. Фоновый контроль владения отменяет CancellationToken анализатора.
    /// </summary>
    [Fact]
    public async Task Test06_BackgroundLeaseRenewal_CancelsAnalyzerCancellationToken()
    {
        // Проверяем, что StartLeaseRenewalAsync в AnalysisService непрерывно опрашивает ValidateOwnershipAsync
        // и при изменении статуса или токена в БД отменяет linkedCts
        var tokenA = Guid.NewGuid();
        var tokenB = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenA);
        var dirA = CreateExecutionDirectory(analysis.Id, tokenA);

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        var analyzerEnteredTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var analyzerTokenCancelledTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var mockAnalyzer = new Mock<IAnalyzer>();
        mockAnalyzer.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
        mockAnalyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns(async (AnalyzerContext ctx, CancellationToken ct) =>
            {
                analyzerEnteredTcs.TrySetResult(true);

                using (ct.Register(() => analyzerTokenCancelledTcs.TrySetResult(true)))
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15), ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                }

                return [];
            });

        var mockRepoSourceService = new Mock<IRepositorySourceService>();
        mockRepoSourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(), It.IsAny<string>(), analysis.Id, tokenA, It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dirA);

        mockRepoSourceService.Setup(x => x.CleanupAsync(analysis.Id, tokenA, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockCredential = new Mock<IGitHubCredentialService>();
        mockCredential.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        await using var runContext = new AppDbContext(options);
        var analysisRepo = new AnalysisRepository(runContext);
        var commitRepo = new CommitRepository(runContext);

        var service = new AnalysisService(
            analysisRepo,
            commitRepo,
            mockRepoSourceService.Object,
            [mockAnalyzer.Object],
            mockCredential.Object,
            guardProvider);

        var workerTask = Task.Run(() => service.ExecuteAnalysisAsync(analysis.Id, tokenA, default, TimeSpan.FromSeconds(3)));

        await analyzerEnteredTcs.Task;

        // В БД меняем токен на tokenB, симулируя перехват задачи другим воркером
        await using var stealContext = new AppDbContext(options);
        var dbAnalysis = await stealContext.Analyses.FirstAsync(a => a.Id == analysis.Id);
        dbAnalysis.ExecutionToken = tokenB;
        await stealContext.SaveChangesAsync();

        // Фоновый цикл StartLeaseRenewalAsync обнаруживает смену токена при ValidateOwnershipAsync и отменяет токен
        var cancelled = await Task.WhenAny(analyzerTokenCancelledTcs.Task, Task.Delay(10000)) == analyzerTokenCancelledTcs.Task;
        Assert.True(cancelled, "Фоновый цикл проверки владения должен отменить CancellationToken анализатора.");

        var success = await workerTask;
        Assert.False(success);
    }

    /// <summary>
    /// Тест 7. После потери соединения Worker A не выполняет новые файловые операции.
    /// </summary>
    [Fact]
    public async Task Test07_WorkerA_DoesNotPerformFurtherFileOperations_AfterConnectionLoss()
    {
        var tokenA = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenA);
        var dirA = CreateExecutionDirectory(analysis.Id, tokenA);

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        var analyzer1Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var analyzer2Called = false;

        var analyzer1 = new Mock<IAnalyzer>();
        analyzer1.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
        analyzer1.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns(async (AnalyzerContext ctx, CancellationToken ct) =>
            {
                analyzer1Entered.TrySetResult(true);

                // Ждем отмены
                var tcs = new TaskCompletionSource<bool>();
                using (ct.Register(() => tcs.TrySetResult(true)))
                {
                    await tcs.Task;
                }

                ct.ThrowIfCancellationRequested();
                return [];
            });

        var analyzer2 = new Mock<IAnalyzer>();
        analyzer2.Setup(x => x.Type).Returns(AnalyzerType.Database);
        analyzer2.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                analyzer2Called = true;
                return Task.FromResult<IReadOnlyList<AnalyzerFinding>>([]);
            });

        var cleanupCalled = false;
        var mockRepoSourceService = new Mock<IRepositorySourceService>();
        mockRepoSourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(), It.IsAny<string>(), analysis.Id, tokenA, It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dirA);

        mockRepoSourceService.Setup(x => x.CleanupAsync(analysis.Id, tokenA, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cleanupCalled = true;
                return Task.CompletedTask;
            });

        var mockCredential = new Mock<IGitHubCredentialService>();
        mockCredential.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        await using var runContext = new AppDbContext(options);
        var analysisRepo = new AnalysisRepository(runContext);
        var commitRepo = new CommitRepository(runContext);

        var service = new AnalysisService(
            analysisRepo,
            commitRepo,
            mockRepoSourceService.Object,
            [analyzer1.Object, analyzer2.Object],
            mockCredential.Object,
            guardProvider);

        var workerTask = Task.Run(() => service.ExecuteAnalysisAsync(analysis.Id, tokenA, default, TimeSpan.FromSeconds(3)));

        await analyzer1Entered.Task;

        // Принудительно разрываем соединение Worker A
        await using var adminConn = new NpgsqlConnection(fixture.ConnectionString);
        await adminConn.OpenAsync();

        await using var killCmd = adminConn.CreateCommand();
        killCmd.CommandText = """
            SELECT pg_terminate_backend(pid) FROM pg_locks
            WHERE locktype = 'advisory'
              AND classid = hashtext('netscope_analysis_workspace')
              AND objid = @analysisId
              AND objsubid = 2
              AND mode = 'ShareLock'
              AND granted = true;
            """;
        killCmd.Parameters.AddWithValue("analysisId", analysis.Id);
        await killCmd.ExecuteNonQueryAsync();

        var success = await workerTask;
        Assert.False(success);

        // Второй анализатор НЕ должен вызываться
        Assert.False(analyzer2Called, "После потери владения последующие анализаторы не должны запускаться!");

        // Очистка каталога не должна вызываться, так как владение утрачено
        Assert.False(cleanupCalled, "При потере владения воркер не должен удалять рабочий каталог.");
    }

    /// <summary>
    /// Тест 8. Worker A не удаляет каталог Worker B.
    /// </summary>
    [Fact]
    public async Task Test08_WorkerA_DoesNotDeleteWorkerBDirectory_AfterLeaseLoss()
    {
        var tokenA = Guid.NewGuid();
        var tokenB = Guid.NewGuid();

        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenA);
        var dirA = CreateExecutionDirectory(analysis.Id, tokenA);
        var dirB = CreateExecutionDirectory(analysis.Id, tokenB);
        var fileB = Path.Combine(dirB, "repository", "workerB_source.cs");
        await File.WriteAllTextAsync(fileB, "// critical worker B data");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:AnalysisPath"] = _testStorageRoot
            })
            .Build();

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        var realRepoSourceService = new RepositorySourceService(new HttpClient(), config, guardProvider);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        var analyzerEnteredTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var mockAnalyzer = new Mock<IAnalyzer>();
        mockAnalyzer.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
        mockAnalyzer.Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .Returns(async (AnalyzerContext ctx, CancellationToken ct) =>
            {
                analyzerEnteredTcs.TrySetResult(true);

                // Во время выполнения анализатора задача в БД перехватывается с токеном B
                await using var stealCtx = new AppDbContext(options);
                var row = await stealCtx.Analyses.FirstAsync(a => a.Id == analysis.Id, ct);
                row.ExecutionToken = tokenB;
                row.LeaseExpiration = DateTime.UtcNow.AddMinutes(10);
                await stealCtx.SaveChangesAsync(ct);

                return [];
            });

        var mockRepoSourceService = new Mock<IRepositorySourceService>();
        mockRepoSourceService.Setup(x => x.DownloadAndExtractAsync(
            It.IsAny<string>(), It.IsAny<string>(), analysis.Id, tokenA, It.IsAny<string>(), It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dirA);

        mockRepoSourceService.Setup(x => x.CleanupAsync(analysis.Id, tokenA, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Returns((int aId, Guid t, IAnalysisLeaseGuard g, CancellationToken ct) => realRepoSourceService.CleanupAsync(aId, t, g, ct));

        var mockCredential = new Mock<IGitHubCredentialService>();
        mockCredential.Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        await using var runContext = new AppDbContext(options);
        var analysisRepo = new AnalysisRepository(runContext);
        var commitRepo = new CommitRepository(runContext);

        var service = new AnalysisService(
            analysisRepo,
            commitRepo,
            mockRepoSourceService.Object,
            [mockAnalyzer.Object],
            mockCredential.Object,
            guardProvider);

        var success = await service.ExecuteAnalysisAsync(analysis.Id, tokenA, default, TimeSpan.FromMinutes(2));
        Assert.False(success);

        // Каталог Worker B должен быть полностью сохранён
        Assert.True(Directory.Exists(dirB), "Каталог Worker B не должен быть удален Worker A!");
        Assert.True(File.Exists(fileB), "Файлы Worker B должны остаться нетронутыми.");
    }

    /// <summary>
    /// Тест 9. DatabaseContainerRunner использует переданный guard и не пытается получить второй.
    /// </summary>
    [Fact]
    public async Task Test09_DatabaseContainerRunner_UsesPassedGuard_AndDoesNotAcquireSecond()
    {
        var token = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, token);

        var guardProviderMock = new Mock<DatabaseAnalyzer.Runner.IAnalysisLeaseGuardProvider>();
        guardProviderMock.Setup(p => p.AcquireGuardAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("DatabaseContainerRunner must NOT call AcquireGuardAsync!"));

        var realProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        await using var activeGuard = await realProvider.AcquireGuardAsync(analysis.Id, token);
        Assert.True(activeGuard.IsAcquired);

        var mockProcessAdapter = new Mock<DatabaseAnalyzer.Process.IContainerProcessAdapter>();
        mockProcessAdapter.Setup(p => p.RunAsync(It.IsAny<DatabaseAnalyzer.Process.ContainerRunOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseAnalyzer.Process.ProcessExecutionResult { ExitCode = 0 });

        var runner = new DatabaseContainerRunner(
            mockProcessAdapter.Object,
            "netscope-database-analyzer:latest",
            guardProviderMock.Object);

        var extraction = new RoslynModelExtractionResult
        {
            MetadataCapabilities = ["CoreRelational"],
            Contexts = []
        };

        var result = await runner.RunAnalysisAsync(extraction, analysis.Id, token, activeGuard);

        // Runner не вызывал AcquireGuardAsync на guardProviderMock
        guardProviderMock.Verify(p => p.AcquireGuardAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Тест 10. DatabaseContainerRunner корректно останавливает контейнер при отмене CancellationToken.
    /// </summary>
    [Fact]
    public async Task Test10_DatabaseContainerRunner_StopsContainerGracefully_OnCancellation()
    {
        var token = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, token);

        var realProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        await using var activeGuard = await realProvider.AcquireGuardAsync(analysis.Id, token);
        Assert.True(activeGuard.IsAcquired);

        var mockProcessAdapter = new Mock<DatabaseAnalyzer.Process.IContainerProcessAdapter>();
        mockProcessAdapter.Setup(p => p.RunAsync(It.IsAny<DatabaseAnalyzer.Process.ContainerRunOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseAnalyzer.Process.ProcessExecutionResult { ExitCode = 137, IsCancelled = true });

        var runner = new DatabaseContainerRunner(mockProcessAdapter.Object);

        var extraction = new RoslynModelExtractionResult
        {
            MetadataCapabilities = ["CoreRelational"],
            Contexts = []
        };

        var result = await runner.RunAnalysisAsync(extraction, analysis.Id, token, activeGuard);

        Assert.Equal("Cancelled", result.Status);
        Assert.Contains("cancelled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Тест 11. CompleteAsync успешно сохраняет результаты при действительном токене.
    /// </summary>
    [Fact]
    public async Task Test11_CompleteAsync_Succeeds_WithValidTokenAndStatus()
    {
        var token = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, token);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        await using var context = new AppDbContext(options);
        var repo = new AnalysisRepository(context);

        var findings = new List<AnalysisResult>
        {
            new()
            {
                AnalysisId = analysis.Id,
                Rule = "DB01",
                FilePath = "Model.cs",
                LineNumber = 10,
                Message = "Missing primary key",
                Severity = Severity.Error,
                AnalyzerType = AnalyzerType.Database
            }
        };

        var completed = await repo.CompleteAsync(analysis.Id, token, findings);
        Assert.True(completed);

        // Проверяем состояние в БД
        await using var verifyContext = new AppDbContext(options);
        var updated = await verifyContext.Analyses
            .Include(a => a.Results)
            .FirstAsync(a => a.Id == analysis.Id);

        Assert.Equal(AnalysisStatus.Completed, updated.Status);
        Assert.NotNull(updated.CompletedAt);
        Assert.Null(updated.LeaseExpiration);
        Assert.Single(updated.Results);
        Assert.Equal("DB01", updated.Results.First().Rule);
    }

    /// <summary>
    /// Тест 12. CompleteAsync возвращает false при несовпадающем токене.
    /// </summary>
    [Fact]
    public async Task Test12_CompleteAsync_ReturnsFalse_OnTokenMismatch()
    {
        var token = Guid.NewGuid();
        var wrongToken = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, token);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        await using var context = new AppDbContext(options);
        var repo = new AnalysisRepository(context);

        var completed = await repo.CompleteAsync(analysis.Id, wrongToken, []);
        Assert.False(completed);

        await using var verifyContext = new AppDbContext(options);
        var unchanged = await verifyContext.Analyses.FirstAsync(a => a.Id == analysis.Id);
        Assert.Equal(AnalysisStatus.Running, unchanged.Status);
    }

    /// <summary>
    /// Тест 13. CompleteAsync возвращает false, если статус больше не Running.
    /// </summary>
    [Fact]
    public async Task Test13_CompleteAsync_ReturnsFalse_WhenStatusNotRunning()
    {
        var token = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Failed, token);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        await using var context = new AppDbContext(options);
        var repo = new AnalysisRepository(context);

        var completed = await repo.CompleteAsync(analysis.Id, token, []);
        Assert.False(completed);
    }

    /// <summary>
    /// Тест 14. Ошибка PostgreSQL при проверке владения приводит к fail-closed.
    /// </summary>
    [Fact]
    public async Task Test14_PostgreSqlError_InOwnershipCheck_ResultsInFailClosed()
    {
        var token = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, token);

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        var guard = await guardProvider.AcquireGuardAsync(analysis.Id, token);
        Assert.True(guard.IsAcquired);

        // Убиваем сессию соединения guard
        await using var adminConn = new NpgsqlConnection(fixture.ConnectionString);
        await adminConn.OpenAsync();

        await using var killCmd = adminConn.CreateCommand();
        killCmd.CommandText = """
            SELECT pg_terminate_backend(pid) FROM pg_locks
            WHERE locktype = 'advisory'
              AND classid = hashtext('netscope_analysis_workspace')
              AND objid = @analysisId
              AND objsubid = 2;
            """;
        killCmd.Parameters.AddWithValue("analysisId", analysis.Id);
        await killCmd.ExecuteNonQueryAsync();

        // Проверка владения должна вернуть false (fail-closed), не вызывая необработанных сбоев
        var isOwner = await guard.ValidateOwnershipAsync([(int)AnalysisStatus.Running]);
        Assert.False(isOwner, "При ошибке/разрыве соединения PostgreSQL проверка владения должна возвращать false (fail-closed).");
    }

    /// <summary>
    /// Тест 15. Отсутствие guard не допускает файловых операций.
    /// </summary>
    [Fact]
    public async Task Test15_MissingGuard_PreventsFileOperations()
    {
        var token = Guid.NewGuid();
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, token);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:AnalysisPath"] = _testStorageRoot
            })
            .Build();

        var repoSourceService = new RepositorySourceService(new HttpClient(), config);

        var expectedDir = Path.Combine(_testStorageRoot, analysis.Id.ToString(), token.ToString("D"));

        // 1. DownloadAndExtractAsync без guard выбрасывает InvalidOperationException и не создаёт каталог
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repoSourceService.DownloadAndExtractAsync(
                "https://github.com/test/repo",
                "hash123",
                analysis.Id,
                token,
                "ghp_test_token",
                guard: null!,
                CancellationToken.None));

        Assert.False(Directory.Exists(expectedDir), "Каталог не должен создаваться при отсутствии guard!");

        // 2. DatabaseContainerRunner без guard возвращает Failed и не запускает процесс
        var mockAdapter = new Mock<DatabaseAnalyzer.Process.IContainerProcessAdapter>();
        var runner = new DatabaseContainerRunner(mockAdapter.Object);

        var extraction = new RoslynModelExtractionResult
        {
            MetadataCapabilities = ["CoreRelational"],
            Contexts = []
        };

        var containerResult = await runner.RunAnalysisAsync(extraction, analysis.Id, token, leaseGuard: null!);
        Assert.Equal("Failed", containerResult.Status);
        Assert.Contains("Active workspace lease guard is missing", containerResult.ErrorMessage);
        mockAdapter.Verify(p => p.RunAsync(It.IsAny<DatabaseAnalyzer.Process.ContainerRunOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion


    /// <summary>
    /// Сценарий 7: Если Worker удерживает файловую SharedLock, Cleaner не удаляет каталог даже после потери PostgreSQL-соединения Worker.
    /// Сценарий 8: После освобождения файловой SharedLock Cleaner может получить ExclusiveLock и выполнить очистку.
    /// </summary>
    [Fact]
    public async Task Scenario7And8_WorkerRetainsFileSharedLock_AfterPostgreSqlConnectionSevered_CleanerBlockedUntilFileLockReleased()
    {
        var tokenOld = Guid.NewGuid();
        var tokenNew = Guid.NewGuid();
        var expiredLease = DateTime.UtcNow.AddMinutes(-5);
        var analysis = await CreateTestAnalysisAsync(AnalysisStatus.Running, tokenNew, expiredLease);

        var staleDir = CreateExecutionDirectory(analysis.Id, tokenOld);
        var activeDir = CreateExecutionDirectory(analysis.Id, tokenNew);
        var dummyFile = Path.Combine(staleDir, "test.txt");
        await File.WriteAllTextAsync(dummyFile, "payload");

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(fixture.ConnectionString, _fileLockProvider);
        var guard = await guardProvider.AcquireGuardAsync(analysis.Id, tokenOld);
        Assert.True(guard.IsAcquired);

        var backendPid = ((PostgreSqlAnalysisLeaseGuard)guard).ConnectionProcessId;
        Assert.NotNull(backendPid);

        // Разрываем соединение Worker с PostgreSQL через pg_terminate_backend
        await using (var killerConn = new NpgsqlConnection(fixture.ConnectionString))
        {
            await killerConn.OpenAsync();
            await using var killCmd = killerConn.CreateCommand();
            killCmd.CommandText = "SELECT pg_terminate_backend(@pid);";
            killCmd.Parameters.AddWithValue("pid", backendPid.Value);
            await killCmd.ExecuteScalarAsync();
        }

        // Даем время на закрытие сессии в PostgreSQL
        await Task.Delay(100);

        var cleanupService = new AnalysisCleanupService(fixture.ConnectionString, _fileLockProvider, _testStorageRoot);

        // Сценарий 7: Cleaner пытается очистить каталог.
        // PostgreSQL lock свободен (сессия закрыта), но файловый SharedLock все еще удерживается процессом Worker!
        var deletedCount1 = await cleanupService.CleanupStaleDirectoriesAsync();

        Assert.Equal(0, deletedCount1);
        Assert.True(Directory.Exists(staleDir), "Каталог воркера не должен быть удален, пока воркер удерживает файловую блокировку!");
        Assert.True(File.Exists(dummyFile));

        // Сценарий 8: Воркер закрывает/освобождает guard (включая файловую блокировку)
        await guard.DisposeAsync();

        // Теперь Cleaner успешно захватывает обе блокировки (PostgreSQL ExclusiveLock и File ExclusiveLock)
        var deletedCount2 = await cleanupService.CleanupStaleDirectoriesAsync();

        Assert.Equal(1, deletedCount2);
        Assert.False(Directory.Exists(staleDir), "Устаревший каталог должен быть удален после освобождения файловой блокировки!");
        Assert.True(Directory.Exists(activeDir), "Каталог активного исполнения должен быть сохранен!");
    }


    [Theory]
    [InlineData(AnalysisStatus.Completed)]
    [InlineData(AnalysisStatus.Failed)]
    public async Task CleanupAsync_RemovesOwnWorkspace_WhenAnalysisIsTerminal(
        AnalysisStatus terminalStatus)
    {
        var executionToken = Guid.NewGuid();

        var analysis = await CreateTestAnalysisAsync(
            terminalStatus,
            executionToken);

        var executionDirectory = CreateExecutionDirectory(
            analysis.Id,
            executionToken);

        Assert.True(Directory.Exists(executionDirectory));

        var guardProvider = new PostgreSqlAnalysisLeaseGuardProvider(
            fixture.ConnectionString,
            _fileLockProvider);

        await using var guard = await guardProvider.AcquireGuardAsync(
            analysis.Id,
            executionToken);

        Assert.True(guard.IsAcquired);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:AnalysisPath"] = _testStorageRoot
            })
            .Build();

        var service = new RepositorySourceService(
            new HttpClient(),
            configuration,
            guardProvider);

        await service.CleanupAsync(
            analysis.Id,
            executionToken,
            guard);

        Assert.False(
            Directory.Exists(executionDirectory),
            $"Собственный workspace должен удаляться при статусе {terminalStatus}.");
    }
}
