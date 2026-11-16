using Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Api.BackgroundServices;

/// <summary>
/// Фоновый сервис периодической безопасной очистки устаревших рабочих каталогов (TD-003).
/// </summary>
public sealed class AnalysisCleanupBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<AnalysisCleanupBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Фоновый сервис очистки рабочих каталогов запущен.");

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var cleanupService = scope.ServiceProvider.GetRequiredService<IAnalysisCleanupService>();
                var deleted = await cleanupService.CleanupStaleDirectoriesAsync(stoppingToken);

                if (deleted > 0)
                {
                    logger.LogInformation("Очищено {Count} устаревших рабочих каталогов.", deleted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при выполнении фоновой очистки рабочих каталогов.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("Фоновый сервис очистки рабочих каталогов остановлен.");
    }
}
