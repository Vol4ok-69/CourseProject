using Api.BackgroundServices;
using Api.Common;
using Application.Analyzers;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Application.Services;
using CSharp;
using DatabaseAnalyzer;
using DatabaseAnalyzer.Mapping;
using DatabaseAnalyzer.Providers;
using Infrastructure.Data;
using Infrastructure.GitHub;
using Infrastructure.Repository;
using Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Строка подключения 'DefaultConnection' не найдена.");

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IProjectRepository, ProjectRepository>();
builder.Services.AddScoped<ICommitRepository, CommitRepository>();
builder.Services.AddScoped<IAnalysisRepository, AnalysisRepository>();
builder.Services.AddScoped<IRoleRepository, RoleRepository>();
builder.Services.AddScoped<IPasswordHasher, PasswordHasher>();
builder.Services.AddScoped<ITokenService, JwtTokenService>();
builder.Services.AddScoped<IAnalyzer, CSharpAnalyzer>();
builder.Services.AddScoped<IAnalyzer, DatabaseAnalyzer.DatabaseAnalyzer>();
builder.Services.AddScoped<IAnalyzer, SqlAnalyzer.SqlAnalyzer>();
builder.Services.AddScoped<IAnalyzer, SecurityAnalyzer.SecurityAnalyzer>();
builder.Services.AddScoped<IDatabaseMetadataProvider, EfCoreMetadataProvider>();
builder.Services.AddScoped<IDatabaseProjectResolver, EfCoreDatabaseProjectResolver>();
builder.Services.AddScoped<IEfCoreModelInspector, EfCoreModelInspector>();
builder.Services.AddScoped<EfCoreMetadataMapper>();

var tokenEncryptionKeyBase64 = builder.Configuration["Security:TokenEncryptionKey"];
if (string.IsNullOrWhiteSpace(tokenEncryptionKeyBase64))
{
    throw new InvalidOperationException("Ключ шифрования токенов 'Security:TokenEncryptionKey' не настроен.");
}

byte[] tokenEncryptionKey;
try
{
    tokenEncryptionKey = Convert.FromBase64String(tokenEncryptionKeyBase64);
}
catch (FormatException ex)
{
    throw new InvalidOperationException("Ключ шифрования токенов 'Security:TokenEncryptionKey' имеет некорректный формат Base64.", ex);
}

if (tokenEncryptionKey.Length != 32)
{
    throw new InvalidOperationException(
        $"Ключ шифрования токенов должен иметь длину 32 байта (AES-256). Фактическая длина: {tokenEncryptionKey.Length} байт.");
}

builder.Services.AddSingleton<ITokenEncryptionService>(new AesGcmTokenEncryptionService(tokenEncryptionKey));

builder.Services.AddHttpClient<IGitHubService, GitHubService>(client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CourseProject", "1.0"));
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
});

builder.Services.AddHttpClient<IRepositorySourceService, RepositorySourceService>(client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CourseProject", "1.0"));
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
});

builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<CommitService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<GitHubSyncService>();
builder.Services.AddScoped<AnalysisService>();
builder.Services.AddScoped<IGitHubCredentialService, GitHubCredentialService>();
builder.Services.AddHostedService<AnalysisBackgroundService>();
builder.Services.AddScoped<ApiJwtEvents>();

builder.Services.AddCors(options =>
{
    options.AddPolicy(ApiCorsPolicies.FrontendDevPolicy, policy =>
    {
        policy.WithOrigins(ApiCorsPolicies.AllowedOrigins)
              .WithMethods(ApiCorsPolicies.AllowedMethods)
              .WithHeaders(ApiCorsPolicies.AllowedHeaders);
    });
});

builder.Services.AddRateLimiter(options =>
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

var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Секретный ключ JWT не настроен.");

var jwtIssuer = builder.Configuration["Jwt:Issuer"]
    ?? throw new InvalidOperationException("Издатель токена JWT (issuer) не настроен.");

var jwtAudience = builder.Configuration["Jwt:Audience"]
    ?? throw new InvalidOperationException("Аудитория токена JWT (audience) не настроена.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtIssuer,
        ValidAudience = jwtAudience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
    };

    options.EventsType = typeof(ApiJwtEvents);
});

builder.Services.AddAuthorization();

builder.Services.AddProblemDetails(ApiProblemDetails.ConfigureProblemDetails);

builder.Services.Configure<ApiBehaviorOptions>(ApiProblemDetails.ConfigureInvalidModelStateResponse);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "CourseProject API",
        Version = "v1",
        Description = "REST API сервиса автоматизированного аудита исходного кода и схем реляционных баз данных CourseProject."
    });

    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }

    var appXmlPath = Path.Combine(AppContext.BaseDirectory, "Application.xml");
    if (File.Exists(appXmlPath))
    {
        options.IncludeXmlComments(appXmlPath);
    }

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Введите чистый JWT-токен (без префикса Bearer)"
    });

    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = []
    });
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseCors(ApiCorsPolicies.FrontendDevPolicy);

app.UseAuthentication();
app.UseAuthorization();

app.UseRateLimiter();

app.MapControllers();

app.Run();
