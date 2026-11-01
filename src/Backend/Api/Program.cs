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
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

var gitHubToken = builder.Configuration["GitHub:Token"]
    ?? throw new InvalidOperationException("Токен GitHub не настроен.");

builder.Services.AddHttpClient<IGitHubService, GitHubService>(client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CourseProject", "1.0"));
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", gitHubToken);
});

builder.Services.AddHttpClient<IRepositorySourceService, RepositorySourceService>(client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CourseProject", "1.0"));
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", gitHubToken);
});

builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<CommitService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<GitHubSyncService>();
builder.Services.AddScoped<AnalysisService>();
builder.Services.AddHostedService<AnalysisBackgroundService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy(ApiCorsPolicies.FrontendDevPolicy, policy =>
    {
        policy.WithOrigins(ApiCorsPolicies.AllowedOrigins)
              .WithMethods(ApiCorsPolicies.AllowedMethods)
              .WithHeaders(ApiCorsPolicies.AllowedHeaders);
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

    options.Events = new JwtBearerEvents
    {
        OnAuthenticationFailed = context =>
        {
            Console.WriteLine($"JWT ERROR: {context.Exception.Message} FOR {context.Principal?.Identity?.Name}");
            return Task.CompletedTask;
        },
        OnTokenValidated = context =>
        {
            Console.WriteLine($"JWT VALIDATED FOR: {context.Principal?.Identity?.Name}");
            return Task.CompletedTask;
        },
        OnChallenge = async context =>
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
        },
        OnForbidden = async context =>
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
    };
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

app.MapControllers();

app.Run();
