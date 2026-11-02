using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using System.Text.Json;

namespace Api.Common;

/// <summary>
/// Предоставляет обработчики событий JWT-аутентификации со структурированным логированием.
/// </summary>
public sealed class ApiJwtEvents(ILogger<ApiJwtEvents> logger) : JwtBearerEvents
{
    private readonly ILogger<ApiJwtEvents> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Категория логгера для событий JWT-аутентификации.
    /// </summary>
    public const string LoggerCategory = "Api.Common.ApiJwtEvents";

    /// <summary>
    /// Обрабатывает событие сбоя аутентификации JWT.
    /// </summary>
    /// <param name="context">Контекст сбоя аутентификации.</param>
    /// <returns>Завершенная задача.</returns>
    public override Task AuthenticationFailed(AuthenticationFailedContext context)
    {
        _logger.LogInformation("Ошибка аутентификации при проверке JWT.");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Обрабатывает событие успешной валидации JWT-токена.
    /// </summary>
    /// <param name="context">Контекст успешной валидации токена.</param>
    /// <returns>Завершенная задача.</returns>
    public override Task TokenValidated(TokenValidatedContext context)
    {
        var userId = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.Principal?.FindFirst("sub")?.Value;

        if (!string.IsNullOrEmpty(userId))
        {
            _logger.LogInformation("Успешная проверка JWT для пользователя с идентификатором {UserId}.", userId);
        }
        else
        {
            _logger.LogInformation("Успешная проверка JWT.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Обрабатывает запрос на аутентификацию при ее отсутствии (401 Unauthorized).
    /// </summary>
    /// <param name="context">Контекст вызова challenge.</param>
    /// <returns>Завершенная задача.</returns>
    public override async Task Challenge(JwtBearerChallengeContext context)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.HandleResponse();
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/problem+json";

        var traceId = context.HttpContext.TraceIdentifier ?? Guid.NewGuid().ToString();
        var instance = context.Request.Path.Value ?? string.Empty;

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Требуется аутентификация",
            Detail = "Для доступа к ресурсу необходимо предоставить действительный токен авторизации.",
            Instance = instance,
            Type = "https://httpstatuses.com/401"
        };
        problemDetails.Extensions["code"] = ApiErrorCodes.Unauthorized;
        problemDetails.Extensions["traceId"] = traceId;

        await context.Response.WriteAsJsonAsync(
            problemDetails,
            options: (JsonSerializerOptions?)null,
            contentType: "application/problem+json",
            cancellationToken: context.HttpContext.RequestAborted);
    }

    /// <summary>
    /// Обрабатывает отказ в доступе при недостаточных правах (403 Forbidden).
    /// </summary>
    /// <param name="context">Контекст отказа в доступе.</param>
    /// <returns>Завершенная задача.</returns>
    public override async Task Forbidden(ForbiddenContext context)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/problem+json";

        var traceId = context.HttpContext.TraceIdentifier ?? Guid.NewGuid().ToString();
        var instance = context.Request.Path.Value ?? string.Empty;

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Доступ запрещен",
            Detail = "У вас недостаточно прав для выполнения данной операции.",
            Instance = instance,
            Type = "https://httpstatuses.com/403"
        };
        problemDetails.Extensions["code"] = ApiErrorCodes.Forbidden;
        problemDetails.Extensions["traceId"] = traceId;

        await context.Response.WriteAsJsonAsync(
            problemDetails,
            options: (JsonSerializerOptions?)null,
            contentType: "application/problem+json",
            cancellationToken: context.HttpContext.RequestAborted);
    }
}
