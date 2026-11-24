using System.Data.Common;
using System.IO;
using System.Net.Sockets;
using Application.Analyzers;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Application.Services;
using DatabaseAnalyzer.Runner;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Npgsql;
using Xunit;
using IAnalysisLeaseGuard = Application.Interfaces.IAnalysisLeaseGuard;

namespace Backend.Tests.Integration;

/// <summary>
/// Наблюдатель диагностических событий для проверки выполнения recovery branch в тестах.
/// </summary>
public sealed class TestCommitRecoveryObserver : IAnalysisCommitRecoveryObserver
{
    public bool CommitStarted { get; private set; }
    public Exception? CaughtCommitException { get; private set; }
    public bool RecoveryStarted { get; private set; }
    public AnalysisStatus? InspectedStatus { get; private set; }
    public Guid? InspectedExecutionToken { get; private set; }
    public int? InspectedGuardProcessId { get; private set; }
    public int? InspectedRecoveryProcessId { get; private set; }
    public int? InspectedConnectionProcessId => InspectedRecoveryProcessId;
    public bool? RecoverySuccess { get; private set; }

    public void OnCommitStarted(int analysisId, Guid executionToken)
    {
        CommitStarted = true;
    }

    public void OnCommitFailed(int analysisId, Guid executionToken, Exception exception)
    {
        CaughtCommitException = exception;
    }

    public void OnRecoveryStarted(int analysisId, Guid executionToken)
    {
        RecoveryStarted = true;
    }

    public void OnRecoveryStateInspected(
        int analysisId,
        Guid executionToken,
        AnalysisStatus? status,
        Guid? token,
        int? guardProcessId,
        int? recoveryProcessId)
    {
        InspectedStatus = status;
        InspectedExecutionToken = token;
        InspectedGuardProcessId = guardProcessId;
        InspectedRecoveryProcessId = recoveryProcessId;
    }

    public void OnRecoveryCompleted(int analysisId, Guid executionToken, bool success)
    {
        RecoverySuccess = success;
    }
}

/// <summary>
/// Тестовый перехватчик для проверки поведения при программном исключении в post-commit callback (Scenario G).
/// ВНИМАНИЕ: Это тест программного исключения в коллбеке EF Core, а НЕ реальная потеря сетевого ACK (Scenario A).
/// </summary>
public sealed class CommitAckFaultInjectionInterceptor : DbTransactionInterceptor
{
    public bool TriggerFault { get; set; }

    public override async Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await base.TransactionCommittedAsync(transaction, eventData, cancellationToken);

        if (TriggerFault)
        {
            throw new NpgsqlException("Simulated programmatic exception in post-commit callback");
        }
    }
}

public class DelegatingAnalysisRepository(IAnalysisRepository inner) : IAnalysisRepository
{
    protected readonly IAnalysisRepository Inner = inner;

    public virtual Task<Analysis?> GetByIdAsync(int id, CancellationToken cancellationToken = default) => Inner.GetByIdAsync(id, cancellationToken);
    public virtual Task<(IReadOnlyList<Analysis> Items, int TotalCount)> GetByCommitIdAsync(int commitId, int page, int pageSize, CancellationToken cancellationToken = default) => Inner.GetByCommitIdAsync(commitId, page, pageSize, cancellationToken);
    public virtual Task AddAsync(Analysis analysis, CancellationToken cancellationToken = default) => Inner.AddAsync(analysis, cancellationToken);
    public virtual Task UpdateAsync(Analysis analysis, CancellationToken cancellationToken = default) => Inner.UpdateAsync(analysis, cancellationToken);
    public virtual Task<bool> HasActiveAnalysisAsync(int commitId, CancellationToken cancellationToken = default) => Inner.HasActiveAnalysisAsync(commitId, cancellationToken);
    public virtual Task<IAnalysisRepositoryTransaction> BeginEnqueueTransactionAsync(int commitId, CancellationToken cancellationToken = default) => Inner.BeginEnqueueTransactionAsync(commitId, cancellationToken);
    public virtual Task<Analysis?> TryAcquireNextPendingOrStaleAsync(TimeSpan leaseDuration, CancellationToken cancellationToken = default) => Inner.TryAcquireNextPendingOrStaleAsync(leaseDuration, cancellationToken);
    public virtual Task<bool> RenewLeaseAsync(int analysisId, Guid executionToken, TimeSpan leaseDuration, CancellationToken cancellationToken = default) => Inner.RenewLeaseAsync(analysisId, executionToken, leaseDuration, cancellationToken);
    public virtual Task<bool> CompleteAsync(int analysisId, Guid executionToken, IReadOnlyList<AnalysisResult> results, IAnalysisLeaseGuard guard, CancellationToken cancellationToken = default) => Inner.CompleteAsync(analysisId, executionToken, results, guard, cancellationToken);
    public virtual Task<bool> FailAsync(int analysisId, Guid executionToken, string? errorMessage, IAnalysisLeaseGuard guard, CancellationToken cancellationToken = default) => Inner.FailAsync(analysisId, executionToken, errorMessage, guard, cancellationToken);
    public virtual Task<bool> ScheduleRetryAsync(int analysisId, Guid executionToken, DateTime nextAttemptAt, string errorMessage, IAnalysisLeaseGuard guard, CancellationToken cancellationToken = default) => Inner.ScheduleRetryAsync(analysisId, executionToken, nextAttemptAt, errorMessage, guard, cancellationToken);
    public virtual int? GetConnectionProcessId() => Inner.GetConnectionProcessId();
    public virtual Task<(Analysis? Analysis, int? ProcessId)> GetAnalysisWithProcessIdAsync(int id, CancellationToken cancellationToken = default) => Inner.GetAnalysisWithProcessIdAsync(id, cancellationToken);
}

