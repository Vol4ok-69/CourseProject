using Application.Interfaces.Repositories;
using Application.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Api.BackgroundServices;

/// <summary>
/// Фоновый сервис долговечной очереди анализа.
/// Периодически извлекает задачи из PostgreSQL со статусом Pending или Running с истёкшей арендой,
/// передаёт их сервису AnalysisService и фиксирует результаты.
/// </summary>
public sealed class AnalysisBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<AnalysisBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollingDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Фоновый сервис обработки очереди анализов запущен.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var processedAnyTask = false;

            try
            {
                using (var scope = scopeFactory.CreateScope())
                {
                    var repository = scope.ServiceProvider.GetRequiredService<IAnalysisRepository>();
                    var analysisService = scope.ServiceProvider.GetRequiredService<AnalysisService>();

                    var acquired = await repository.TryAcquireNextPendingOrStaleAsync(
                        DefaultLeaseDuration,
                        stoppingToken);

                    if (acquired != null)
                    {
                        processedAnyTask = true;

                        logger.LogInformation(
                            "Захвачена задача анализа {AnalysisId} для коммита {CommitId}. ExecutionToken: {ExecutionToken}",
                            acquired.Id,
                            acquired.CommitId,
                            acquired.ExecutionToken);

                        try
                        {
                            var success = await analysisService.ExecuteAnalysisAsync(
                                acquired.Id,
                                acquired.ExecutionToken!.Value,
                                stoppingToken);

                            if (success)
                            {
                                logger.LogInformation("Задача анализа {AnalysisId} успешно выполнена.", acquired.Id);
                            }
                            else
                            {
                                logger.LogWarning("Задача анализа {AnalysisId} завершилась неудачей или аренда была утрачена.", acquired.Id);
                            }
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            logger.LogInformation("Обработка задачи анализа {AnalysisId} прервана из-за остановки сервиса.", acquired.Id);
                            break;
                        }
                        catch (Exception taskEx)
                        {
                            logger.LogError(taskEx, "Непредвиденная ошибка при обработке задачи анализа {AnalysisId}.", acquired.Id);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка в основном цикле фонового обработчика очереди. Ожидание {Delay} перед повтором...", ErrorRetryDelay);
                try
                {
                    await Task.Delay(ErrorRetryDelay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }

            if (!processedAnyTask && !stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(PollingDelay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        logger.LogInformation("Фоновый сервис обработки очереди анализов остановлен.");
    }
}
