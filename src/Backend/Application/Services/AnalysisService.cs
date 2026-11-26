using Application.Analyzers;
using Application.Exceptions;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Domain.Entities;
using Domain.Enums;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Application.Services;

public class AnalysisService(
    IAnalysisRepository analysisRepository,
    ICommitRepository commitRepository,
    IRepositorySourceService repositorySourceService,
    IEnumerable<IAnalyzer> analyzers,
    IGitHubCredentialService gitHubCredentialService,
    IAnalysisLeaseGuardProvider leaseGuardProvider,
    IAnalysisRetryPolicy? retryPolicy = null,
    IAnalysisCommitRecoveryObserver? recoveryObserver = null,
    ILogger<AnalysisService>? logger = null)
{
    private readonly IGitHubCredentialService _gitHubCredentialService = gitHubCredentialService ?? throw new ArgumentNullException(nameof(gitHubCredentialService));
    private readonly IAnalysisLeaseGuardProvider _leaseGuardProvider = leaseGuardProvider ?? throw new ArgumentNullException(nameof(leaseGuardProvider));
    private readonly IAnalysisRetryPolicy? _retryPolicy = retryPolicy;
    private readonly IAnalysisCommitRecoveryObserver? _recoveryObserver = recoveryObserver;
    private readonly ILogger<AnalysisService>? _logger = logger;
    /// <summary>
    /// Создаёт задачу анализа коммита со статусом Pending и сохраняет её в очередь задач.
    /// Не выполняет запуск анализаторов и не скачивает исходный код.
    /// </summary>
    public async Task<Analysis> EnqueueAsync(int commitId, CancellationToken cancellationToken = default)
    {
        var commit = await commitRepository.GetByIdAsync(commitId, cancellationToken)
            ?? throw new InvalidOperationException("Коммит не найден.");

        var transaction = await analysisRepository.BeginEnqueueTransactionAsync(commitId, cancellationToken);
        try
        {
            var hasActive = await analysisRepository.HasActiveAnalysisAsync(commitId, cancellationToken);
            if (hasActive)
            {
                throw new AnalysisAlreadyQueuedException("Анализ для данного коммита уже находится в очереди или выполняется.");
            }

            var analysis = new Analysis
            {
                CommitId = commitId,
                Status = AnalysisStatus.Pending,
                CreatedAt = DateTime.UtcNow,
                StartedAt = null,
                CompletedAt = null,
                LeaseExpiration = null,
                ExecutionToken = null,
                ErrorMessage = null
            };

            await analysisRepository.AddAsync(analysis, cancellationToken);

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return analysis;
        }
        finally
        {
            if (transaction != null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private static readonly TimeSpan CriticalOperationTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan[] GuardAcquireRetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200)
    ];

    private sealed record GuardedExecutionOutcome(
        bool Succeeded,
        bool OwnershipConfirmed,
        string? ErrorMessage,
        IReadOnlyList<AnalysisResult> Results);

    /// <summary>
    /// Выполняет анализ для ранее захваченной задачи с активным токеном выполнения.
    /// Периодически продлевает аренду, скачивает исходники, запускает анализаторы,
    /// сохраняет результаты и гарантирует очистку временных файлов.
    /// </summary>
    public async Task<bool> ExecuteAnalysisAsync(
        int analysisId,
        Guid executionToken,
        CancellationToken cancellationToken = default,
        TimeSpan? leaseDuration = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Analysis? analysis;
        try
        {
            analysis = await analysisRepository.GetByIdAsync(analysisId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }

        if (analysis == null || analysis.ExecutionToken != executionToken || analysis.Status != AnalysisStatus.Running)
        {
            return false;
        }

        var guard = await AcquireGuardWithRetryAsync(analysisId, executionToken, cancellationToken);
        if (guard == null)
        {
            return false;
        }

        try
        {
            var effectiveLeaseDuration = leaseDuration ?? TimeSpan.FromMinutes(2);
            var outcome = await ExecuteGuardedCoreAsync(
                analysis,
                executionToken,
                guard,
                effectiveLeaseDuration,
                allowRetry: true,
                rethrowFailures: false,
                cancellationToken);

            return outcome.Succeeded;
        }
        finally
        {
            await guard.DisposeAsync();
        }
    }

    private async Task<IAnalysisLeaseGuard?> AcquireGuardWithRetryAsync(
        int analysisId,
        Guid executionToken,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var guard = await _leaseGuardProvider.AcquireGuardAsync(analysisId, executionToken, cancellationToken);
            if (guard != null && guard.IsAcquired)
            {
                return guard;
            }

            if (guard != null)
            {
                await guard.DisposeAsync();
            }

            if (attempt >= GuardAcquireRetryDelays.Length)
            {
                _logger?.LogWarning(
                    "Не удалось получить охранную блокировку рабочего пространства анализа {AnalysisId} с токеном {ExecutionToken} после {AttemptCount} попыток.",
                    analysisId,
                    executionToken,
                    attempt + 1);
                return null;
            }

            _logger?.LogDebug(
                "Охранная блокировка рабочего пространства анализа {AnalysisId} временно занята (попытка {Attempt}); повтор через {DelayMs} мс.",
                analysisId,
                attempt + 1,
                GuardAcquireRetryDelays[attempt].TotalMilliseconds);

            await Task.Delay(GuardAcquireRetryDelays[attempt], cancellationToken);

            Analysis? current;
            try
            {
                current = await analysisRepository.GetByIdAsync(analysisId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }

            if (current == null || current.ExecutionToken != executionToken || current.Status != AnalysisStatus.Running)
            {
                return null;
            }
        }
    }

    private async Task<GuardedExecutionOutcome> ExecuteGuardedCoreAsync(
        Analysis analysis,
        Guid executionToken,
        IAnalysisLeaseGuard guard,
        TimeSpan effectiveLeaseDuration,
        bool allowRetry,
        bool rethrowFailures,
        CancellationToken cancellationToken)
    {
        var isInitialOwner = await guard.ValidateOwnershipAsync([(int)AnalysisStatus.Running], cancellationToken);
        if (!isInitialOwner)
        {
            return new GuardedExecutionOutcome(false, false, "Не удалось подтвердить начальное владение арендой анализа.", Array.Empty<AnalysisResult>());
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewalState = new LeaseRenewalState();
        var leaseRenewalTask = StartLeaseRenewalAsync(analysis.Id, executionToken, effectiveLeaseDuration, guard, linkedCts, renewalState);

        var ownershipConfirmed = true;
        var workspaceCleaned = false;
        bool isAmbiguousCommit = false;
        string? terminalErrorMessage = null;

        try
        {
            var commit = (analysis.Commit?.Project != null ? analysis.Commit : null)
                ?? await commitRepository.GetByIdAsync(analysis.CommitId, linkedCts.Token)
                ?? throw new InvalidOperationException($"Коммит {analysis.CommitId} не найден.");

            if (commit.Project == null)
            {
                throw new InvalidOperationException($"Проект для коммита {commit.Id} не найден.");
            }

            var personalAccessToken = await _gitHubCredentialService.GetUserTokenAsync(commit.Project.OwnerId, linkedCts.Token);

            if (string.IsNullOrWhiteSpace(personalAccessToken))
            {
                throw new GitHubIntegrationException(
                    GitHubErrorKind.TokenRequired,
                    "Для выполнения анализа требуется подключение персонального токена GitHub владельца проекта.");
            }

            var repositoryPath = await repositorySourceService.DownloadAndExtractAsync(
                commit.Project.RepoUrl,
                commit.CommitHash,
                analysis.Id,
                executionToken,
                personalAccessToken,
                guard,
                linkedCts.Token);

            var context = new AnalyzerContext
            {
                RepositoryPath = repositoryPath,
                CommitHash = commit.CommitHash,
                AnalysisId = analysis.Id,
                ExecutionToken = executionToken,
                LeaseGuard = guard
            };

            var results = new List<AnalysisResult>();
            var usefulWorkCompleted = false;
            var totalAnalyzersCount = 0;

            foreach (var analyzer in analyzers)
            {
                totalAnalyzersCount++;
                linkedCts.Token.ThrowIfCancellationRequested();
                if (renewalState.IsTerminal)
                {
                    throw new OperationCanceledException(linkedCts.Token);
                }

                bool isStillOwner;
                try
                {
                    isStillOwner = await guard.ValidateOwnershipAsync([(int)AnalysisStatus.Running], linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    ownershipConfirmed = false;
                    return new GuardedExecutionOutcome(false, false, "Не удалось подтвердить владение арендой перед запуском анализатора.", Array.Empty<AnalysisResult>());
                }

                if (!isStillOwner)
                {
                    ownershipConfirmed = false;
                    return new GuardedExecutionOutcome(false, false, "Владение арендой анализа было потеряно перед запуском анализатора.", Array.Empty<AnalysisResult>());
                }

                IReadOnlyList<AnalyzerFinding> findings;

                if (analyzer.Type == AnalyzerType.Database)
                {
                    try
                    {
                        findings = await analyzer.AnalyzeAsync(context, linkedCts.Token);

                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }

                        if (linkedCts.IsCancellationRequested || renewalState.IsTerminal)
                        {
                            throw new OperationCanceledException(linkedCts.Token);
                        }

                        if (analyzer is IDatabaseAnalyzer dbAnalyzer)
                        {
                            var status = dbAnalyzer.ExecutionStatus;
                            if (status is DatabaseAnalysisExecutionStatus.Success or DatabaseAnalysisExecutionStatus.PartialSuccess)
                            {
                                usefulWorkCompleted = true;
                            }
                        }
                        else
                        {
                            var hasFatalDbError = findings.Any(f =>
                                f.Rule is DatabaseAnalyzerRules.ContainerTimeout
                                       or DatabaseAnalyzerRules.ContainerError
                                       or DatabaseAnalyzerRules.InternalAnalyzerError
                                       or DatabaseAnalyzerRules.ExtractionError
                                       or "DB_CONTAINER_ERROR");

                            var isInformationSkipped = findings.Any(f => f.Rule == DatabaseAnalyzerRules.MissingDbContext);

                            if (!hasFatalDbError && !isInformationSkipped)
                            {
                                usefulWorkCompleted = true;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Отмена вызывающего кода пробрасывается наверх
                        throw;
                    }
                    catch (OperationCanceledException) when (renewalState.StopReason is LeaseRenewalStopReason.OwnershipLost or LeaseRenewalStopReason.OwnershipValidationException)
                    {
                        // Подтверждённая потеря владения или ошибка проверки владения в цикле renewal
                        ownershipConfirmed = false;
                        throw;
                    }
                    catch (OperationCanceledException ex)
                    {
                        // Если остановка вызвана сбоем продления аренды в БД, прерываем конвейер
                        if (renewalState.StopReason is LeaseRenewalStopReason.RenewalException)
                        {
                            throw;
                        }

                        bool isStillOwnerAfterCancel = false;
                        try
                        {
                            isStillOwnerAfterCancel = await guard.ValidateOwnershipAsync([(int)AnalysisStatus.Running], CancellationToken.None);
                        }
                        catch { }

                        if (!isStillOwnerAfterCancel)
                        {
                            ownershipConfirmed = false;
                            throw;
                        }

                        // Если токен отменен или зафиксирована терминальная причина, прерываем
                        if (linkedCts.IsCancellationRequested && renewalState.IsTerminal)
                        {
                            throw;
                        }

                        findings =
                        [
                            new AnalyzerFinding
                            {
                                Rule = DatabaseAnalyzerRules.InternalAnalyzerError,
                                FilePath = string.Empty,
                                LineNumber = 0,
                                Message = $"Операция анализатора базы данных была отменена: {ex.Message}",
                                Severity = Severity.Warning,
                                Recommendation = "Проверьте конфигурацию и таймауты изолированного анализатора базы данных."
                            }
                        ];
                    }
                    catch (Exception ex)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }

                        if (linkedCts.IsCancellationRequested || renewalState.IsTerminal)
                        {
                            throw new OperationCanceledException(linkedCts.Token);
                        }

                        findings =
                        [
                            new AnalyzerFinding
                            {
                                Rule = DatabaseAnalyzerRules.InternalAnalyzerError,
                                FilePath = string.Empty,
                                LineNumber = 0,
                                Message = $"Ошибка анализатора базы данных: {SanitizeErrorMessage(ex)}",
                                Severity = Severity.Warning,
                                Recommendation = "Проверьте структуру моделей Entity Framework Core и параметры конфигурации базы данных."
                            }
                        ];
                    }
                }
                else
                {
                    findings = await analyzer.AnalyzeAsync(context, linkedCts.Token);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    if (linkedCts.IsCancellationRequested || renewalState.IsTerminal)
                    {
                        throw new OperationCanceledException(linkedCts.Token);
                    }

                    usefulWorkCompleted = true;
                }

                foreach (var finding in findings)
                {
                    results.Add(new AnalysisResult
                    {
                        AnalysisId = analysis.Id,
                        FilePath = finding.FilePath,
                        LineNumber = finding.LineNumber,
                        Rule = finding.Rule,
                        Message = finding.Message,
                        Severity = finding.Severity,
                        Recommendation = finding.Recommendation,
                        AnalyzerType = analyzer.Type
                    });
                }
            }

            // TD-002: Если все анализаторы завершились ошибкой или ни один не выполнил полезной работы
            if (!usefulWorkCompleted && totalAnalyzersCount > 0)
            {
                renewalState.RequestNormalStop();
                await StopLeaseRenewalAsync(linkedCts, leaseRenewalTask);

                if (renewalState.IsTerminal)
                {
                    throw new OperationCanceledException(linkedCts.Token);
                }

                bool isStillOwnerOnFail = false;
                try
                {
                    isStillOwnerOnFail = await guard.ValidateOwnershipAsync([(int)AnalysisStatus.Running], CancellationToken.None);
                }
                catch
                {
                    isStillOwnerOnFail = false;
                }

                var failMessage = "Все анализаторы завершились ошибкой или не смогли предоставить результаты.";
                if (isStillOwnerOnFail)
                {
                    var failed = await analysisRepository.FailAsync(analysis.Id, executionToken, failMessage, guard, CancellationToken.None);
                    if (!failed)
                    {
                        ownershipConfirmed = false;
                    }
                    else
                    {
                        terminalErrorMessage = failMessage;
                    }
                }
                else
                {
                    ownershipConfirmed = false;
                }

                return new GuardedExecutionOutcome(false, ownershipConfirmed, terminalErrorMessage ?? failMessage, Array.Empty<AnalysisResult>());
            }

            renewalState.RequestNormalStop();
            await StopLeaseRenewalAsync(linkedCts, leaseRenewalTask);

            if (renewalState.IsTerminal)
            {
                throw new OperationCanceledException(linkedCts.Token);
            }

            bool isStillOwnerBeforeComplete = false;
            try
            {
                isStillOwnerBeforeComplete = await guard.ValidateOwnershipAsync([(int)AnalysisStatus.Running], CancellationToken.None);
            }
            catch
            {
                isStillOwnerBeforeComplete = false;
            }

            if (!isStillOwnerBeforeComplete)
            {
                ownershipConfirmed = false;
                return new GuardedExecutionOutcome(false, false, "Владение арендой анализа было потеряно перед сохранением результатов.", Array.Empty<AnalysisResult>());
            }

            int? guardProcessId = guard.ConnectionProcessId;
            bool completed = false;
            _recoveryObserver?.OnCommitStarted(analysis.Id, executionToken);
            try
            {
                completed = await analysisRepository.CompleteAsync(analysis.Id, executionToken, results, guard, CancellationToken.None);
            }
            catch (Exception commitEx) when (commitEx is not OperationCanceledException)
            {
                _recoveryObserver?.OnCommitFailed(analysis.Id, executionToken, commitEx);
                _recoveryObserver?.OnRecoveryStarted(analysis.Id, executionToken);

                // Q-04: Ambiguous COMMIT ACK Loss recovery
                // Если произошёл сбой сети/TCP при фиксации транзакции коммита в СУБД,
                // исход коммита считается неопределённым (ambiguous) до явного подтверждения.
                isAmbiguousCommit = true;

                int? recoveryProcessId = null;
                Analysis? current = null;
                Exception? checkException = null;

                try
                {
                    var recoveryData = await analysisRepository.GetAnalysisWithProcessIdAsync(analysis.Id, CancellationToken.None);
                    current = recoveryData.Analysis;
                    recoveryProcessId = recoveryData.ProcessId;
                }
                catch (Exception checkEx) when (checkEx is not OperationCanceledException)
                {
                    checkException = checkEx;
                }

                if (checkException != null)
                {
                    ownershipConfirmed = false;
                    isAmbiguousCommit = true;
                    _recoveryObserver?.OnRecoveryCompleted(analysis.Id, executionToken, false);

                    var aggEx = new AggregateException(
                        $"Сбой фиксации коммита анализа {analysis.Id} с последующей невозможностью проверить состояние в БД. Статус коммита неоднозначен (ambiguous).",
                        commitEx,
                        checkException);

                    _logger?.LogError(aggEx,
                        "Неоднозначный коммит для анализа {AnalysisId}: CompleteAsync завершился ошибкой, и проверка состояния в БД также завершилась ошибкой. Автоматический Fail/Retry отменен во избежание повреждения данных.",
                        analysis.Id);

                    return new GuardedExecutionOutcome(false, false, aggEx.Message, Array.Empty<AnalysisResult>());
                }

                _recoveryObserver?.OnRecoveryStateInspected(
                    analysis.Id,
                    executionToken,
                    current?.Status,
                    current?.ExecutionToken,
                    guardProcessId,
                    recoveryProcessId);

                if (current != null &&
                    current.Status == AnalysisStatus.Completed &&
                    current.ExecutionToken == executionToken &&
                    AreResultsConsistent(current.Results, results))
                {
                    completed = true;
                    ownershipConfirmed = true;
                    isAmbiguousCommit = false;
                    _recoveryObserver?.OnRecoveryCompleted(analysis.Id, executionToken, true);
                }
                else if (current != null &&
                         current.Status == AnalysisStatus.Running &&
                         current.ExecutionToken == executionToken)
                {
                    isAmbiguousCommit = false;
                    _recoveryObserver?.OnRecoveryCompleted(analysis.Id, executionToken, false);
                    throw;
                }
                else if (current != null &&
                         current.Status == AnalysisStatus.Completed &&
                         current.ExecutionToken != executionToken)
                {
                    ownershipConfirmed = false;
                    isAmbiguousCommit = false;
                    _recoveryObserver?.OnRecoveryCompleted(analysis.Id, executionToken, false);
                    _logger?.LogWarning(
                        "Восстановление коммита анализа {AnalysisId} отклонено: обнаружен сторонний execution token {CurrentToken} (ожидался {ExpectedToken}).",
                        analysis.Id, current.ExecutionToken, executionToken);
                    return new GuardedExecutionOutcome(false, false, "Восстановление коммита отклонено из-за несовпадения execution token.", Array.Empty<AnalysisResult>());
                }
                else if (current != null &&
                         current.Status == AnalysisStatus.Completed &&
                         !AreResultsConsistent(current.Results, results))
                {
                    ownershipConfirmed = false;
                    isAmbiguousCommit = true;
                    _recoveryObserver?.OnRecoveryCompleted(analysis.Id, executionToken, false);
                    _logger?.LogError(
                        "Восстановление коммита анализа {AnalysisId} отклонено: результаты в БД не согласованы с ожидаемыми (БД: {ActualCount}, Ожидалось: {ExpectedCount}). Коммит помечен как неоднозначный.",
                        analysis.Id, current.Results?.Count ?? 0, results.Count);
                    return new GuardedExecutionOutcome(false, false, "Результаты в БД не согласованы после сбоя коммита.", Array.Empty<AnalysisResult>());
                }
                else
                {
                    ownershipConfirmed = false;
                    isAmbiguousCommit = true;
                    _recoveryObserver?.OnRecoveryCompleted(analysis.Id, executionToken, false);
                    _logger?.LogError(
                        "Восстановление коммита анализа {AnalysisId} завершилось в непредвиденном состоянии (Статус: {Status}, Токен: {Token}). Коммит помечен как неоднозначный.",
                        analysis.Id, current?.Status, current?.ExecutionToken);
                    return new GuardedExecutionOutcome(false, false, "Непредвиденное состояние анализа после сбоя коммита.", Array.Empty<AnalysisResult>());
                }
            }

            if (!completed)
            {
                ownershipConfirmed = false;
                return new GuardedExecutionOutcome(false, false, "Не удалось зафиксировать завершение анализа в БД.", Array.Empty<AnalysisResult>());
            }

            return new GuardedExecutionOutcome(true, true, null, results);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            renewalState.TrySetTerminalReason(LeaseRenewalStopReason.ExternalCancellation);
            await StopLeaseRenewalAsync(linkedCts, leaseRenewalTask);

            bool isStillOwner = false;
            try
            {
                isStillOwner = await guard.ValidateOwnershipAsync([(int)AnalysisStatus.Running], CancellationToken.None);
            }
            catch
            {
                isStillOwner = false;
            }

            if (!isStillOwner)
            {
                ownershipConfirmed = false;
            }

            // При штатной остановке приложения НЕ помечаем задачу как Failed,
            // чтобы после истечения аренды она могла быть повторно захвачена.
            throw;
        }
        catch (OperationCanceledException ex)
        {
            await StopLeaseRenewalAsync(linkedCts, leaseRenewalTask);

            if (cancellationToken.IsCancellationRequested || renewalState.StopReason == LeaseRenewalStopReason.ExternalCancellation)
            {
                bool isStillOwnerOnCancel = false;
                try
                {
                    isStillOwnerOnCancel = await guard.ValidateOwnershipAsync([(int)AnalysisStatus.Running], CancellationToken.None);
                }
                catch
                {
                    isStillOwnerOnCancel = false;
                }

                if (!isStillOwnerOnCancel)
                {
                    ownershipConfirmed = false;
                }

                throw;
            }

            // 1. Подтверждённая потеря владения
            if (renewalState.StopReason == LeaseRenewalStopReason.OwnershipLost)
            {
                ownershipConfirmed = false;
                return new GuardedExecutionOutcome(false, false, "Владение задачей анализа было потеряно.", Array.Empty<AnalysisResult>());
            }

            // 2. Исключение при проверке владения: состояние владения неизвестно
            if (renewalState.StopReason == LeaseRenewalStopReason.OwnershipValidationException)
            {
                ownershipConfirmed = false;
                return new GuardedExecutionOutcome(false, false, "Не удалось подтвердить владение задачей анализа при продлении аренды.", Array.Empty<AnalysisResult>());
            }

            // 3. Проверяем актуальное состояние владения
            bool isStillOwner = false;
            try
            {
                isStillOwner = await guard.ValidateOwnershipAsync([(int)AnalysisStatus.Running], CancellationToken.None);
            }
            catch
            {
                isStillOwner = false;
            }

            if (!isStillOwner)
            {
                ownershipConfirmed = false;
                return new GuardedExecutionOutcome(false, false, "Владение задачей анализа было потеряно.", Array.Empty<AnalysisResult>());
            }

            // Владение ПОДТВЕРЖДЕНО!
            string errorMessage;
            if (renewalState.StopReason == LeaseRenewalStopReason.RenewalException)
            {
                errorMessage = renewalState.Exception != null
                    ? $"Сбой продления аренды анализа: {SanitizeErrorMessage(renewalState.Exception)}"
                    : "Не удалось продлить аренду анализа в базе данных.";
            }
            else
            {
                errorMessage = $"Операция анализа была отменена: {SanitizeErrorMessage(ex)}";
            }

            var failed = await analysisRepository.FailAsync(analysis.Id, executionToken, errorMessage, guard, CancellationToken.None);
            if (!failed)
            {
                ownershipConfirmed = false;
            }
            else
            {
                terminalErrorMessage = errorMessage;
            }

            if (rethrowFailures && failed)
            {
                analysis.Status = AnalysisStatus.Failed;
                analysis.CompletedAt = DateTime.UtcNow;
                analysis.ErrorMessage = errorMessage;
                throw;
            }

            return new GuardedExecutionOutcome(false, ownershipConfirmed, terminalErrorMessage ?? errorMessage, Array.Empty<AnalysisResult>());
        }
        catch (Exception ex)
        {
            await StopLeaseRenewalAsync(linkedCts, leaseRenewalTask);

            if (isAmbiguousCommit)
            {
                ownershipConfirmed = false;
                _logger?.LogWarning(ex, "Анализ {AnalysisId} находится в состоянии неоднозначного коммита. Вызов FailAsync/ScheduleRetryAsync отменен.", analysis.Id);
                return new GuardedExecutionOutcome(false, false, "Неоднозначный коммит анализа.", Array.Empty<AnalysisResult>());
            }

            if (cancellationToken.IsCancellationRequested || renewalState.StopReason == LeaseRenewalStopReason.ExternalCancellation)
            {
                bool isStillOwnerOnCancel = false;
                try
                {
                    isStillOwnerOnCancel = await guard.ValidateOwnershipAsync([(int)AnalysisStatus.Running], CancellationToken.None);
                }
                catch
                {
                    isStillOwnerOnCancel = false;
                }

                if (!isStillOwnerOnCancel)
                {
                    ownershipConfirmed = false;
                }

                throw;
            }

            if (renewalState.StopReason == LeaseRenewalStopReason.OwnershipLost ||
                renewalState.StopReason == LeaseRenewalStopReason.OwnershipValidationException)
            {
                ownershipConfirmed = false;
                return new GuardedExecutionOutcome(false, false, "Владение задачей анализа было потеряно.", Array.Empty<AnalysisResult>());
            }

            bool isStillOwner = false;
            try
            {
                isStillOwner = await guard.ValidateOwnershipAsync([(int)AnalysisStatus.Running], CancellationToken.None);
            }
            catch
            {
                isStillOwner = false;
            }

            if (isStillOwner)
            {
                string errorMessage;
                if (renewalState.StopReason == LeaseRenewalStopReason.RenewalException)
                {
                    errorMessage = renewalState.Exception != null
                        ? $"Сбой продления аренды анализа: {SanitizeErrorMessage(renewalState.Exception)}"
                        : "Не удалось продлить аренду анализа в базе данных.";
                }
                else
                {
                    errorMessage = SanitizeErrorMessage(ex);
                }

                if (allowRetry && _retryPolicy != null)
                {
                    var decision = _retryPolicy.Evaluate(ex, analysis.AttemptCount, analysis.MaxAttempts, DateTime.UtcNow);

                    if (decision.ShouldRetry)
                    {
                        var cleanedBeforeRetry = false;
                        try
                        {
                            using var cleanupTimeoutCts = new CancellationTokenSource(CriticalOperationTimeout);
                            await repositorySourceService.CleanupAsync(
                                analysis.Id,
                                executionToken,
                                guard,
                                cleanupTimeoutCts.Token);
                            cleanedBeforeRetry = true;
                        }
                        catch (Exception cleanupEx)
                        {
                            _logger?.LogWarning(
                                cleanupEx,
                                "Не удалось безопасно очистить рабочее пространство анализа {AnalysisId} с токеном {ExecutionToken} перед планированием повтора. Перевод в Pending отменён.",
                                analysis.Id,
                                executionToken);
                            ownershipConfirmed = false;
                        }

                        if (cleanedBeforeRetry)
                        {
                            workspaceCleaned = true;
                            var nextAttemptAt = DateTime.UtcNow + (decision.Delay ?? TimeSpan.FromSeconds(10));
                            var retryMessage = $"{errorMessage} [Повтор запланирован на {nextAttemptAt:u} UTC: {decision.Reason}]";

                            var (retryOwnershipConfirmed, retryOutcomeMessage) = await TryScheduleRetryOrRecoverAsync(
                                analysis.Id,
                                executionToken,
                                nextAttemptAt,
                                errorMessage,
                                retryMessage,
                                guard);

                            ownershipConfirmed = retryOwnershipConfirmed;
                            terminalErrorMessage = retryOutcomeMessage;
                        }
                    }
                    else
                    {
                        var failMessage = decision.IsPoisonPill
                            ? $"{errorMessage} [{decision.Reason}]"
                            : errorMessage;

                        var failed = await analysisRepository.FailAsync(analysis.Id, executionToken, failMessage, guard, CancellationToken.None);
                        if (!failed)
                        {
                            ownershipConfirmed = false;
                        }
                        else
                        {
                            terminalErrorMessage = failMessage;
                        }
                    }
                }
                else
                {
                    var failed = await analysisRepository.FailAsync(analysis.Id, executionToken, errorMessage, guard, CancellationToken.None);
                    if (!failed)
                    {
                        ownershipConfirmed = false;
                    }
                    else
                    {
                        terminalErrorMessage = errorMessage;
                    }

                    if (rethrowFailures && failed)
                    {
                        analysis.Status = AnalysisStatus.Failed;
                        analysis.CompletedAt = DateTime.UtcNow;
                        analysis.ErrorMessage = errorMessage;
                        throw;
                    }
                }
            }
            else
            {
                ownershipConfirmed = false;
            }

            return new GuardedExecutionOutcome(false, ownershipConfirmed, terminalErrorMessage, Array.Empty<AnalysisResult>());
        }
        finally
        {
            // Гарантируем остановку и завершение фоновой задачи продления аренды
            // на всех путях выхода до очистки рабочего пространства и освобождения guard.
            await StopLeaseRenewalAsync(linkedCts, leaseRenewalTask);

            // Очищаем временные файлы только если владение подтверждено и каталог ещё не был очищен перед повтором
            // (при потере владения или при невозможности подтвердить владение очистка запрещена)
            if (ownershipConfirmed && !workspaceCleaned)
            {
                try
                {
                    using var finallyCleanupTimeoutCts = new CancellationTokenSource(CriticalOperationTimeout);
                    await repositorySourceService.CleanupAsync(
                        analysis.Id,
                        executionToken,
                        guard,
                        finallyCleanupTimeoutCts.Token);
                }
                catch (DirectoryNotFoundException)
                {
                }
                catch (Exception cleanupEx)
                {
                    _logger?.LogWarning(
                        cleanupEx,
                        "Не удалось очистить рабочее пространство анализа {AnalysisId} с токеном {ExecutionToken} в блоке finally.",
                        analysis.Id,
                        executionToken);
                }
            }
        }
    }

    private async Task<(bool OwnershipConfirmed, string? TerminalErrorMessage)> TryScheduleRetryOrRecoverAsync(
        int analysisId,
        Guid executionToken,
        DateTime nextAttemptAt,
        string errorMessage,
        string retryMessage,
        IAnalysisLeaseGuard guard)
    {
        bool scheduled;
        try
        {
            using var scheduleTimeoutCts = new CancellationTokenSource(CriticalOperationTimeout);
            scheduled = await analysisRepository.ScheduleRetryAsync(
                analysisId,
                executionToken,
                nextAttemptAt,
                retryMessage,
                guard,
                scheduleTimeoutCts.Token);
        }
        catch (Exception scheduleEx)
        {
            _logger?.LogError(
                scheduleEx,
                "Ошибка при планировании повтора анализа {AnalysisId} с токеном {ExecutionToken}. Выполняется безопасное восстановление состояния.",
                analysisId,
                executionToken);

            return await RecoverAfterScheduleRetryFailureAsync(
                analysisId,
                executionToken,
                $"{errorMessage} [Ошибка планирования повтора: {SanitizeErrorMessage(scheduleEx)}]",
                guard,
                inspectDatabaseStateFirst: true);
        }

        if (scheduled)
        {
            return (true, retryMessage);
        }

        _logger?.LogWarning(
            "ScheduleRetryAsync вернул false для анализа {AnalysisId} с токеном {ExecutionToken}. Проверяется актуальное владение для завершения жизненного цикла.",
            analysisId,
            executionToken);

        return await RecoverAfterScheduleRetryFailureAsync(
            analysisId,
            executionToken,
            $"{errorMessage} [Планирование повтора отклонено]",
            guard,
            inspectDatabaseStateFirst: false);
    }

    private async Task<(bool OwnershipConfirmed, string? TerminalErrorMessage)> RecoverAfterScheduleRetryFailureAsync(
        int analysisId,
        Guid executionToken,
        string failMessage,
        IAnalysisLeaseGuard guard,
        bool inspectDatabaseStateFirst)
    {
        if (inspectDatabaseStateFirst)
        {
            Analysis? current;
            try
            {
                using var inspectTimeoutCts = new CancellationTokenSource(CriticalOperationTimeout);
                current = await analysisRepository.GetByIdAsync(analysisId, inspectTimeoutCts.Token);
            }
            catch (Exception inspectEx)
            {
                _logger?.LogError(
                    inspectEx,
                    "БД недоступна при проверке состояния анализа {AnalysisId} с токеном {ExecutionToken} после исключения ScheduleRetryAsync. Запись в БД запрещена (fail-closed); восстановление отложено до истечения аренды.",
                    analysisId,
                    executionToken);
                return (false, null);
            }

            if (current == null)
            {
                _logger?.LogWarning(
                    "Анализ {AnalysisId} не найден в БД после исключения ScheduleRetryAsync.",
                    analysisId);
                return (false, null);
            }

            if (current.Status == AnalysisStatus.Pending && current.ExecutionToken == null)
            {
                _logger?.LogWarning(
                    "Анализ {AnalysisId} уже переведён в статус Pending после исключения ScheduleRetryAsync (возможна потеря подтверждения фиксации транзакции); повторное изменение состояния не выполняется.",
                    analysisId);
                return (false, null);
            }

            if (current.Status != AnalysisStatus.Running || current.ExecutionToken != executionToken)
            {
                _logger?.LogWarning(
                    "Владение анализом {AnalysisId} с токеном {ExecutionToken} уже изменено (текущий статус: {Status}, токен: {CurrentToken}). Изменение состояния запрещено.",
                    analysisId,
                    executionToken,
                    current.Status,
                    current.ExecutionToken);
                return (false, null);
            }
        }

        bool isStillOwner;
        try
        {
            using var validateTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            isStillOwner = await guard.ValidateOwnershipAsync(
                [(int)AnalysisStatus.Running],
                validateTimeoutCts.Token);
        }
        catch (Exception validateEx)
        {
            _logger?.LogWarning(
                validateEx,
                "Не удалось подтвердить владение анализом {AnalysisId} с токеном {ExecutionToken} после неудачного ScheduleRetryAsync; безусловная запись в БД запрещена (fail-closed).",
                analysisId,
                executionToken);
            return (false, null);
        }

        if (!isStillOwner)
        {
            _logger?.LogWarning(
                "Владение анализом {AnalysisId} с токеном {ExecutionToken} потеряно после неудачного ScheduleRetryAsync; состояние в БД не изменяется.",
                analysisId,
                executionToken);
            return (false, null);
        }

        try
        {
            using var failTimeoutCts = new CancellationTokenSource(CriticalOperationTimeout);
            var failed = await analysisRepository.FailAsync(
                analysisId,
                executionToken,
                failMessage,
                guard,
                failTimeoutCts.Token);

            if (!failed)
            {
                _logger?.LogWarning(
                    "Fenced FailAsync вернул false после неудачного ScheduleRetryAsync для анализа {AnalysisId} с токеном {ExecutionToken}; дальнейшее восстановление полагается на истечение аренды.",
                    analysisId,
                    executionToken);
                return (false, null);
            }

            return (true, failMessage);
        }
        catch (Exception failEx)
        {
            _logger?.LogError(
                failEx,
                "Ошибка fenced FailAsync при восстановлении анализа {AnalysisId} с токеном {ExecutionToken} после неудачного ScheduleRetryAsync; дальнейшее восстановление полагается на истечение аренды.",
                analysisId,
                executionToken);
            return (false, null);
        }
    }

    private enum LeaseRenewalStopReason
    {
        None,
        NormalStop,
        OwnershipLost,
        RenewalException,
        OwnershipValidationException,
        ExternalCancellation
    }

    private sealed class LeaseRenewalState
    {
        private readonly object _syncRoot = new();
        private LeaseRenewalStopReason _stopReason = LeaseRenewalStopReason.None;
        private Exception? _exception;

        public LeaseRenewalStopReason StopReason
        {
            get
            {
                lock (_syncRoot)
                {
                    return _stopReason;
                }
            }
        }

        public Exception? Exception
        {
            get
            {
                lock (_syncRoot)
                {
                    return _exception;
                }
            }
        }

        public bool IsTerminal
        {
            get
            {
                lock (_syncRoot)
                {
                    return IsTerminalReason(_stopReason);
                }
            }
        }

        public static bool IsTerminalReason(LeaseRenewalStopReason reason) =>
            reason is LeaseRenewalStopReason.OwnershipLost
                   or LeaseRenewalStopReason.RenewalException
                   or LeaseRenewalStopReason.OwnershipValidationException
                   or LeaseRenewalStopReason.ExternalCancellation;

        public bool TrySetTerminalReason(LeaseRenewalStopReason reason, Exception? exception = null)
        {
            if (!IsTerminalReason(reason))
            {
                throw new ArgumentException($"Причина {reason} не является терминальной.", nameof(reason));
            }

            lock (_syncRoot)
            {
                if (IsTerminalReason(_stopReason))
                {
                    if (_stopReason == reason && _exception == null && exception != null)
                    {
                        _exception = exception;
                    }
                    return false;
                }

                _stopReason = reason;
                _exception = exception;
                return true;
            }
        }

        public bool RequestNormalStop()
        {
            lock (_syncRoot)
            {
                if (IsTerminalReason(_stopReason))
                {
                    return false;
                }

                _stopReason = LeaseRenewalStopReason.NormalStop;
                return true;
            }
        }
    }

    private async Task StopLeaseRenewalAsync(
        CancellationTokenSource linkedCts,
        Task leaseRenewalTask)
    {
        try
        {
            linkedCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Ошибка при отмене токена фоновой задачи продления аренды анализа.");
        }

        try
        {
            await leaseRenewalTask;
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Фоновая задача продления аренды анализа завершилась с исключением.");
        }
    }

    private async Task StartLeaseRenewalAsync(
        int analysisId,
        Guid executionToken,
        TimeSpan leaseDuration,
        IAnalysisLeaseGuard guard,
        CancellationTokenSource linkedCts,
        LeaseRenewalState renewalState)
    {
        var interval = leaseDuration.TotalSeconds >= 3
            ? TimeSpan.FromSeconds(Math.Clamp(leaseDuration.TotalSeconds / 3, 1, 5))
            : TimeSpan.FromMilliseconds(Math.Max(10, leaseDuration.TotalMilliseconds / 3));

        try
        {
            while (!linkedCts.Token.IsCancellationRequested)
            {
                await Task.Delay(interval, linkedCts.Token);

                bool isOwner;
                try
                {
                    using var validationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    isOwner = await guard.ValidateOwnershipAsync(
                        [(int)AnalysisStatus.Running],
                        validationTimeout.Token);
                }
                catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Исключение при проверке владения: состояние владения неизвестно
                    renewalState.TrySetTerminalReason(LeaseRenewalStopReason.OwnershipValidationException, ex);
                    linkedCts.Cancel();
                    return;
                }

                if (!isOwner)
                {
                    // Подтверждённая потеря владения
                    renewalState.TrySetTerminalReason(LeaseRenewalStopReason.OwnershipLost);
                    linkedCts.Cancel();
                    return;
                }

                bool renewed;
                try
                {
                    renewed = await analysisRepository.RenewLeaseAsync(
                        analysisId,
                        executionToken,
                        leaseDuration,
                        linkedCts.Token);
                }
                catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Исключение при вызове RenewLeaseAsync (например, таймаут БД)
                    renewalState.TrySetTerminalReason(LeaseRenewalStopReason.RenewalException, ex);
                    linkedCts.Cancel();
                    return;
                }

                if (!renewed)
                {
                    // Отказ продления аренды в БД (RenewLeaseAsync вернул false: задача в БД изменена или перехвачена)
                    renewalState.TrySetTerminalReason(LeaseRenewalStopReason.OwnershipLost);
                    linkedCts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            // Штатная остановка цикла.
        }
        catch (Exception ex)
        {
            // Непредвиденное исключение в цикле продления аренды
            renewalState.TrySetTerminalReason(LeaseRenewalStopReason.RenewalException, ex);
            linkedCts.Cancel();
        }
    }

    /// <summary>
    /// Синхронное выполнение анализа для сохранения обратной совместимости.
    /// </summary>
    public async Task<Analysis> AnalyzeAsync(int commitId, CancellationToken cancellationToken = default)
    {
        var commit = await commitRepository.GetByIdAsync(commitId, cancellationToken)
            ?? throw new InvalidOperationException("Коммит не найден.");

        if (commit.Project == null)
        {
            throw new InvalidOperationException($"Проект для коммита {commit.Id} не найден.");
        }

        var now = DateTime.UtcNow;
        var effectiveLeaseDuration = TimeSpan.FromMinutes(2);
        var token = Guid.NewGuid();
        var analysis = new Analysis
        {
            CommitId = commitId,
            Status = AnalysisStatus.Running,
            CreatedAt = now,
            StartedAt = now,
            LeaseExpiration = now.Add(effectiveLeaseDuration),
            ExecutionToken = token,
            AttemptCount = 1,
            MaxAttempts = 1,
            ErrorMessage = null
        };

        await analysisRepository.AddAsync(analysis, cancellationToken);
        analysis.Commit = commit;

        IAnalysisLeaseGuard? guard;
        try
        {
            guard = await _leaseGuardProvider.AcquireGuardAsync(analysis.Id, token, cancellationToken);
        }
        catch (Exception acquireEx)
        {
            await TryFailStartupWithoutGuardAsync(
                analysis,
                token,
                $"Не удалось получить охранную блокировку рабочего пространства анализа: {SanitizeErrorMessage(acquireEx)}");
            throw;
        }

        if (guard == null || !guard.IsAcquired)
        {
            if (guard != null)
            {
                await guard.DisposeAsync();
            }

            await TryFailStartupWithoutGuardAsync(
                analysis,
                token,
                "Не удалось получить охранную блокировку рабочего пространства анализа.");
            throw new InvalidOperationException("Failed to acquire workspace lease guard.");
        }

        await using (guard)
        {
            var outcome = await ExecuteGuardedCoreAsync(
                analysis,
                token,
                guard,
                effectiveLeaseDuration,
                allowRetry: false,
                rethrowFailures: true,
                cancellationToken);

            if (!outcome.Succeeded)
            {
                if (outcome.OwnershipConfirmed && !string.IsNullOrWhiteSpace(outcome.ErrorMessage))
                {
                    analysis.Status = AnalysisStatus.Failed;
                    analysis.CompletedAt = DateTime.UtcNow;
                    analysis.ErrorMessage = outcome.ErrorMessage;
                }

                throw new InvalidOperationException(
                    outcome.ErrorMessage ?? "Не удалось выполнить анализ с подтверждённым владением арендой.");
            }

            analysis.Status = AnalysisStatus.Completed;
            analysis.CompletedAt = DateTime.UtcNow;
            analysis.ErrorMessage = null;
            analysis.Results.Clear();
            foreach (var result in outcome.Results)
            {
                analysis.Results.Add(result);
            }

            return analysis;
        }
    }

    private async Task TryFailStartupWithoutGuardAsync(
        Analysis analysis,
        Guid executionToken,
        string errorMessage)
    {
        try
        {
            using var startupCleanupTimeoutCts = new CancellationTokenSource(CriticalOperationTimeout);
            var failed = await analysisRepository.TryFailStartupWithoutGuardAsync(
                analysis.Id,
                executionToken,
                errorMessage,
                startupCleanupTimeoutCts.Token);

            if (failed)
            {
                analysis.Status = AnalysisStatus.Failed;
                analysis.CompletedAt = DateTime.UtcNow;
                analysis.LeaseExpiration = null;
                analysis.ErrorMessage = errorMessage;
            }
            else
            {
                _logger?.LogWarning(
                    "Не удалось перевести незапущенный анализ {AnalysisId} с токеном {ExecutionToken} в статус Failed: владение уже потеряно или состояние изменено.",
                    analysis.Id,
                    executionToken);
            }
        }
        catch (Exception dbEx)
        {
            _logger?.LogError(
                dbEx,
                "Не удалось обновить статус незапущенного анализа {AnalysisId} с токеном {ExecutionToken} после сбоя получения lease guard; выполнена остановка fail-closed без безусловной записи в БД.",
                analysis.Id,
                executionToken);
        }
    }

    public async Task<Analysis?> GetByIdAsync(int analysisId, CancellationToken cancellationToken = default)
    {
        return await analysisRepository.GetByIdAsync(analysisId, cancellationToken);
    }

    public async Task<(IReadOnlyList<Analysis> Items, int TotalCount)> GetByCommitIdAsync(
        int commitId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        return await analysisRepository.GetByCommitIdAsync(commitId, page, pageSize, cancellationToken);
    }

    public static string SanitizeErrorMessage(Exception exception)
    {
        if (exception is null)
        {
            return "Во время выполнения анализа произошла неизвестная ошибка.";
        }

        // Если это системное исключение с техническими деталями среды исполнения,
        // сразу заменяем на безопасное общее сообщение.
        if (exception is NullReferenceException or IndexOutOfRangeException or ArgumentOutOfRangeException or AccessViolationException)
        {
            return "Во время выполнения анализа произошла непредвиденная ошибка.";
        }

        // Проверяем всю цепочку исключений на наличие конфиденциальных данных и технических параметров
        var currentEx = exception;
        while (currentEx != null)
        {
            var rawMsg = currentEx.Message;
            if (!string.IsNullOrEmpty(rawMsg))
            {
                var msg = StripStackTrace(rawMsg);
                if (ContainsSensitiveData(msg))
                {
                    return "Во время выполнения анализа произошла внутренняя ошибка.";
                }
            }

            if (currentEx is AggregateException agg && agg.InnerExceptions.Count > 0)
            {
                foreach (var inner in agg.InnerExceptions)
                {
                    var innerRaw = inner.Message;
                    if (!string.IsNullOrEmpty(innerRaw))
                    {
                        var innerMsg = StripStackTrace(innerRaw);
                        if (ContainsSensitiveData(innerMsg))
                        {
                            return "Во время выполнения анализа произошла внутренняя ошибка.";
                        }
                    }
                }
            }

            currentEx = currentEx.InnerException;
        }

        // Извлекаем наиболее информативное сообщение (раскрываем AggregateException)
        var targetException = exception;
        if (targetException is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
        {
            targetException = aggregate.Flatten().InnerExceptions[0];
        }

        var message = targetException.Message?.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Во время выполнения анализа произошла непредвиденная ошибка.";
        }

        // Если в сообщении присутствует стек вызовов, отсекаем его
        message = StripStackTrace(message);

        // Маскирование абсолютных путей файловой системы, чтобы не раскрывать структуру сервера и имена учетных записей
        // Windows пути с диском: C:\Users\user\...\file.ext -> file.ext или [путь]
        message = Regex.Replace(message, @"[A-Za-z]:\\[^:\r\n\t()]+?\\([a-zA-Z0-9_.-]+\.[a-zA-Z0-9]+)", "$1");
        message = Regex.Replace(message, @"[A-Za-z]:\\[^:\r\n\t()]+", "[путь]");

        // Unix пути: /home/user/.../file.ext -> file.ext или [путь]
        message = Regex.Replace(message, @"/(?:Users|home|var|tmp|etc|opt|usr|app|srv|root)/[^:\r\n\t()]+?/([a-zA-Z0-9_.-]+\.[a-zA-Z0-9]+)", "$1");
        message = Regex.Replace(message, @"/(?:Users|home|var|tmp|etc|opt|usr|app|srv|root)/[^:\r\n\t()]+", "[путь]");

        // UNC пути: \\server\share\... -> [путь]
        message = Regex.Replace(message, @"\\\\[^:\r\n\t()]+", "[путь]");

        // Ограничиваем длину сообщения для безопасности
        if (message.Length > 500)
        {
            message = message[..500];
        }

        return message;
    }

    private static string StripStackTrace(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        var stackTraceIndex = message.IndexOf("\n   at ", StringComparison.OrdinalIgnoreCase);
        if (stackTraceIndex < 0)
        {
            stackTraceIndex = message.IndexOf("\r\n   at ", StringComparison.OrdinalIgnoreCase);
        }
        if (stackTraceIndex < 0)
        {
            stackTraceIndex = message.IndexOf("\n   в ", StringComparison.OrdinalIgnoreCase);
        }
        if (stackTraceIndex < 0)
        {
            stackTraceIndex = message.IndexOf("\r\n   в ", StringComparison.OrdinalIgnoreCase);
        }

        if (stackTraceIndex >= 0)
        {
            return message[..stackTraceIndex].Trim();
        }

        return message;
    }
    private static bool ContainsSensitiveData(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg))
        {
            return false;
        }

        // 1. Официальные префиксы GitHub PAT (безусловное маскирование)
        var gitHubPatPrefixes = new[]
        {
            "ghp_", "github_pat_", "gho_", "ghu_", "ghs_", "ghr_"
        };

        foreach (var prefix in gitHubPatPrefixes)
        {
            if (msg.Contains(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 2. Параметры строк подключения к базам данных и серверные параметры
        var connectionStringPatterns = new[]
        {
            "connectionstring", "host=", "server=", "database=", "user id=",
            "username=", "port=", "ssl mode=", "localhost", "127.0.0.1"
        };

        foreach (var pattern in connectionStringPatterns)
        {
            if (msg.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 3. Трассировка стека и системные дампы
        var stackTracePatterns = new[]
        {
            "stack trace", "stacktrace", "at System.", "exception:"
        };

        foreach (var pattern in stackTracePatterns)
        {
            if (msg.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 4. Приватные IP-адреса локальных сетей (10.x.x.x, 192.168.x.x, 172.16-31.x.x)
        if (Regex.IsMatch(msg, @"\b(?:10\.\d{1,3}|192\.168\.\d{1,3}|172\.(?:1[6-9]|2\d|3[0-1]))\.\d{1,3}\b"))
        {
            return true;
        }

        // 5. Контекстное определение утечек авторизационных данных и токенов:
        // - Bearer <token>
        // - Authorization: <value>
        // - key/secret/password/token/credentials с присваиванием значения через = или :
        if (Regex.IsMatch(msg, @"\bbearer\s+\S+", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(msg, @"\bauthorization\s*:\s*\S+", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(msg, @"\b(?:password|pwd|secret|api[_-]?key|private[_-]?key|access[_-]?token|refresh[_-]?token|credentials|token)\s*[:=]\s*\S+", RegexOptions.IgnoreCase))
        {
            return true;
        }

        return false;
    }

    internal static bool AreResultsConsistent(ICollection<AnalysisResult>? actualResults, IReadOnlyList<AnalysisResult>? expectedResults)
    {
        if (actualResults == null || expectedResults == null)
        {
            return false;
        }

        if (actualResults.Count != expectedResults.Count)
        {
            return false;
        }

        var remainingActual = new List<AnalysisResult>(actualResults);

        foreach (var expected in expectedResults)
        {
            var matchIndex = remainingActual.FindIndex(a =>
                string.Equals(a.FilePath, expected.FilePath, StringComparison.Ordinal) &&
                a.LineNumber == expected.LineNumber &&
                string.Equals(a.Rule, expected.Rule, StringComparison.Ordinal) &&
                string.Equals(a.Message, expected.Message, StringComparison.Ordinal) &&
                a.Severity == expected.Severity &&
                string.Equals(a.Recommendation, expected.Recommendation, StringComparison.Ordinal) &&
                a.AnalyzerType == expected.AnalyzerType);

            if (matchIndex < 0)
            {
                return false;
            }

            remainingActual.RemoveAt(matchIndex);
        }

        return remainingActual.Count == 0;
    }
}