public sealed class InconsistentResultsRepository(IAnalysisRepository inner) : DelegatingAnalysisRepository(inner)
{
    public override async Task<bool> CompleteAsync(
        int analysisId,
        Guid executionToken,
        IReadOnlyList<AnalysisResult> results,
        IAnalysisLeaseGuard guard,
        CancellationToken cancellationToken = default)
    {
        await base.CompleteAsync(analysisId, executionToken, results, guard, cancellationToken);
        throw new NpgsqlException("Simulated commit ACK loss with corrupted results");
    }

    public override async Task<(Analysis? Analysis, int? ProcessId)> GetAnalysisWithProcessIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var (analysis, pid) = await base.GetAnalysisWithProcessIdAsync(id, cancellationToken);
        if (analysis != null)
        {
            analysis.Results.Clear();
        }
        return (analysis, pid);
    }
}

public sealed class ForeignTokenRepository(IAnalysisRepository inner) : DelegatingAnalysisRepository(inner)
{
    public override async Task<bool> CompleteAsync(
        int analysisId,
        Guid executionToken,
        IReadOnlyList<AnalysisResult> results,
        IAnalysisLeaseGuard guard,
        CancellationToken cancellationToken = default)
    {
        await base.CompleteAsync(analysisId, executionToken, results, guard, cancellationToken);
        throw new NpgsqlException("Simulated commit ACK loss with foreign token");
    }

    public override async Task<(Analysis? Analysis, int? ProcessId)> GetAnalysisWithProcessIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var (analysis, pid) = await base.GetAnalysisWithProcessIdAsync(id, cancellationToken);
        if (analysis != null)
        {
            analysis.ExecutionToken = Guid.NewGuid();
        }
        return (analysis, pid);
    }
}

