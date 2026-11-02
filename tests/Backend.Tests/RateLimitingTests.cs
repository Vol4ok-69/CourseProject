using Api.Common;
using Api.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading.RateLimiting;
using Xunit;

namespace Backend.Tests;

public sealed class RateLimitingTests
{
    private static RequestDelegate CreateRateLimitingPipeline()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.ContentType = "application/problem+json";

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfterSpan))
                {
                    var seconds = Math.Max(1, (int)Math.Ceiling(retryAfterSpan.TotalSeconds));
                    context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                }

                var traceId = context.HttpContext.TraceIdentifier ?? Guid.NewGuid().ToString();
                var instance = context.HttpContext.Request.Path.Value ?? string.Empty;

                var problemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Слишком много запросов",
                    Detail = "Превышен лимит частоты запросов. Пожалуйста, повторите попытку позже.",
                    Instance = instance,
                    Type = "https://httpstatuses.com/429"
                };
                problemDetails.Extensions["code"] = ApiErrorCodes.RateLimitExceeded;
                problemDetails.Extensions["traceId"] = traceId;

                await context.HttpContext.Response.WriteAsJsonAsync(
                    problemDetails,
                    options: (JsonSerializerOptions?)null,
                    contentType: "application/problem+json",
                    cancellationToken: cancellationToken);
            };

            options.AddPolicy(RateLimitingPolicies.AuthPolicy, httpContext =>
            {
                var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetTokenBucketLimiter(
                    remoteIp,
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 10,
                        TokensPerPeriod = 1,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                        AutoReplenishment = true,
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    });
            });
        });

        var serviceProvider = services.BuildServiceProvider();
        var appBuilder = new ApplicationBuilder(serviceProvider);
        appBuilder.UseRateLimiter();
        appBuilder.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        return appBuilder.Build();
    }

    private static DefaultHttpContext CreateHttpContext(
        string? ipAddress,
        string path = "/api/auth/login",
        bool enableRateLimiting = true,
        string? xForwardedFor = null)
    {
        var context = new DefaultHttpContext();

        if (ipAddress is not null)
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(ipAddress);
        }

        context.Request.Path = path;
        context.Request.Method = "POST";
        context.Response.Body = new MemoryStream();

        if (!string.IsNullOrEmpty(xForwardedFor))
        {
            context.Request.Headers["X-Forwarded-For"] = xForwardedFor;
        }

        var metadata = enableRateLimiting
            ? new EndpointMetadataCollection(new EnableRateLimitingAttribute(RateLimitingPolicies.AuthPolicy))
            : new EndpointMetadataCollection();

        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, path));
        return context;
    }

    private static async Task<string> ReadResponseBodyAsync(DefaultHttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task InitialBucket_Allows10Requests_WithinLimit()
    {
        var pipeline = CreateRateLimitingPipeline();
        var ip = "192.168.10.1";

        for (var i = 1; i <= 10; i++)
        {
            var context = CreateHttpContext(ip);
            await pipeline(context);

            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        }
    }

    [Fact]
    public async Task NextRequest_AfterExhaustion_Returns429TooManyRequests()
    {
        var pipeline = CreateRateLimitingPipeline();
        var ip = "192.168.10.2";

        for (var i = 1; i <= 10; i++)
        {
            var context = CreateHttpContext(ip);
            await pipeline(context);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        }

        var rejectedContext = CreateHttpContext(ip);
        await pipeline(rejectedContext);

        Assert.Equal(StatusCodes.Status429TooManyRequests, rejectedContext.Response.StatusCode);
    }

    [Fact]
    public async Task AfterTokenReplenishment_AdditionalRequestAllowed()
    {
        var pipeline = CreateRateLimitingPipeline();
        var ip = "192.168.10.3";

        for (var i = 1; i <= 10; i++)
        {
            var context = CreateHttpContext(ip);
            await pipeline(context);
        }

        var rejectedContext = CreateHttpContext(ip);
        await pipeline(rejectedContext);
        Assert.Equal(StatusCodes.Status429TooManyRequests, rejectedContext.Response.StatusCode);

        // Ждем 1.2 секунды для восстановления 1 токена (скорость 1 токен / 1 сек).
        await Task.Delay(1200);

        var allowedContext = CreateHttpContext(ip);
        await pipeline(allowedContext);
        Assert.Equal(StatusCodes.Status200OK, allowedContext.Response.StatusCode);

        // Следующий запрос без задержки снова должен быть отклонен.
        var nextRejectedContext = CreateHttpContext(ip);
        await pipeline(nextRejectedContext);
        Assert.Equal(StatusCodes.Status429TooManyRequests, nextRejectedContext.Response.StatusCode);
    }

    [Fact]
    public async Task Bucket_DoesNotExceedMaxCapacityOf10()
    {
        var pipeline = CreateRateLimitingPipeline();
        var ip = "192.168.10.4";

        // Выполняем первичный запрос для создания корзины лимитером (остается 9 токенов).
        var initialContext = CreateHttpContext(ip);
        await pipeline(initialContext);
        Assert.Equal(StatusCodes.Status200OK, initialContext.Response.StatusCode);

        // Ожидаем 2.2 секунды (проходит 2 периода пополнения по 1 с).
        // Если бы емкость корзины не ограничивалась 10 токенами, восстановилось бы 9 + 2 = 11 токенов.
        await Task.Delay(2200);

        // Корзина ограничена емкостью 10 токенов, поэтому разрешено ровно 10 запросов подряд.
        for (var i = 1; i <= 10; i++)
        {
            var context = CreateHttpContext(ip);
            await pipeline(context);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        }

        // 11-й запрос без ожидания пополнения должен быть отклонен со статусом 429.
        var eleventh = CreateHttpContext(ip);
        await pipeline(eleventh);
        Assert.Equal(StatusCodes.Status429TooManyRequests, eleventh.Response.StatusCode);
    }

    [Fact]
    public async Task DifferentIpAddresses_HaveIndependentBuckets()
    {
        var pipeline = CreateRateLimitingPipeline();
        var ip1 = "192.168.10.5";
        var ip2 = "192.168.10.6";

        // Исчерпываем корзину первого IP
        for (var i = 1; i <= 10; i++)
        {
            var context = CreateHttpContext(ip1);
            await pipeline(context);
        }

        var ip1Rejected = CreateHttpContext(ip1);
        await pipeline(ip1Rejected);
        Assert.Equal(StatusCodes.Status429TooManyRequests, ip1Rejected.Response.StatusCode);

        // Второй IP имеет свежую независимую корзину
        var ip2Allowed = CreateHttpContext(ip2);
        await pipeline(ip2Allowed);
        Assert.Equal(StatusCodes.Status200OK, ip2Allowed.Response.StatusCode);
    }

    [Fact]
    public async Task SameIp_ConsumesSameBucket()
    {
        var pipeline = CreateRateLimitingPipeline();
        var ip = "192.168.10.7";

        for (var i = 1; i <= 5; i++)
        {
            var context = CreateHttpContext(ip);
            await pipeline(context);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        }

        for (var i = 6; i <= 10; i++)
        {
            var context = CreateHttpContext(ip);
            await pipeline(context);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        }

        var eleventh = CreateHttpContext(ip);
        await pipeline(eleventh);
        Assert.Equal(StatusCodes.Status429TooManyRequests, eleventh.Response.StatusCode);
    }

    [Fact]
    public async Task XForwardedFor_Spoofing_DoesNotBypassRateLimit()
    {
        var pipeline = CreateRateLimitingPipeline();
        var realIp = "192.168.10.8";

        // Посылаем 10 запросов с разными фейковыми заголовками X-Forwarded-For
        for (var i = 1; i <= 10; i++)
        {
            var context = CreateHttpContext(realIp, xForwardedFor: $"10.99.99.{i}");
            await pipeline(context);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        }

        // 11-й запрос с очередным новым X-Forwarded-For должен быть отклонен, так как ключ - RemoteIpAddress
        var eleventh = CreateHttpContext(realIp, xForwardedFor: "10.99.99.99");
        await pipeline(eleventh);

        Assert.Equal(StatusCodes.Status429TooManyRequests, eleventh.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/register")]
    [InlineData("/api/auth/me/password")]
    public async Task Policy_AppliesToTargetAuthRoutes(string route)
    {
        var pipeline = CreateRateLimitingPipeline();
        var ip = $"192.168.20.{route.GetHashCode() & 0xFF}";

        for (var i = 1; i <= 10; i++)
        {
            var context = CreateHttpContext(ip, path: route, enableRateLimiting: true);
            await pipeline(context);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        }

        var rejected = CreateHttpContext(ip, path: route, enableRateLimiting: true);
        await pipeline(rejected);
        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.Response.StatusCode);
    }

    [Fact]
    public async Task Endpoints_NotInPolicy_AreNotBlocked()
    {
        var pipeline = CreateRateLimitingPipeline();
        var ip = "192.168.10.9";

        // Для эндпоинта без политики (например, GET /api/projects или GET /api/auth/me)
        for (var i = 1; i <= 25; i++)
        {
            var context = CreateHttpContext(ip, path: "/api/projects", enableRateLimiting: false);
            await pipeline(context);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        }
    }

    [Fact]
    public async Task RejectedResponse_HasCorrectProblemDetailsFormat()
    {
        var pipeline = CreateRateLimitingPipeline();
        var ip = "192.168.10.10";

        for (var i = 1; i <= 10; i++)
        {
            var c = CreateHttpContext(ip, path: "/api/auth/login");
            await pipeline(c);
        }

        var rejectedContext = CreateHttpContext(ip, path: "/api/auth/login");
        await pipeline(rejectedContext);

        Assert.Equal(StatusCodes.Status429TooManyRequests, rejectedContext.Response.StatusCode);
        Assert.Equal("application/problem+json", rejectedContext.Response.ContentType);

        var body = await ReadResponseBodyAsync(rejectedContext);
        using var jsonDoc = JsonDocument.Parse(body);
        var root = jsonDoc.RootElement;

        Assert.Equal(429, root.GetProperty("status").GetInt32());
        Assert.Equal("Слишком много запросов", root.GetProperty("title").GetString());
        Assert.Equal("Превышен лимит частоты запросов. Пожалуйста, повторите попытку позже.", root.GetProperty("detail").GetString());
        Assert.Equal("/api/auth/login", root.GetProperty("instance").GetString());
        Assert.Equal("https://httpstatuses.com/429", root.GetProperty("type").GetString());
        Assert.Equal(ApiErrorCodes.RateLimitExceeded, root.GetProperty("code").GetString());
        Assert.True(root.TryGetProperty("traceId", out var traceIdProp) && !string.IsNullOrEmpty(traceIdProp.GetString()));

        // Поле errors должно отсутствовать (согласно RFC 9457 для не-400 ошибок)
        Assert.False(root.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task RetryAfter_Header_IsPresentAndValid()
    {
        var pipeline = CreateRateLimitingPipeline();
        var ip = "192.168.10.11";

        for (var i = 1; i <= 10; i++)
        {
            var c = CreateHttpContext(ip);
            await pipeline(c);
        }

        var rejectedContext = CreateHttpContext(ip);
        await pipeline(rejectedContext);

        Assert.Equal(StatusCodes.Status429TooManyRequests, rejectedContext.Response.StatusCode);
        Assert.True(rejectedContext.Response.Headers.ContainsKey("Retry-After"));

        var retryAfterVal = rejectedContext.Response.Headers.RetryAfter.ToString();
        Assert.True(int.TryParse(retryAfterVal, CultureInfo.InvariantCulture, out var seconds));
        Assert.True(seconds >= 1, "Retry-After должен быть положительным числом секунд");
    }

    [Fact]
    public async Task MissingRemoteIp_FallsBackToUnknown_SafelyEnforcesLimit()
    {
        var pipeline = CreateRateLimitingPipeline();

        for (var i = 1; i <= 10; i++)
        {
            var context = CreateHttpContext(ipAddress: null);
            await pipeline(context);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        }

        var rejected = CreateHttpContext(ipAddress: null);
        await pipeline(rejected);

        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.Response.StatusCode);
    }

    [Theory]
    [InlineData(nameof(AuthController.Login))]
    [InlineData(nameof(AuthController.Register))]
    [InlineData(nameof(AuthController.ChangePassword))]
    public void AuthController_ProtectedActions_HaveEnableRateLimitingAttribute(string actionName)
    {
        var method = typeof(AuthController).GetMethod(actionName);
        Assert.NotNull(method);

        var attr = method.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(RateLimitingPolicies.AuthPolicy, attr.PolicyName);
    }

    [Theory]
    [InlineData(nameof(AuthController.GetCurrentUser))]
    [InlineData(nameof(AuthController.ChangeUsername))]
    public void AuthController_UnprotectedActions_DoNotHaveEnableRateLimitingAttribute(string actionName)
    {
        var method = typeof(AuthController).GetMethod(actionName);
        Assert.NotNull(method);

        var attr = method.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.Null(attr);
    }

    [Theory]
    [InlineData(nameof(AuthController.Login))]
    [InlineData(nameof(AuthController.Register))]
    [InlineData(nameof(AuthController.ChangePassword))]
    public void AuthController_ProtectedActions_HaveProduces429ResponseType(string actionName)
    {
        var method = typeof(AuthController).GetMethod(actionName);
        Assert.NotNull(method);

        var attr = method.GetCustomAttributes<ProducesResponseTypeAttribute>()
            .FirstOrDefault(a => a.StatusCode == StatusCodes.Status429TooManyRequests);
        Assert.NotNull(attr);
        Assert.Equal(typeof(ProblemDetails), attr.Type);
    }

    [Fact]
    public void ProblemDetails_Configures429_Correctly()
    {
        var options = new ProblemDetailsOptions();
        ApiProblemDetails.ConfigureProblemDetails(options);

        var httpContext = new DefaultHttpContext();
        httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        httpContext.Request.Path = "/api/auth/login";

        var problemDetails = new ProblemDetails { Status = StatusCodes.Status429TooManyRequests };
        var context = new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails
        };

        options.CustomizeProblemDetails!(context);

        Assert.Equal(429, problemDetails.Status);
        Assert.Equal("Слишком много запросов", problemDetails.Title);
        Assert.Equal("Превышен лимит частоты запросов. Пожалуйста, повторите попытку позже.", problemDetails.Detail);
        Assert.Equal(ApiErrorCodes.RateLimitExceeded, problemDetails.Extensions["code"]);
        Assert.Equal("/api/auth/login", problemDetails.Instance);
    }
}
