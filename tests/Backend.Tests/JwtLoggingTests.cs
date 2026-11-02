using Api.Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using Xunit;

namespace Backend.Tests;

public sealed class JwtLoggingTests
{
    private sealed class LogEntry
    {
        public LogLevel LogLevel { get; init; }
        public EventId EventId { get; init; }
        public string? FormattedMessage { get; init; }
        public Exception? Exception { get; init; }
        public IReadOnlyList<KeyValuePair<string, object?>>? StateValues { get; init; }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state as IReadOnlyList<KeyValuePair<string, object?>>;

            Entries.Add(new LogEntry
            {
                LogLevel = logLevel,
                EventId = eventId,
                FormattedMessage = formatter(state, exception),
                Exception = exception,
                StateValues = values
            });
        }
    }

    private sealed class TestLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new TestLogger<ApiJwtEvents>();
        public void Dispose() { }
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenLoggerIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new ApiJwtEvents(null!));
    }

    [Fact]
    public async Task HandleTokenValidated_LogsInformation_WithUserId_AndNoSecrets()
    {
        var logger = new TestLogger<ApiJwtEvents>();
        var events = new ApiJwtEvents(logger);

        var httpContext = new DefaultHttpContext();
        var rawJwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NSJ9.fake_signature";
        httpContext.Request.Headers.Authorization = $"Bearer {rawJwt}";

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "42"),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("sensitive_claim", "top_secret_data")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        var options = new JwtBearerOptions();
        var context = new TokenValidatedContext(httpContext, scheme, options)
        {
            Principal = principal,
            SecurityToken = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken()
        };

        await events.TokenValidated(context);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.LogLevel);
        Assert.NotNull(entry.FormattedMessage);
        Assert.Contains("42", entry.FormattedMessage);

        // Проверяем, что в журнал не попали сырой JWT, заголовок Authorization и чувствительные claims
        Assert.DoesNotContain(rawJwt, entry.FormattedMessage);
        Assert.DoesNotContain("top_secret_data", entry.FormattedMessage);
        Assert.DoesNotContain("Bearer", entry.FormattedMessage);

        // Проверяем наличие именованного структурированного параметра {UserId}
        Assert.NotNull(entry.StateValues);
        var userIdParam = entry.StateValues.FirstOrDefault(kvp => kvp.Key == "UserId");
        Assert.Equal("42", userIdParam.Value?.ToString());
    }

    [Fact]
    public async Task HandleTokenValidated_WithoutUserId_LogsInformation_WithoutThrowing()
    {
        var logger = new TestLogger<ApiJwtEvents>();
        var events = new ApiJwtEvents(logger);

        var httpContext = new DefaultHttpContext();
        var identity = new ClaimsIdentity([], "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        var options = new JwtBearerOptions();
        var context = new TokenValidatedContext(httpContext, scheme, options)
        {
            Principal = principal
        };

        await events.TokenValidated(context);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.LogLevel);
        Assert.Equal("Успешная проверка JWT.", entry.FormattedMessage);
    }

    [Fact]
    public async Task HandleAuthenticationFailed_LogsInformation_WithNoSecrets_AndNoExceptionObject()
    {
        var logger = new TestLogger<ApiJwtEvents>();
        var events = new ApiJwtEvents(logger);

        var httpContext = new DefaultHttpContext();
        var rawJwt = "malformed_token_value_abc123";
        httpContext.Request.Headers.Authorization = $"Bearer {rawJwt}";

        var exception = new SecurityTokenInvalidSignatureException("IDX10500: Signature validation failed.");
        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        var options = new JwtBearerOptions();
        var context = new AuthenticationFailedContext(httpContext, scheme, options)
        {
            Exception = exception
        };

        await events.AuthenticationFailed(context);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.LogLevel);
        Assert.Null(entry.Exception);
        Assert.Equal("Ошибка аутентификации при проверке JWT.", entry.FormattedMessage);

        // Проверяем отсутствие сырого токена, заголовка авторизации и деталей исключения в сообщении
        Assert.DoesNotContain(rawJwt, entry.FormattedMessage);
        Assert.DoesNotContain("Bearer", entry.FormattedMessage);
        Assert.DoesNotContain("IDX10500", entry.FormattedMessage);
    }

    [Fact]
    public void ServiceCollection_ResolvesApiJwtEvents_FromDi()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ApiJwtEvents>();
        using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var events = scope.ServiceProvider.GetService<ApiJwtEvents>();
        Assert.NotNull(events);
    }

    [Fact]
    public void DevelopmentConfiguration_EnablesInformationLogLevel_ForJwtBearer()
    {
        var devConfig = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: false)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddConfiguration(devConfig.GetSection("Logging"));
            builder.AddProvider(new TestLoggerProvider());
        });

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ILoggerFactory>();

        var jwtBearerLogger = factory.CreateLogger("Microsoft.AspNetCore.Authentication.JwtBearer");
        var apiJwtEventsLogger = factory.CreateLogger(ApiJwtEvents.LoggerCategory);

        Assert.True(jwtBearerLogger.IsEnabled(LogLevel.Information), "В Development-окружении категория JwtBearer должна логировать на уровне Information.");
        Assert.True(apiJwtEventsLogger.IsEnabled(LogLevel.Information), "В Development-окружении категория ApiJwtEvents должна логировать на уровне Information.");
    }

    [Fact]
    public void ProductionConfiguration_DisablesInformationLogLevel_ForJwtBearer()
    {
        var prodConfig = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddConfiguration(prodConfig.GetSection("Logging"));
            builder.AddProvider(new TestLoggerProvider());
        });

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ILoggerFactory>();

        var jwtBearerLogger = factory.CreateLogger("Microsoft.AspNetCore.Authentication.JwtBearer");
        var apiJwtEventsLogger = factory.CreateLogger(ApiJwtEvents.LoggerCategory);

        Assert.False(jwtBearerLogger.IsEnabled(LogLevel.Information), "В Production-окружении категория JwtBearer НЕ должна логировать на уровне Information.");
        Assert.True(jwtBearerLogger.IsEnabled(LogLevel.Warning), "В Production-окружении категория JwtBearer должна логировать на уровне Warning.");

        Assert.False(apiJwtEventsLogger.IsEnabled(LogLevel.Information), "В Production-окружении категория ApiJwtEvents НЕ должна логировать на уровне Information.");
        Assert.True(apiJwtEventsLogger.IsEnabled(LogLevel.Warning), "В Production-окружении категория ApiJwtEvents должна логировать на уровне Warning.");
    }
}