public sealed class PostgreSqlCommitAckLossIntegrationTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly string _testStorageRoot;
    private readonly CommitAckFaultInjectionInterceptor _interceptor;

    public PostgreSqlCommitAckLossIntegrationTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _testStorageRoot = Path.Combine(Path.GetTempPath(), "test_commit_ack_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testStorageRoot);
        _interceptor = new CommitAckFaultInjectionInterceptor();
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

    private AppDbContext CreateDbContext(string? connectionString = null, bool addInterceptor = false)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString ?? _fixture.ConnectionString);

        if (addInterceptor)
        {
            builder.AddInterceptors(_interceptor);
        }

        return new AppDbContext(builder.Options);
    }

    private async Task<(Analysis Analysis, Guid Token, string WorkspaceDir, Mock<IRepositorySourceService> SourceService, AnalysisService Service)>
        SetupAnalysisEnvironmentAsync(
            string connectionString,
            bool addInterceptor = false,
            TestCommitRecoveryObserver? observer = null,
            Func<IAnalysisRepository, IAnalysisRepository>? repoDecorator = null)
    {
        var token = Guid.NewGuid();
        var futureLease = DateTime.UtcNow.AddMinutes(5);

        await using var initContext = CreateDbContext();

        var user = new User
        {
            Username = $"user_{Guid.NewGuid():N}"[..16],
            PasswordHash = "hash",
            RoleId = 2
        };
        initContext.Users.Add(user);
        await initContext.SaveChangesAsync();

        var project = new Project
        {
            Name = "CommitAckTestProject_" + Guid.NewGuid().ToString("N")[..8],
            RepoUrl = "https://github.com/test/repo",
            OwnerId = user.Id
        };
        initContext.Projects.Add(project);
        await initContext.SaveChangesAsync();

        var commit = new Commit
        {
            ProjectId = project.Id,
            CommitHash = Guid.NewGuid().ToString("N"),
            CommitDate = DateTime.UtcNow
        };
        initContext.Commits.Add(commit);
        await initContext.SaveChangesAsync();

        var analysis = new Analysis
        {
            CommitId = commit.Id,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            LeaseExpiration = futureLease,
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        initContext.Analyses.Add(analysis);
        await initContext.SaveChangesAsync();

        var workspaceDir = Path.Combine(_testStorageRoot, "workspace_" + analysis.Id + "_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(workspaceDir);

        var mockSourceService = new Mock<IRepositorySourceService>();
        mockSourceService
            .Setup(x => x.DownloadAndExtractAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<IAnalysisLeaseGuard>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(workspaceDir);

        mockSourceService
            .Setup(x => x.CleanupAsync(analysis.Id, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                if (Directory.Exists(workspaceDir))
                {
                    Directory.Delete(workspaceDir, recursive: true);
                }
            })
            .Returns(Task.CompletedTask);

        var mockAnalyzer = new Mock<IAnalyzer>();
        mockAnalyzer.Setup(x => x.Type).Returns(AnalyzerType.CSharp);
        mockAnalyzer
            .Setup(x => x.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AnalyzerFinding>
            {
                new()
                {
                    Rule = "NET001",
                    Message = "Buffer boundary check missing",
                    FilePath = "Network.cs",
                    LineNumber = 42,
                    Severity = Severity.Error,
                    Recommendation = "Check buffer length"
                }
            });

        var mockCredService = new Mock<IGitHubCredentialService>();
        mockCredService
            .Setup(x => x.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        var workspaceLockProvider = new Infrastructure.Storage.WorkspaceFileLockProvider(_testStorageRoot);
        var leaseGuardProvider = new PostgreSqlAnalysisLeaseGuardProvider(connectionString, workspaceLockProvider);

        var executionContext = CreateDbContext(connectionString, addInterceptor);
        IAnalysisRepository analysisRepo = new AnalysisRepository(executionContext);
        if (repoDecorator != null)
        {
            analysisRepo = repoDecorator(analysisRepo);
        }
        var commitRepo = new CommitRepository(executionContext);

        var service = new AnalysisService(
            analysisRepo,
            commitRepo,
            mockSourceService.Object,
            [mockAnalyzer.Object],
            mockCredService.Object,
            leaseGuardProvider,
            recoveryObserver: observer);

        return (analysis, token, workspaceDir, mockSourceService, service);
    }

    /// <summary>
    /// Scenario A: COMMIT успешно зафиксирован в PostgreSQL, но подтверждение (ACK) перехвачено
    /// и сброшено прокси на уровне PostgreSQL wire protocol, после чего клиентский сокет разорван с TCP RST.
    /// Доказывается:
    /// 1. Перехват пакета CommandComplete('COMMIT') прокси и клиентский RST.
    /// 2. Получение реального сетевого исключения Npgsql/Socket в CompleteAsync.
    /// 3. Вход в recovery branch и выполнение независимого чтения через отдельное физическое соединение.
    /// 4. Подтверждение разделения PID: guardProcessId != recoveryProcessId.
    /// 5. Подтверждение статуса Completed, токена и согласованности результатов.
    /// 6. Восстановление без дублирования результатов и выполнение штатной очистки.
    /// </summary>
    [Fact]
    public async Task ScenarioA_ExecuteAnalysisAsync_RecoversFromRealNetworkCommitAckLoss_ViaLoopbackProxy()
    {
        await using var proxy = PostgreSqlLoopbackFaultProxy.Create(_fixture.ConnectionString);
        var proxiedConnString = proxy.GetProxiedConnectionString(_fixture.ConnectionString);

        var observer = new TestCommitRecoveryObserver();
        var (analysis, token, workspaceDir, mockSourceService, service) =
            await SetupAnalysisEnvironmentAsync(proxiedConnString, observer: observer);

        // Активируем реальный сетевой обрыв сокета ровно при доставке CommandComplete с тегом COMMIT
        proxy.DropOnCommitAck = true;

        var result = await service.ExecuteAnalysisAsync(analysis.Id, token);

        // 1. Проверяем сигналы прокси:
        Assert.True(proxy.CommitDetected, "Команда COMMIT обязана быть распознана в клиентском потоке.");
        Assert.True(proxy.CommitAckIntercepted, "Пакет CommandComplete('COMMIT') обязан быть перехвачен до доставки клиенту.");
        Assert.True(proxy.ClientSocketAborted, "Клиентский TCP-сокет обязан быть разорван через RST (LingerState=0).");

        // 2. Проверяем диагностические сигналы наблюдателя (recovery branch):
        Assert.True(observer.CommitStarted, "Вызов CompleteAsync обязан быть начат.");
        Assert.NotNull(observer.CaughtCommitException);
        Assert.True(
            observer.CaughtCommitException is NpgsqlException or IOException or SocketException,
            $"Ожидалось реальное сетевое исключение разрыва сокета, получено: {observer.CaughtCommitException.GetType().Name}");

        Assert.True(observer.RecoveryStarted, "Recovery branch в AnalysisService обязана быть вызвана при исключении.");
        Assert.Equal(AnalysisStatus.Completed, observer.InspectedStatus);
        Assert.Equal(token, observer.InspectedExecutionToken);
        Assert.NotNull(observer.InspectedGuardProcessId);
        Assert.NotNull(observer.InspectedRecoveryProcessId);
        Assert.NotEqual(observer.InspectedGuardProcessId, observer.InspectedRecoveryProcessId);
        Assert.True(observer.RecoverySuccess == true, "Ветка восстановления обязана признать транзакцию успешно зафиксированной.");

        // 3. Метод обязан вернуть true, восстановив состояние после разрыва TCP-сокета
        Assert.True(result);

        // 4. Задача в PostgreSQL переведена в Completed
        await using var verifyContext = CreateDbContext();
        var refreshedAnalysis = await verifyContext.Analyses
            .Include(a => a.Results)
            .FirstOrDefaultAsync(a => a.Id == analysis.Id);

        Assert.NotNull(refreshedAnalysis);
        Assert.Equal(AnalysisStatus.Completed, refreshedAnalysis.Status);
        Assert.Null(refreshedAnalysis.LeaseExpiration);
        Assert.NotNull(refreshedAnalysis.CompletedAt);

        // 5. Результаты анализа сохранены в базе данных (ровно 1 результат, нет дубликатов)
        Assert.Single(refreshedAnalysis.Results);
        Assert.Equal("NET001", refreshedAnalysis.Results.First().Rule);

        // 6. Очистка рабочего каталога выполнена ровно один раз
        mockSourceService.Verify(x => x.CleanupAsync(analysis.Id, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(Directory.Exists(workspaceDir));
    }

    /// <summary>
    /// Scenario B: Фиксация COMMIT в PostgreSQL НЕ состоялась (обрыв произошёл ДО фиксации коммита на сервере).
    /// Доказывается:
    /// 1. Разрыв сокета до фиксации коммита на сервере.
    /// 2. Исключение на стороне клиента.
    /// 3. Вход в recovery branch и обнаружение, что задача по-прежнему в статусе Running (не зафиксирована).
    /// 4. Сервис НЕ признает задачу выполненной (возвращает false).
    /// 5. Блокировка слепой очистки каталога: CleanupAsync не вызывается, директория сохраняется на диске.
    /// </summary>
    [Fact]
    public async Task ScenarioB_ExecuteAnalysisAsync_WhenCommitNeverExecutedInDatabase_DoesNotBlindlyCompleteOrCleanup()
    {
        await using var proxy = PostgreSqlLoopbackFaultProxy.Create(_fixture.ConnectionString);
        var proxiedConnString = proxy.GetProxiedConnectionString(_fixture.ConnectionString);

        var observer = new TestCommitRecoveryObserver();
        var (analysis, token, workspaceDir, mockSourceService, service) =
            await SetupAnalysisEnvironmentAsync(proxiedConnString, observer: observer);

        // Обрываем соединение ДО фиксации транзакции на сервере (транзакция откатывается PostgreSQL)
        proxy.DropOnClientCommit = true;

        var result = await service.ExecuteAnalysisAsync(analysis.Id, token);

        // 1. Проверяем диагностику прокси: сбой был вызван ДО коммита
        Assert.True(proxy.CommitDetected);
        Assert.False(proxy.CommitAckIntercepted, "ACK не должен быть перехвачен, так как коммит не дошёл до сервера.");
        Assert.True(proxy.ClientSocketAborted);

        // 2. Проверяем сигналы наблюдателя:
        Assert.True(observer.CommitStarted);
        Assert.NotNull(observer.CaughtCommitException);
        Assert.True(observer.RecoveryStarted);
        Assert.Equal(AnalysisStatus.Running, observer.InspectedStatus); // Сервер НЕ зафиксировал коммит!
        Assert.True(observer.RecoverySuccess == false, "Ветка восстановления обязана отклонить незафиксированный анализ.");

        // 3. Анализ не может считаться завершённым
        Assert.False(result);

        // 4. База данных остаётся в исходном состоянии: статус Running, результатов нет
        await using var verifyContext = CreateDbContext();
        var refreshedAnalysis = await verifyContext.Analyses
            .Include(a => a.Results)
            .FirstOrDefaultAsync(a => a.Id == analysis.Id);

        Assert.NotNull(refreshedAnalysis);
        Assert.Equal(AnalysisStatus.Running, refreshedAnalysis.Status);
        Assert.Null(refreshedAnalysis.CompletedAt);
        Assert.Empty(refreshedAnalysis.Results);

        // 5. Слепая очистка каталога ЗАПРЕЩЕНА: CleanupAsync не должен быть вызван при неопределённом или сбойном результате!
        mockSourceService.Verify(x => x.CleanupAsync(analysis.Id, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(Directory.Exists(workspaceDir), "Рабочий каталог обязан быть сохранен для анализа инцидента.");
    }

    /// <summary>
    /// Scenario C: Сбой проверки статуса при recovery (недоступность БД).
    /// При checkEx в recovery-блоке статус коммита признаётся неоднозначным (isAmbiguousCommit = true).
    /// Метод возвращает false, автоматические изменения состояния (FailAsync, ScheduleRetryAsync) отменяются,
    /// очистка каталога ЗАПРЕЩЕНА (директория сохраняется на диске), логируется AggregateException.
    /// </summary>
    [Fact]
    public async Task ScenarioC_ExecuteAnalysisAsync_WhenRecoveryDatabaseUnreachable_SuppressesAutomaticStateChangesAndRetainsWorkspace()
    {
        await using var proxy = PostgreSqlLoopbackFaultProxy.Create(_fixture.ConnectionString);
        var proxiedConnString = proxy.GetProxiedConnectionString(_fixture.ConnectionString);

        var observer = new TestCommitRecoveryObserver();
        var (analysis, token, workspaceDir, mockSourceService, service) =
            await SetupAnalysisEnvironmentAsync(proxiedConnString, observer: observer);

        // Обрываем ACK коммита И сбрасываем любые последующие новые подключения к прокси
        proxy.DropOnCommitAck = true;
        proxy.RejectNewConnectionsAfterDrop = true;

        var result = await service.ExecuteAnalysisAsync(analysis.Id, token);

        // 1. Метод возвращает false (коммит неоднозначен)
        Assert.False(result);

        // 2. Сигналы прокси
        Assert.True(proxy.CommitDetected);
        Assert.True(proxy.CommitAckIntercepted);
        Assert.True(proxy.ClientSocketAborted);

        // 3. Сигналы наблюдателя
        Assert.True(observer.CommitStarted);
        Assert.NotNull(observer.CaughtCommitException);
        Assert.True(observer.RecoveryStarted);
        Assert.NotEqual(true, observer.RecoverySuccess);

        // 4. Очистка каталога ЗАПРЕЩЕНА: рабочий каталог обязан быть сохранен для ручного расследования
        mockSourceService.Verify(x => x.CleanupAsync(analysis.Id, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(Directory.Exists(workspaceDir), "Рабочий каталог обязан быть сохранен при неоднозначном коммите.");

        // 5. Проверяем состояние БД напрямую через фикстуру:
        // Сервер PostgreSQL на самом деле успешно зафиксировал транзакцию (Completed),
        // и AnalysisService НЕ перезаписал её в Failed или Pending!
        await using var verifyContext = CreateDbContext();
        var refreshedAnalysis = await verifyContext.Analyses
            .Include(a => a.Results)
            .FirstOrDefaultAsync(a => a.Id == analysis.Id);

        Assert.NotNull(refreshedAnalysis);
        Assert.Equal(AnalysisStatus.Completed, refreshedAnalysis.Status);
        Assert.Single(refreshedAnalysis.Results);
    }

    /// <summary>
    /// Scenario D: Неполные / повреждённые результаты при recovery.
    /// Если в БД статус задачи Completed, но результаты отсутствуют или не совпадают с ожидаемыми,
    /// восстановление отклоняется, сервис возвращает false, коммит помечается как неоднозначный,
    /// очистка каталога ЗАПРЕЩЕНА.
    /// </summary>
    [Fact]
    public async Task ScenarioD_ExecuteAnalysisAsync_WhenResultsInconsistentOnRecovery_RejectsCompletionAndRetainsWorkspace()
    {
        var observer = new TestCommitRecoveryObserver();
        var (analysis, token, workspaceDir, mockSourceService, service) =
            await SetupAnalysisEnvironmentAsync(
                _fixture.ConnectionString,
                observer: observer,
                repoDecorator: inner => new InconsistentResultsRepository(inner));

        var result = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.False(result);
        Assert.True(observer.CommitStarted);
        Assert.NotNull(observer.CaughtCommitException);
        Assert.True(observer.RecoveryStarted);
        Assert.Equal(AnalysisStatus.Completed, observer.InspectedStatus);
        Assert.NotEqual(true, observer.RecoverySuccess);

        mockSourceService.Verify(x => x.CleanupAsync(analysis.Id, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(Directory.Exists(workspaceDir), "Рабочий каталог обязан быть сохранен при поврежденных результатах.");
    }

    /// <summary>
    /// Scenario E: Чужой ExecutionToken при recovery.
    /// Если в БД статус задачи Completed, но ExecutionToken принадлежит другому воркеру (аренда истекла и задачу перехватили),
    /// восстановление отклоняется, сервис возвращает false, чужая задача не затрагивается,
    /// очистка каталога ЗАПРЕЩЕНА.
    /// </summary>
    [Fact]
    public async Task ScenarioE_ExecuteAnalysisAsync_WhenForeignExecutionTokenDetectedOnRecovery_RejectsCompletionAndDoesNotTouchTask()
    {
        var observer = new TestCommitRecoveryObserver();
        var (analysis, token, workspaceDir, mockSourceService, service) =
            await SetupAnalysisEnvironmentAsync(
                _fixture.ConnectionString,
                observer: observer,
                repoDecorator: inner => new ForeignTokenRepository(inner));

        var result = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.False(result);
        Assert.True(observer.CommitStarted);
        Assert.NotNull(observer.CaughtCommitException);
        Assert.True(observer.RecoveryStarted);
        Assert.Equal(AnalysisStatus.Completed, observer.InspectedStatus);
        Assert.NotEqual(true, observer.RecoverySuccess);

        mockSourceService.Verify(x => x.CleanupAsync(analysis.Id, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(Directory.Exists(workspaceDir), "Рабочий каталог обязан быть сохранен при чужом execution token.");
    }

    /// <summary>
    /// Scenario F: Идемпотентность повторного восстановления.
    /// Повторная попытка выполнить уже завершённый анализ с тем же токеном
    /// отклоняется на этапе валидации и не приводит к дублированию результатов в БД.
    /// </summary>
    [Fact]
    public async Task ScenarioF_ExecuteAnalysisAsync_IdempotentRepeatedRecovery_DoesNotDuplicateResults()
    {
        await using var proxy = PostgreSqlLoopbackFaultProxy.Create(_fixture.ConnectionString);
        var proxiedConnString = proxy.GetProxiedConnectionString(_fixture.ConnectionString);

        var observer = new TestCommitRecoveryObserver();
        var (analysis, token, workspaceDir, mockSourceService, service) =
            await SetupAnalysisEnvironmentAsync(proxiedConnString, observer: observer);

        proxy.DropOnCommitAck = true;

        var firstResult = await service.ExecuteAnalysisAsync(analysis.Id, token);
        Assert.True(firstResult);
        Assert.True(observer.RecoverySuccess == true);

        await using var verifyContext = CreateDbContext();
        var resultsAfterFirst = await verifyContext.AnalysisResults
            .Where(r => r.AnalysisId == analysis.Id)
            .ToListAsync();

        Assert.Single(resultsAfterFirst);

        // Повторная попытка выполнить завершённый анализ с тем же токеном
        var secondResult = await service.ExecuteAnalysisAsync(analysis.Id, token);
        Assert.False(secondResult);

        var resultsAfterSecond = await verifyContext.AnalysisResults
            .Where(r => r.AnalysisId == analysis.Id)
            .ToListAsync();

        // Количество результатов строго равно 1 (дублирование отсутствует)
        Assert.Single(resultsAfterSecond);
        Assert.Equal(resultsAfterFirst[0].Id, resultsAfterSecond[0].Id);
    }

    /// <summary>
    /// Scenario G: Восстановление после программного исключения в post-commit callback через DbTransactionInterceptor.
    /// ВНИМАНИЕ: Данный сценарий проверяет реакцию на программное исключение в коллбеке EF Core (DbTransactionInterceptor),
    /// а НЕ реальную сетевую потерю ACK-пакета на уровне TCP-сокета (которая проверяется в Scenario A).
    /// </summary>
    [Fact]
    public async Task ScenarioG_ExecuteAnalysisAsync_RecoversFromPostCommitCallbackFault_ViaInterceptor()
    {
        var observer = new TestCommitRecoveryObserver();
        var (analysis, token, workspaceDir, mockSourceService, service) =
            await SetupAnalysisEnvironmentAsync(_fixture.ConnectionString, addInterceptor: true, observer: observer);

        // Активируем fault injection: исключение ровно в пост-коммит коллбеке
        _interceptor.TriggerFault = true;

        var result = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.True(result);
        Assert.True(observer.CommitStarted);
        Assert.NotNull(observer.CaughtCommitException);
        Assert.True(observer.RecoveryStarted);
        Assert.True(observer.RecoverySuccess == true);

        await using var verifyContext = CreateDbContext();
        var refreshedAnalysis = await verifyContext.Analyses
            .Include(a => a.Results)
            .FirstOrDefaultAsync(a => a.Id == analysis.Id);

        Assert.NotNull(refreshedAnalysis);
        Assert.Equal(AnalysisStatus.Completed, refreshedAnalysis.Status);
        Assert.Null(refreshedAnalysis.LeaseExpiration);
        Assert.NotNull(refreshedAnalysis.CompletedAt);

        Assert.Single(refreshedAnalysis.Results);
        Assert.Equal("NET001", refreshedAnalysis.Results.First().Rule);

        mockSourceService.Verify(x => x.CleanupAsync(analysis.Id, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(Directory.Exists(workspaceDir));
    }

    /// <summary>
    /// Штатное выполнение коммита без сбоев (DropOnCommitAck = false).
    /// Прокси прозрачно пропускает весь трафик, CommitAckIntercepted остается false, recovery branch не вызывается.
    /// </summary>
    [Fact]
    public async Task NormalExecutionThroughProxy_SucceedsWithoutInterception()
    {
        await using var proxy = PostgreSqlLoopbackFaultProxy.Create(_fixture.ConnectionString);
        var proxiedConnString = proxy.GetProxiedConnectionString(_fixture.ConnectionString);

        var observer = new TestCommitRecoveryObserver();
        var (analysis, token, workspaceDir, mockSourceService, service) =
            await SetupAnalysisEnvironmentAsync(proxiedConnString, observer: observer);

        // Сбои выключены
        proxy.DropOnCommitAck = false;
        proxy.DropOnClientCommit = false;

        var result = await service.ExecuteAnalysisAsync(analysis.Id, token);

        Assert.True(result);
        Assert.False(proxy.CommitAckIntercepted, "При штатной работе перехват ACK не должен срабатывать.");
        Assert.False(proxy.ClientSocketAborted);

        // Ветка восстановления НЕ должна вызываться
        Assert.False(observer.RecoveryStarted, "Recovery branch не должна вызываться при штатном коммите.");
        Assert.Null(observer.CaughtCommitException);

        await using var verifyContext = CreateDbContext();
        var refreshedAnalysis = await verifyContext.Analyses
            .Include(a => a.Results)
            .FirstOrDefaultAsync(a => a.Id == analysis.Id);

        Assert.NotNull(refreshedAnalysis);
        Assert.Equal(AnalysisStatus.Completed, refreshedAnalysis.Status);
        Assert.Single(refreshedAnalysis.Results);

        mockSourceService.Verify(x => x.CleanupAsync(analysis.Id, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(Directory.Exists(workspaceDir));
    }

    /// <summary>
    /// Scenario H: Сквозная проверка жизненного цикла при неоднозначном коммите.
    /// Когда сервер PostgreSQL зафиксировал COMMIT, но клиент потерял ACK, и recovery-проверка БД временно недоступна,
    /// первый воркер возвращает false и не трогает БД.
    /// Следующий цикл очереди (TryAcquireNextPendingOrStaleAsync) не должен повторно захватывать уже завершённую задачу,
    /// её статус остаётся Completed, результаты не дублируются, новый ExecutionToken не назначается.
    /// </summary>
    [Fact]
    public async Task ScenarioH_AmbiguousCommit_NextWorkerCycleDoesNotReacquireCompletedAnalysis()
    {
        await using var proxy = PostgreSqlLoopbackFaultProxy.Create(_fixture.ConnectionString);
        var proxiedConnString = proxy.GetProxiedConnectionString(_fixture.ConnectionString);

        var observer = new TestCommitRecoveryObserver();
        var (analysis, token, workspaceDir, mockSourceService, service) =
            await SetupAnalysisEnvironmentAsync(proxiedConnString, observer: observer);

        // Обрываем ACK коммита И сбрасываем любые последующие новые подключения к прокси (recovery DB check fails)
        proxy.DropOnCommitAck = true;
        proxy.RejectNewConnectionsAfterDrop = true;

        var result = await service.ExecuteAnalysisAsync(analysis.Id, token);

        // 1. Первый воркер возвращает false (коммит неоднозначен)
        Assert.False(result);
        Assert.True(proxy.CommitDetected);
        Assert.True(proxy.CommitAckIntercepted);

        // 2. Каталог не удалён воркером, так как исход был неоднозначным
        mockSourceService.Verify(x => x.CleanupAsync(analysis.Id, token, It.IsAny<IAnalysisLeaseGuard>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(Directory.Exists(workspaceDir));

        // 3. Завершаем первый воркер и проверяем состояние в PostgreSQL напрямую:
        // Сервер PostgreSQL фактически зафиксировал транзакцию коммита в БД!
        await using (var verifyContext = CreateDbContext())
        {
            var dbAnalysis = await verifyContext.Analyses
                .Include(a => a.Results)
                .FirstOrDefaultAsync(a => a.Id == analysis.Id);

            Assert.NotNull(dbAnalysis);
            Assert.Equal(AnalysisStatus.Completed, dbAnalysis.Status);
            Assert.Equal(token, dbAnalysis.ExecutionToken);
            Assert.Single(dbAnalysis.Results);
            Assert.NotNull(dbAnalysis.CompletedAt);

            // Имитируем истечение времени аренды в строке БД (даже если прошло много времени):
            await verifyContext.Database.ExecuteSqlRawAsync(
                @"UPDATE ""Analyses"" SET ""LeaseExpiration"" = clock_timestamp() - interval '1 hour' WHERE ""Id"" = {0}",
                analysis.Id);
        }

        // 4. Следующий цикл очереди (новый воркер):
        // Пытается захватить следующую задачу через реальный AnalysisRepository на чистом соединении
        await using var nextWorkerContext = CreateDbContext();
        var nextWorkerRepo = new AnalysisRepository(nextWorkerContext);

        var nextTask = await nextWorkerRepo.TryAcquireNextPendingOrStaleAsync(TimeSpan.FromMinutes(2));

        // 5. Завершённая задача НИ ПРИ КАКИХ УСЛОВИЯХ не должна быть захвачена повторно!
        if (nextTask != null)
        {
            Assert.NotEqual(analysis.Id, nextTask.Id);
        }

        // 6. Повторно проверяем запись в PostgreSQL: статус остался Completed, токен не изменился, дублей находок нет
        await using (var finalContext = CreateDbContext())
        {
            var finalAnalysis = await finalContext.Analyses
                .Include(a => a.Results)
                .FirstOrDefaultAsync(a => a.Id == analysis.Id);

            Assert.NotNull(finalAnalysis);
            Assert.Equal(AnalysisStatus.Completed, finalAnalysis.Status);
            Assert.Equal(token, finalAnalysis.ExecutionToken);
            Assert.Single(finalAnalysis.Results);
        }
    }

    /// <summary>
    /// Scenario I: PostgreSQL не получил команду COMMIT из-за реального сетевого обрыва
    /// на стороне клиента. Транзакция не фиксируется сервером и откатывается.
    /// После recovery сервис обнаруживает Running. После истечения lease следующий
    /// цикл очереди безопасно захватывает задачу с новым ExecutionToken и AttemptCount.
    /// </summary>
    [Fact]
    public async Task ScenarioI_AmbiguousCommit_WhenCommitWasNotExecuted_AllowsSafeReacquisitionAfterLeaseExpiry()
    {
        await using var proxy = PostgreSqlLoopbackFaultProxy.Create(_fixture.ConnectionString);
        var proxiedConnString = proxy.GetProxiedConnectionString(_fixture.ConnectionString);

        var observer = new TestCommitRecoveryObserver();

        var (analysis, token, workspaceDir, mockSourceService, service) =
            await SetupAnalysisEnvironmentAsync(proxiedConnString, observer: observer);

        // SetupAnalysisEnvironmentAsync создаёт Running-анализ.
        // Устанавливаем AttemptCount = 1, то есть состояние после первой попытки захвата.
        await using (var initialAttemptContext = CreateDbContext())
        {
            var affected = await initialAttemptContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE "Analyses"
                SET "AttemptCount" = 1
                WHERE "Id" = {0}
                """,
                analysis.Id);

            Assert.Equal(1, affected);
        }

        const int initialAttemptCount = 1;

        // Разрываем клиентский TCP-сокет ДО передачи COMMIT серверу PostgreSQL.
        // PostgreSQL не получает команду COMMIT, поэтому транзакция откатывается.
        proxy.DropOnClientCommit = true;

        var executionResult = await service.ExecuteAnalysisAsync(analysis.Id, token);

        // 1. Подтверждаем, что произошёл настоящий сетевой обрыв до выполнения COMMIT.
        Assert.True(
            proxy.CommitDetected,
            "Прокси должна обнаружить команду COMMIT в клиентском потоке.");

        Assert.False(
            proxy.CommitAckIntercepted,
            "Подтверждение COMMIT не должно перехватываться: сервер не получил команду COMMIT.");

        Assert.True(
            proxy.ClientSocketAborted,
            "Клиентский TCP-сокет должен быть разорван через RST.");

        // 2. Проверяем recovery-ветку AnalysisService.
        Assert.True(observer.CommitStarted);
        Assert.NotNull(observer.CaughtCommitException);
        Assert.True(observer.RecoveryStarted);

        Assert.Equal(
            AnalysisStatus.Running,
            observer.InspectedStatus);

        Assert.Equal(
            token,
            observer.InspectedExecutionToken);

        Assert.False(
            observer.RecoverySuccess,
            "Recovery не должен признавать анализ завершённым, если COMMIT не был выполнен.");

        // 3. Анализ не должен считаться завершённым.
        Assert.False(executionResult);

        // 4. Проверяем фактическое состояние PostgreSQL после rollback.
        await using (var verifyContext = CreateDbContext())
        {
            var persistedAnalysis = await verifyContext.Analyses
                .Include(a => a.Results)
                .FirstOrDefaultAsync(a => a.Id == analysis.Id);

            Assert.NotNull(persistedAnalysis);

            Assert.Equal(AnalysisStatus.Running, persistedAnalysis.Status);
            Assert.Equal(token, persistedAnalysis.ExecutionToken);
            Assert.Equal(initialAttemptCount, persistedAnalysis.AttemptCount);

            Assert.Empty(persistedAnalysis.Results);
            Assert.Null(persistedAnalysis.CompletedAt);
            Assert.NotNull(persistedAnalysis.LeaseExpiration);
        }

        // 5. При неподтверждённом результате автоматическая очистка запрещена.
        mockSourceService.Verify(
            x => x.CleanupAsync(
                analysis.Id,
                token,
                It.IsAny<IAnalysisLeaseGuard>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        Assert.True(
            Directory.Exists(workspaceDir),
            "Рабочий каталог должен сохраняться после неуспешного recovery.");

        // 6. Имитируем истечение lease серверным временем PostgreSQL.
        await using (var expireContext = CreateDbContext())
        {
            var affected = await expireContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE "Analyses"
                SET "LeaseExpiration" = clock_timestamp() - interval '5 seconds'
                WHERE "Id" = {0}
                  AND "Status" = {1}
                """,
                analysis.Id,
                (int)AnalysisStatus.Running);

            Assert.Equal(1, affected);
        }

        // 7. Следующий цикл очереди должен повторно захватить незавершённую задачу.
        await using var nextWorkerContext = CreateDbContext();

        var nextWorkerRepository = new AnalysisRepository(nextWorkerContext);

        var reacquired = await nextWorkerRepository.TryAcquireNextPendingOrStaleAsync(
            TimeSpan.FromMinutes(2));

        Assert.NotNull(reacquired);
        Assert.Equal(analysis.Id, reacquired.Id);
        Assert.Equal(AnalysisStatus.Running, reacquired.Status);

        // Новый запуск обязан получить новый токен владения.
        Assert.NotEqual(token, reacquired.ExecutionToken!.Value);

        // AttemptCount должен увеличиться ровно на одну попытку.
        Assert.Equal(
            initialAttemptCount + 1,
            reacquired.AttemptCount);

        // 8. Проверяем итоговое состояние в PostgreSQL.
        var finalAnalysis = await nextWorkerContext.Analyses
            .Include(a => a.Results)
            .FirstOrDefaultAsync(a => a.Id == analysis.Id);

        Assert.NotNull(finalAnalysis);

        Assert.Equal(AnalysisStatus.Running, finalAnalysis.Status);
        Assert.Equal(reacquired.ExecutionToken, finalAnalysis.ExecutionToken);
        Assert.Equal(initialAttemptCount + 1, finalAnalysis.AttemptCount);

        Assert.Empty(finalAnalysis.Results);
        Assert.Null(finalAnalysis.CompletedAt);
    }
}
