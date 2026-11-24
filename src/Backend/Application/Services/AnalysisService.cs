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

        var guard = await _leaseGuardProvider.AcquireGuardAsync(analysisId, executionToken, cancellationToken);
        if (guard == null || !guard.IsAcquired)
        {
            return false;
        }

        try
        {
            var isInitialOwner = await guard.ValidateOwnershipAsync([(int)AnalysisStatus.Running], cancellationToken);
            if (!isInitialOwner)
            {
                return false;
            }

            var effectiveLeaseDuration = leaseDuration ?? TimeSpan.FromMinutes(2);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var renewalState = new LeaseRenewalState();
            var leaseRenewalTask = StartLeaseRenewalAsync(analysisId, executionToken, effectiveLeaseDuration, guard, linkedCts, renewalState);

            var ownershipConfirmed = true;
            bool isAmbiguousCommit = false;

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
                    ExecutionToken = analysis.ExecutionToken,
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
                        return false;
                    }

                    if (!isStillOwner)
                    {
                        ownershipConfirmed = false;
                        return false;
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
                    linkedCts.Cancel();
                    try { await leaseRenewalTask; } catch { }

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

                    if (isStillOwnerOnFail)
                    {
                        var failMessage = "Все анализаторы завершились ошибкой или не смогли предоставить результаты.";
                        var failed = await analysisRepository.FailAsync(analysis.Id, executionToken, failMessage, guard, CancellationToken.None);
                        if (!failed)
                        {
                            ownershipConfirmed = false;
                        }
                    }
                    else
                    {
                        ownershipConfirmed = false;
                    }

                    return false;
                }

                renewalState.RequestNormalStop();
                linkedCts.Cancel();
                try { await leaseRenewalTask; } catch { }

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
                    return false;
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

                        return false;
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
                        return false;
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
                        return false;
                    }
                    else
                    {
                        ownershipConfirmed = false;
                        isAmbiguousCommit = true;
                        _recoveryObserver?.OnRecoveryCompleted(analysis.Id, executionToken, false);
                        _logger?.LogError(
                            "Восстановление коммита анализа {AnalysisId} завершилось в непредвиденном состоянии (Статус: {Status}, Токен: {Token}). Коммит помечен как неоднозначный.",
                            analysis.Id, current?.Status, current?.ExecutionToken);
                        return false;
                    }
                }

                if (!completed)
                {
                    ownershipConfirmed = false;
                    return false;
                }

                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                renewalState.TrySetTerminalReason(LeaseRenewalStopReason.ExternalCancellation);
                linkedCts.Cancel();
                try { await leaseRenewalTask; } catch { }

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
                linkedCts.Cancel();
                try { await leaseRenewalTask; } catch { }

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
                    return false;
                }

                // 2. Исключение при проверке владения: состояние владения неизвестно
                if (renewalState.StopReason == LeaseRenewalStopReason.OwnershipValidationException)
                {
                    ownershipConfirmed = false;
                    return false;
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
                    return false;
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

                return false;
            }
            catch (Exception ex)
            {
                linkedCts.Cancel();
                try { await leaseRenewalTask; } catch { }

                if (isAmbiguousCommit)
                {
                    ownershipConfirmed = false;
                    _logger?.LogWarning(ex, "Анализ {AnalysisId} находится в состоянии неоднозначного коммита. Вызов FailAsync/ScheduleRetryAsync отменен.", analysis.Id);
                    return false;
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
                    return false;
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

                    if (_retryPolicy != null)
                    {
                        var decision = _retryPolicy.Evaluate(ex, analysis.AttemptCount, analysis.MaxAttempts, DateTime.UtcNow);

                        if (decision.ShouldRetry)
                        {
                            var nextAttemptAt = DateTime.UtcNow + (decision.Delay ?? TimeSpan.FromSeconds(10));
                            var retryMessage = $"{errorMessage} [Повтор запланирован на {nextAttemptAt:u} UTC: {decision.Reason}]";
                            var scheduled = await analysisRepository.ScheduleRetryAsync(
                                analysis.Id,
                                executionToken,
                                nextAttemptAt,
                                retryMessage,
                                guard,
                                CancellationToken.None);

                            if (!scheduled)
                            {
                                ownershipConfirmed = false;
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
                        }
                    }
                    else
                    {
                        var failed = await analysisRepository.FailAsync(analysis.Id, executionToken, errorMessage, guard, CancellationToken.None);
                        if (!failed)
                        {
                            ownershipConfirmed = false;
                        }
                    }
                }
                else
                {
                    ownershipConfirmed = false;
                }

                return false;
            }
            finally
            {
                // Очищаем временные файлы только если владение подтверждено
                // (при потере владения или при невозможности подтвердить владение очистка запрещена)
                if (ownershipConfirmed)
                {
                    try
                    {
                        await repositorySourceService.CleanupAsync(analysis.Id, executionToken, guard, CancellationToken.None);
                    }
                    catch
                    {
                    }
                }
            }
        }
        finally
        {
            await guard.DisposeAsync();
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

        var token = Guid.NewGuid();
        var analysis = new Analysis
        {
            CommitId = commitId,
            Status = AnalysisStatus.Running,
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            ExecutionToken = token,
            ErrorMessage = null
        };

        await analysisRepository.AddAsync(analysis, cancellationToken);

        await using var guard = await _leaseGuardProvider.AcquireGuardAsync(analysis.Id, token, cancellationToken);
        if (guard == null || !guard.IsAcquired)
        {
            throw new InvalidOperationException("Failed to acquire workspace lease guard.");
        }

        try
        {
            var personalAccessToken = await _gitHubCredentialService.GetUserTokenAsync(commit.Project.OwnerId, cancellationToken);

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
                token,
                personalAccessToken,
                guard,
                cancellationToken);

            var context = new AnalyzerContext
            {
                RepositoryPath = repositoryPath,
                CommitHash = commit.CommitHash,
                AnalysisId = analysis.Id,
                ExecutionToken = token,
                LeaseGuard = guard
            };

            foreach (var analyzer in analyzers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var findings = await analyzer.AnalyzeAsync(context, cancellationToken);

                foreach (var finding in findings)
                {
                    analysis.Results.Add(new AnalysisResult
                    {
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

            analysis.Status = AnalysisStatus.Completed;
            analysis.CompletedAt = DateTime.UtcNow;

            await analysisRepository.UpdateAsync(analysis, cancellationToken);

            return analysis;
        }
        catch (Exception ex)
        {
            analysis.Status = AnalysisStatus.Failed;
            analysis.CompletedAt = DateTime.UtcNow;
            analysis.ErrorMessage = SanitizeErrorMessage(ex);

            await analysisRepository.UpdateAsync(analysis, CancellationToken.None);

            throw;
        }
        finally
        {
            try
            {
                await repositorySourceService.CleanupAsync(analysis.Id, token, guard, CancellationToken.None);
            }
            catch
            {
                // Ошибки очистки не должны маскировать ошибку анализа
            }
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
