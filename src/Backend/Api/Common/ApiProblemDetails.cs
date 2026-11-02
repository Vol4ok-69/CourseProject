using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace Api.Common;

/// <summary>
/// Фабрика для создания ответов с ошибками в формате Problem Details (RFC 9457).
/// </summary>
public static class ApiProblemDetails
{
    /// <summary>
    /// Создает результат ObjectResult, содержащий объект ProblemDetails со всеми обязательными полями RFC 9457.
    /// </summary>
    /// <param name="statusCode">HTTP-статус ошибки.</param>
    /// <param name="title">Краткое русскоязычное наименование категории проблемы.</param>
    /// <param name="detail">Понятное пользователю русскоязычное описание ошибки.</param>
    /// <param name="code">Машиночитаемый код ошибки в SCREAMING_SNAKE_CASE.</param>
    /// <param name="instance">Относительный URI запроса.</param>
    /// <param name="traceId">Сквозной идентификатор запроса для связи с серверными логами.</param>
    /// <returns>Экземпляр ObjectResult с объектом ProblemDetails.</returns>
    public static ObjectResult Create(
        int statusCode,
        string title,
        string detail,
        string code,
        string? instance = null,
        string? traceId = null)
    {
        var resolvedTraceId = !string.IsNullOrEmpty(traceId)
            ? traceId
            : (Activity.Current?.Id ?? Guid.NewGuid().ToString());

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = instance ?? string.Empty,
            Type = $"https://httpstatuses.com/{statusCode}"
        };

        problemDetails.Extensions["code"] = code;
        problemDetails.Extensions["traceId"] = resolvedTraceId;

