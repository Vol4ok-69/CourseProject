using Api.BackgroundServices;
using Application.Interfaces;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Moq;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Backend.Tests.Integration;

/// <summary>
/// Тестовая фабрика приложения на базе WebApplicationFactory для интеграционного тестирования HTTP-конвейера.
/// Запускает реальный ASP.NET Core хост с отдельным контейнером PostgreSQL и изолированными внешними сервисами.
/// </summary>
public class ApiWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly bool _ownsFixture;

    /// <summary>
    /// Секретный ключ подписи JWT для интеграционных тестов (256 бит).
    /// </summary>
    public const string TestJwtKey = "CourseProjectIntegrationTestKey_Secret256BitsLengthMustBeUsedHere!";

    /// <summary>
    /// Издатель JWT-токенов в интеграционных тестах.
    /// </summary>
    public const string TestJwtIssuer = "CourseProjectTestIssuer";

    /// <summary>
    /// Аудитория JWT-токенов в интеграционных тестах.
    /// </summary>
    public const string TestJwtAudience = "CourseProjectTestAudience";

    /// <summary>
    /// Мок сервиса взаимодействия с GitHub API.
    /// </summary>
    public Mock<IGitHubService> MockGitHubService { get; } = new();

    /// <summary>
    /// Мок сервиса безопасной работы с архивами исходного кода репозиториев.
    /// </summary>
    public Mock<IRepositorySourceService> MockRepositorySourceService { get; } = new();

    /// <summary>
    /// Флаг разрешения работы фонового сервиса AnalysisBackgroundService в тестовом хосте.
    /// По умолчанию отключен (false), чтобы предотвратить гонки за задачами анализа.
    /// </summary>
    public bool EnableAnalysisBackgroundService { get; set; }

    /// <summary>
    /// Строка подключения к тестовой базе данных PostgreSQL.
    /// </summary>
    public string ConnectionString => _fixture.ConnectionString;

    /// <summary>
    /// Инициализирует новую тестовую фабрику с собственным контейнером PostgreSQL.
    /// </summary>
    public ApiWebApplicationFactory()
    {
        _fixture = new PostgreSqlFixture();
        _ownsFixture = true;

        SetupDefaultMocks();
    }

    /// <summary>
    /// Инициализирует новую тестовую фабрику с разделяемой фикстурой PostgreSQL.
    /// </summary>
    /// <param name="fixture">Внешняя фикстура PostgreSQL.</param>
    internal ApiWebApplicationFactory(PostgreSqlFixture fixture)
    {
        _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
        _ownsFixture = false;

        SetupDefaultMocks();
    }

    private void SetupDefaultMocks()
    {
        MockGitHubService
            .Setup(g => g.VerifyTokenAndGetUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-github-user");

        MockGitHubService
            .Setup(g => g.GetCommitsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GitHubCommitInfo>
            {
                new("0123456789abcdef0123456789abcdef01234567", DateTime.UtcNow.AddDays(-1))
            });

        MockRepositorySourceService
            .Setup(r => r.DownloadAndExtractAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Path.GetTempPath());

        MockRepositorySourceService
            .Setup(r => r.CleanupAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    /// <summary>
    /// Сбрасывает настройки мок-сервисов к значениям по умолчанию для изоляции тестов.
    /// </summary>
    public void ResetMocks()
    {
        MockGitHubService.Reset();
        MockRepositorySourceService.Reset();
        SetupDefaultMocks();
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var aesKey = new byte[32];
        RandomNumberGenerator.Fill(aesKey);
        var aesKeyBase64 = Convert.ToBase64String(aesKey);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _fixture.ConnectionString,
                ["Security:TokenEncryptionKey"] = aesKeyBase64,
                ["Jwt:Key"] = TestJwtKey,
                ["Jwt:Issuer"] = TestJwtIssuer,
                ["Jwt:Audience"] = TestJwtAudience
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Настраиваем параметры валидации JWT для интеграционных тестов
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = TestJwtIssuer,
                    ValidAudience = TestJwtAudience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtKey))
                };
            });

            // Перенаправляем AppDbContext на тестовый экземпляр PostgreSQL
            var dbContextDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (dbContextDescriptor != null)
            {
                services.Remove(dbContextDescriptor);
            }

            services.AddDbContext<AppDbContext>(options =>
                options.UseNpgsql(_fixture.ConnectionString));

            // Изолируем фоновые воркеры, если не запрошено их явное включение
            if (!EnableAnalysisBackgroundService)
            {
                var backgroundWorkerDescriptor = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(AnalysisBackgroundService));

                if (backgroundWorkerDescriptor != null)
                {
                    services.Remove(backgroundWorkerDescriptor);
                }

                var cleanupWorkerDescriptor = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(AnalysisCleanupBackgroundService));

                if (cleanupWorkerDescriptor != null)
                {
                    services.Remove(cleanupWorkerDescriptor);
                }
            }

            // Подменяем внешние службы интеграции с GitHub
            var gitHubDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IGitHubService));
            if (gitHubDescriptor != null)
            {
                services.Remove(gitHubDescriptor);
            }
            services.AddSingleton(MockGitHubService.Object);

            var repoSourceDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IRepositorySourceService));
            if (repoSourceDescriptor != null)
            {
                services.Remove(repoSourceDescriptor);
            }
            services.AddSingleton(MockRepositorySourceService.Object);
        });
    }

    /// <summary>
    /// Очищает таблицы тестовой БД PostgreSQL между тестами.
    /// </summary>
    public async Task ResetDatabaseAsync()
    {
        await _fixture.ResetDatabaseAsync();
    }

    /// <summary>
    /// Выполняет действие с scoped экземпляром AppDbContext тестовой БД.
    /// </summary>
    public async Task ExecuteDbContextAsync(Func<AppDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await action(context);
    }

    /// <summary>
    /// Выполняет функцию с scoped экземпляром AppDbContext тестовой БД и возвращает результат.
    /// </summary>
    public async Task<T> ExecuteDbContextAsync<T>(Func<AppDbContext, Task<T>> func)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await func(context);
    }

    /// <summary>
    /// Генерирует действительный JWT-токен для указанного пользователя.
    /// </summary>
    public string GenerateValidToken(User user)
    {
        using var scope = Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();
        return tokenService.GenerateToken(user);
    }

    /// <summary>
    /// Генерирует просроченный JWT-токен для тестирования сценариев истечения срока действия.
    /// </summary>
    public string GenerateExpiredToken(User user)
    {
        return GenerateTokenInternal(user, TestJwtKey, DateTime.UtcNow.AddHours(-1));
    }

    /// <summary>
    /// Генерирует JWT-токен с неверной цифровой подписью для тестирования отклонения поддельных токенов.
    /// </summary>
    public string GenerateInvalidSignatureToken(User user)
    {
        const string wrongKey = "InvalidSecretKeyForTestingSignatureRejectionOnly_256BitsLength!";
        return GenerateTokenInternal(user, wrongKey, DateTime.UtcNow.AddHours(1));
    }

    private static string GenerateTokenInternal(User user, string secretKey, DateTime expires)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.Username),
            new Claim(ClaimTypes.Role, user.Role?.Name ?? "User")
        };

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken
        (
            issuer: TestJwtIssuer,
            audience: TestJwtAudience,
            claims: claims,
            expires: expires,
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Создает экземпляр HttpClient с предварительно установленным заголовком авторизации Bearer.
    /// </summary>
    public HttpClient CreateAuthenticatedClient(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// Запускает тестовую инфраструктуру PostgreSQL.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_ownsFixture)
        {
            await _fixture.InitializeAsync();
        }
    }

    /// <summary>
    /// Освобождает тестовую инфраструктуру.
    /// </summary>
    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();

        if (_ownsFixture)
        {
            await _fixture.DisposeAsync();
        }
    }
}
