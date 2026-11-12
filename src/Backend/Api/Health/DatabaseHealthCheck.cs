using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Api.Health;

/// <summary>
/// Проверка готовности и доступности базы данных PostgreSQL для эндпоинта /health.
/// Выполняет легковесный ping подключения, не раскрывая конфиденциальных сведений и исключений.
/// </summary>
public sealed class DatabaseHealthCheck(
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseHealthCheck> logger) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);
            if (canConnect)
            {
                return HealthCheckResult.Healthy("База данных доступна.");
            }

            return HealthCheckResult.Unhealthy("База данных недоступна.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка проверки состояния подключения к базе данных.");
            return HealthCheckResult.Unhealthy("База данных недоступна.");
        }
    }
}
