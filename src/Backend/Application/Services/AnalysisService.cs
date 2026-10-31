using Application.Analyzers;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Domain.Entities;
using Domain.Enums;
using System.Text.RegularExpressions;

namespace Application.Services;

public class AnalysisService(
    IAnalysisRepository analysisRepository,
    ICommitRepository commitRepository,
    IRepositorySourceService repositorySourceService,
    IEnumerable<IAnalyzer> analyzers)
{
    /// <summary>
    /// Создаёт задачу анализа коммита со статусом Pending и сохраняет её в очередь задач.
    /// Не выполняет запуск анализаторов и не скачивает исходный код.
    /// </summary>
    public async Task<Analysis> EnqueueAsync(int commitId, CancellationToken cancellationToken = default)
    {
        var commit = await commitRepository.GetByIdAsync(commitId, cancellationToken)
            ?? throw new InvalidOperationException("Коммит не найден.");

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
        return analysis;
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

        var effectiveLeaseDuration = leaseDuration ?? TimeSpan.FromMinutes(2);
        using var leaseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseRenewalTask = StartLeaseRenewalAsync(analysisId, executionToken, effectiveLeaseDuration, leaseCts);

        var ownershipLost = false;

        try
        {
            var commit = (analysis.Commit?.Project != null ? analysis.Commit : null)
                ?? await commitRepository.GetByIdAsync(analysis.CommitId, leaseCts.Token)
                ?? throw new InvalidOperationException($"Коммит {analysis.CommitId} не найден.");

            if (commit.Project == null)
            {
                throw new InvalidOperationException($"Проект для коммита {commit.Id} не найден.");
            }

            var repositoryPath = await repositorySourceService.DownloadAndExtractAsync(
                commit.Project.RepoUrl,
                commit.CommitHash,
                analysis.Id,
                leaseCts.Token);

            var context = new AnalyzerContext
            {
                RepositoryPath = repositoryPath,
                CommitHash = commit.CommitHash
            };

            var results = new List<AnalysisResult>();

            foreach (var analyzer in analyzers)
            {
                leaseCts.Token.ThrowIfCancellationRequested();

                var findings = await analyzer.AnalyzeAsync(context, leaseCts.Token);

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

            leaseCts.Cancel();
            try { await leaseRenewalTask; } catch { }

            var completed = await analysisRepository.CompleteAsync(analysis.Id, executionToken, results, CancellationToken.None);
            if (!completed)
            {
                ownershipLost = true;
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            leaseCts.Cancel();
            try { await leaseRenewalTask; } catch { }

            // При штатной остановке приложения НЕ помечаем задачу как Failed,
            // чтобы после истечения аренды она могла быть повторно захвачена.
            throw;
        }
        catch (OperationCanceledException) when (leaseCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            ownershipLost = true;
            leaseCts.Cancel();
            try { await leaseRenewalTask; } catch { }

            // Потеря или истечение аренды: не перезаписываем чужую задачу
            return false;
        }
        catch (Exception ex)
        {
            leaseCts.Cancel();
            try { await leaseRenewalTask; } catch { }

            // Ошибка подготовки коммита/проекта, скачивания или анализа:
            // помечаем задачу как неудачную с защитой по ExecutionToken.
            // Если владение уже утрачено, FailAsync вернёт false.
            var errorMessage = SanitizeErrorMessage(ex);
            var failed = await analysisRepository.FailAsync(analysis.Id, executionToken, errorMessage, CancellationToken.None);
            if (!failed)
            {
                ownershipLost = true;
            }

            return false;
        }
        finally
        {
            // Очищаем временные файлы только если обработчик не потерял владение задачей
            // (чтобы не удалить файлы другого воркера, перехватившего задачу)
            if (!ownershipLost)
            {
                try
                {
                    await repositorySourceService.CleanupAsync(analysis.Id, CancellationToken.None);
                }
                catch
                {
                    // Исключения из CleanupAsync не должны маскировать ошибки анализа
                    // или приводить к смене статуса задачи
                }
            }
        }
    }

    private async Task StartLeaseRenewalAsync(
        int analysisId,
        Guid executionToken,
        TimeSpan leaseDuration,
        CancellationTokenSource linkedCts)
    {
        var interval = leaseDuration.TotalSeconds >= 15
            ? TimeSpan.FromSeconds(leaseDuration.TotalSeconds / 3)
            : TimeSpan.FromMilliseconds(Math.Max(10, leaseDuration.TotalMilliseconds / 3));

        try
        {
            while (!linkedCts.Token.IsCancellationRequested)
            {
                await Task.Delay(interval, linkedCts.Token);

                var renewed = await analysisRepository.RenewLeaseAsync(
                    analysisId,
                    executionToken,
                    leaseDuration,
                    CancellationToken.None);

                if (!renewed)
                {
                    linkedCts.Cancel();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ожидаемое завершение цикла продления аренды
        }
    }

    /// <summary>
    /// Синхронное выполнение анализа для сохранения обратной совместимости.
    /// </summary>
    public async Task<Analysis> AnalyzeAsync(int commitId, CancellationToken cancellationToken = default)
    {
        var commit = await commitRepository.GetByIdAsync(commitId, cancellationToken)
            ?? throw new InvalidOperationException("Коммит не найден.");

        var analysis = new Analysis
        {
            CommitId = commitId,
            Status = AnalysisStatus.Running,
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            ErrorMessage = null
        };

        await analysisRepository.AddAsync(analysis, cancellationToken);

        try
        {
            var repositoryPath = await repositorySourceService.DownloadAndExtractAsync(
                commit.Project.RepoUrl,
                commit.CommitHash,
                analysis.Id,
                cancellationToken);

            var context = new AnalyzerContext
            {
                RepositoryPath = repositoryPath,
                CommitHash = commit.CommitHash
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
                await repositorySourceService.CleanupAsync(analysis.Id, CancellationToken.None);
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

    public async Task<IReadOnlyList<Analysis>> GetByCommitIdAsync(
        int commitId,
        CancellationToken cancellationToken = default)
    {
        return await analysisRepository.GetByCommitIdAsync(commitId, cancellationToken);
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
        var sensitivePatterns = new[]
        {
            "password", "pwd", "secret", "token", "bearer", "authorization",
            "connectionstring", "host=", "server=", "database=", "user id=",
            "username=", "port=", "ssl mode=", "stack trace", "at System.",
            "exception:", "stacktrace", "credentials", "apikey", "api_key",
            "private_key", "localhost", "127.0.0.1"
        };

        var currentEx = exception;
        while (currentEx != null)
        {
            var rawMsg = currentEx.Message;
            if (!string.IsNullOrEmpty(rawMsg))
            {
                var msg = StripStackTrace(rawMsg);
                foreach (var pattern in sensitivePatterns)
                {
                    if (msg.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        return "Во время выполнения анализа произошла внутренняя ошибка.";
                    }
                }

                // Проверка на внутренние приватные IP-адреса (10.x.x.x, 192.168.x.x, 172.16-31.x.x)
                if (Regex.IsMatch(msg, @"\b(?:10\.\d{1,3}|192\.168\.\d{1,3}|172\.(?:1[6-9]|2\d|3[0-1]))\.\d{1,3}\b"))
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
                        foreach (var pattern in sensitivePatterns)
                        {
                            if (innerMsg.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                            {
                                return "Во время выполнения анализа произошла внутренняя ошибка.";
                            }
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
}