        return new ObjectResult(problemDetails)
        {
            StatusCode = statusCode,
            ContentTypes = { "application/problem+json" }
        };
    }

    /// <summary>
    /// Создает результат ObjectResult, извлекая instance и traceId из HttpContext контроллера.
    /// </summary>
    /// <param name="controller">Контроллер, формирующий ответ.</param>
    /// <param name="statusCode">HTTP-статус ошибки.</param>
    /// <param name="title">Краткое русскоязычное наименование категории проблемы.</param>
    /// <param name="detail">Понятное пользователю русскоязычное описание ошибки.</param>
    /// <param name="code">Машиночитаемый код ошибки в SCREAMING_SNAKE_CASE.</param>
    /// <returns>Экземпляр ObjectResult с объектом ProblemDetails.</returns>
    public static ObjectResult Create(
        ControllerBase controller,
        int statusCode,
        string title,
        string detail,
        string code)
    {
        var traceId = controller.HttpContext?.TraceIdentifier;
        var instance = controller.HttpContext?.Request?.Path.Value;

        return Create(statusCode, title, detail, code, instance, traceId);
    }

    /// <summary>
    /// Настраивает обработку невалидного состояния модели контроллеров в формате RFC 9457 ValidationProblemDetails.
    /// </summary>
    /// <param name="options">Параметры ApiBehaviorOptions.</param>
    public static void ConfigureInvalidModelStateResponse(ApiBehaviorOptions options)
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var traceId = context.HttpContext.TraceIdentifier ?? Guid.NewGuid().ToString();
            var instance = context.HttpContext.Request.Path.Value ?? string.Empty;

            var validationProblemDetails = new ValidationProblemDetails(context.ModelState)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Ошибка валидации входных данных",
                Detail = "Одно или несколько полей содержат некорректные данные.",
                Instance = instance,
                Type = "https://httpstatuses.com/400"
            };

            validationProblemDetails.Extensions["code"] = ApiErrorCodes.ValidationFailed;
            validationProblemDetails.Extensions["traceId"] = traceId;

            return new BadRequestObjectResult(validationProblemDetails)
            {
                ContentTypes = { "application/problem+json" }
            };
        };
    }

    /// <summary>
    /// Настраивает параметры ProblemDetailsOptions в соответствии с контрактом RFC 9457 для проекта.
    /// </summary>
    /// <param name="options">Параметры ProblemDetailsOptions.</param>
    public static void ConfigureProblemDetails(ProblemDetailsOptions options)
    {
        options.CustomizeProblemDetails = context =>
        {
            var httpContext = context.HttpContext;
            var problemDetails = context.ProblemDetails;

            var statusCode = problemDetails.Status ?? httpContext.Response.StatusCode;
            if (statusCode == 0)
            {
                statusCode = StatusCodes.Status500InternalServerError;
            }

            problemDetails.Status = statusCode;
            problemDetails.Type = $"https://httpstatuses.com/{statusCode}";
            problemDetails.Instance ??= httpContext.Request.Path.Value ?? string.Empty;

            if (!problemDetails.Extensions.ContainsKey("traceId"))
            {
                problemDetails.Extensions["traceId"] = httpContext.TraceIdentifier ?? Guid.NewGuid().ToString();
            }

            switch (statusCode)
            {
                case StatusCodes.Status400BadRequest:
                    if (string.IsNullOrEmpty(problemDetails.Title) || problemDetails.Title == "Bad Request" || problemDetails.Title == "One or more validation errors occurred.")
                    {
                        problemDetails.Title = problemDetails is HttpValidationProblemDetails or ValidationProblemDetails
                            ? "Ошибка валидации входных данных"
                            : "Некорректный запрос";
                    }
                    if (problemDetails.Detail == null || problemDetails.Detail == "Запрос содержит некорректные данные." || problemDetails.Detail == "One or more validation errors occurred.")
                    {
                        problemDetails.Detail = problemDetails is HttpValidationProblemDetails or ValidationProblemDetails
                            ? "Одно или несколько полей содержат некорректные данные."
                            : "Запрос содержит некорректные данные.";
                    }
                    if (!problemDetails.Extensions.ContainsKey("code"))
                    {
                        problemDetails.Extensions["code"] = ApiErrorCodes.ValidationFailed;
                    }
                    break;

                case StatusCodes.Status401Unauthorized:
                    problemDetails.Title = "Требуется аутентификация";
                    problemDetails.Detail ??= "Для доступа к ресурсу необходимо предоставить действительный токен авторизации.";
                    if (!problemDetails.Extensions.ContainsKey("code"))
                    {
                        problemDetails.Extensions["code"] = ApiErrorCodes.Unauthorized;
                    }
                    break;

                case StatusCodes.Status403Forbidden:
                    problemDetails.Title = "Доступ запрещен";
                    problemDetails.Detail ??= "У вас недостаточно прав для выполнения данной операции.";
                    if (!problemDetails.Extensions.ContainsKey("code"))
                    {
                        problemDetails.Extensions["code"] = ApiErrorCodes.Forbidden;
                    }
                    break;

                case StatusCodes.Status404NotFound:
                    problemDetails.Title = "Ресурс не найден";
                    problemDetails.Detail ??= "Запрашиваемый ресурс не найден.";
                    if (!problemDetails.Extensions.ContainsKey("code"))
                    {
                        var path = httpContext.Request.Path.Value ?? string.Empty;
                        if (path.StartsWith("/api/admin/users", StringComparison.OrdinalIgnoreCase) ||
                            path.Equals("/api/auth/me", StringComparison.OrdinalIgnoreCase))
                        {
                            problemDetails.Extensions["code"] = ApiErrorCodes.UserNotFound;
                        }
                        else if (path.Contains("/analyses", StringComparison.OrdinalIgnoreCase))
                        {
                            problemDetails.Extensions["code"] = ApiErrorCodes.AnalysisNotFound;
                        }
                        else if (path.Contains("/commits", StringComparison.OrdinalIgnoreCase))
                        {
                            problemDetails.Extensions["code"] = ApiErrorCodes.CommitNotFound;
                        }
                        else if (path.StartsWith("/api/projects", StringComparison.OrdinalIgnoreCase))
                        {
                            problemDetails.Extensions["code"] = ApiErrorCodes.ProjectNotFound;
                        }
                        else
                        {
                            problemDetails.Extensions["code"] = "NOT_FOUND";
                        }
                    }
                    break;

                case StatusCodes.Status429TooManyRequests:
                    problemDetails.Title = "Слишком много запросов";
                    problemDetails.Detail ??= "Превышен лимит частоты запросов. Пожалуйста, повторите попытку позже.";
                    if (!problemDetails.Extensions.ContainsKey("code"))
                    {
                        problemDetails.Extensions["code"] = ApiErrorCodes.RateLimitExceeded;
                    }
                    break;

                case StatusCodes.Status409Conflict:
                    problemDetails.Title = "Конфликт данных";
                    problemDetails.Detail ??= "Запрос конфликтует с текущим состоянием ресурса.";
                    if (!problemDetails.Extensions.ContainsKey("code"))
                    {
                        problemDetails.Extensions["code"] = "CONFLICT";
                    }
                    break;

                default:
                    if (statusCode >= 500)
                    {
                        problemDetails.Title = "Внутренняя ошибка сервера";
                        problemDetails.Detail = "Произошла непредвиденная ошибка на стороне сервера.";
                        problemDetails.Type = "https://httpstatuses.com/500";
                        problemDetails.Extensions["code"] = ApiErrorCodes.InternalServerError;
                    }
                    else if (!problemDetails.Extensions.ContainsKey("code"))
                    {
                        problemDetails.Extensions["code"] = $"HTTP_{statusCode}";
                    }
                    break;
            }
        };
    }
}
