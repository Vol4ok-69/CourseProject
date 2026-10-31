using Api.Common;
using Api.Controllers;
using Application.DTOs;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Backend.Tests;

public sealed class ProblemDetailsAndEnumTests
{
    private readonly JsonSerializerOptions _jsonOptions;

    public ProblemDetailsAndEnumTests()
    {
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    [Theory]
    [InlineData(AnalysisStatus.Pending, "\"Pending\"")]
    [InlineData(AnalysisStatus.Running, "\"Running\"")]
    [InlineData(AnalysisStatus.Completed, "\"Completed\"")]
    [InlineData(AnalysisStatus.Failed, "\"Failed\"")]
    public void AnalysisStatus_SerializesAndDeserializes_AsString(AnalysisStatus status, string expectedJson)
    {
        var json = JsonSerializer.Serialize(status, _jsonOptions);
        Assert.Equal(expectedJson, json);

        var deserialized = JsonSerializer.Deserialize<AnalysisStatus>(json, _jsonOptions);
        Assert.Equal(status, deserialized);
    }

    [Theory]
    [InlineData(Severity.Info, "\"Info\"")]
    [InlineData(Severity.Warning, "\"Warning\"")]
    [InlineData(Severity.Error, "\"Error\"")]
    [InlineData(Severity.Critical, "\"Critical\"")]
    public void Severity_SerializesAndDeserializes_AsString(Severity severity, string expectedJson)
    {
        var json = JsonSerializer.Serialize(severity, _jsonOptions);
        Assert.Equal(expectedJson, json);

        var deserialized = JsonSerializer.Deserialize<Severity>(json, _jsonOptions);
        Assert.Equal(severity, deserialized);
    }

    [Theory]
    [InlineData(AnalyzerType.Security, "\"Security\"")]
    [InlineData(AnalyzerType.Sql, "\"Sql\"")]
    [InlineData(AnalyzerType.CSharp, "\"CSharp\"")]
    [InlineData(AnalyzerType.Database, "\"Database\"")]
    public void AnalyzerType_SerializesAndDeserializes_AsString(AnalyzerType analyzerType, string expectedJson)
    {
        var json = JsonSerializer.Serialize(analyzerType, _jsonOptions);
        Assert.Equal(expectedJson, json);

        var deserialized = JsonSerializer.Deserialize<AnalyzerType>(json, _jsonOptions);
        Assert.Equal(analyzerType, deserialized);
    }

    [Fact]
    public void ApiProblemDetails_Create_SetsAllRequiredRfc9457Fields()
    {
        var result = ApiProblemDetails.Create(
            StatusCodes.Status409Conflict,
            "Конфликт данных",
            "Пользователь с таким логином уже существует.",
            ApiErrorCodes.UsernameAlreadyExists,
            "/api/auth/register",
            "test-trace-id-123");

        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Contains("application/problem+json", result.ContentTypes);

        var problemDetails = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal(StatusCodes.Status409Conflict, problemDetails.Status);
        Assert.Equal("Конфликт данных", problemDetails.Title);
        Assert.Equal("Пользователь с таким логином уже существует.", problemDetails.Detail);
        Assert.Equal("/api/auth/register", problemDetails.Instance);
        Assert.Equal("https://httpstatuses.com/409", problemDetails.Type);
        Assert.Equal(ApiErrorCodes.UsernameAlreadyExists, problemDetails.Extensions["code"]);
        Assert.Equal("test-trace-id-123", problemDetails.Extensions["traceId"]);
    }

    [Fact]
    public void ApiProblemDetails_Create_WithControllerContext_ExtractsTraceIdAndInstance()
    {
        var controller = new AuthController(null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    TraceIdentifier = "ctx-trace-999"
                }
            }
        };
        controller.HttpContext.Request.Path = "/api/test/path";

        var result = ApiProblemDetails.Create(
            controller,
            StatusCodes.Status400BadRequest,
            "Некорректный запрос",
            "Тестовая ошибка",
            ApiErrorCodes.ValidationFailed);

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Contains("application/problem+json", result.ContentTypes);

        var problemDetails = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal("/api/test/path", problemDetails.Instance);
        Assert.Equal("ctx-trace-999", problemDetails.Extensions["traceId"]);
        Assert.Equal(ApiErrorCodes.ValidationFailed, problemDetails.Extensions["code"]);
        Assert.Equal("https://httpstatuses.com/400", problemDetails.Type);
    }

    [Fact]
    public async Task Pipeline_UnhandledException_Returns500ProblemDetailsWithMaskedDetails()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddFilter(_ => false));
        services.AddMetrics();
        services.AddSingleton(new System.Diagnostics.DiagnosticListener("Microsoft.AspNetCore"));
        services.AddProblemDetails(ApiProblemDetails.ConfigureProblemDetails);

        var sp = services.BuildServiceProvider();
        var app = new ApplicationBuilder(sp);

        app.UseExceptionHandler();
        app.Run(_ => throw new InvalidOperationException("Критическая ошибка базы данных: пароль=secret123"));

        var pipeline = app.Build();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        httpContext.Request.Path = "/api/test/crash";
        httpContext.TraceIdentifier = "trace-crash-500";
        var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;

        await pipeline(httpContext);

        Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);
        Assert.Contains("application/problem+json", httpContext.Response.ContentType);

        responseBody.Seek(0, SeekOrigin.Begin);
        var json = await new StreamReader(responseBody).ReadToEndAsync();

        Assert.DoesNotContain("secret123", json);
        Assert.DoesNotContain("InvalidOperationException", json);

        var problem = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.Equal(500, problem.GetProperty("status").GetInt32());
        Assert.Equal("Внутренняя ошибка сервера", problem.GetProperty("title").GetString());
        Assert.Equal("Произошла непредвиденная ошибка на стороне сервера.", problem.GetProperty("detail").GetString());
        Assert.Equal("https://httpstatuses.com/500", problem.GetProperty("type").GetString());
        Assert.Equal("/api/test/crash", problem.GetProperty("instance").GetString());
        Assert.Equal(ApiErrorCodes.InternalServerError, problem.GetProperty("code").GetString());
        Assert.Equal("trace-crash-500", problem.GetProperty("traceId").GetString());
        Assert.False(problem.TryGetProperty("errors", out _));
    }

    [Theory]
    [InlineData("/api/projects/999", ApiErrorCodes.ProjectNotFound)]
    [InlineData("/api/projects/1/commits/999", ApiErrorCodes.CommitNotFound)]
    [InlineData("/api/projects/1/commits/2/analyses/999", ApiErrorCodes.AnalysisNotFound)]
    [InlineData("/api/admin/users/999", ApiErrorCodes.UserNotFound)]
    [InlineData("/api/auth/me", ApiErrorCodes.UserNotFound)]
    [InlineData("/api/nonexistent", "NOT_FOUND")]
    public async Task Pipeline_StatusCodePages_Converts404ToProblemDetailsWithExactCode(string path, string expectedCode)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddFilter(_ => false));
        services.AddProblemDetails(ApiProblemDetails.ConfigureProblemDetails);

        var sp = services.BuildServiceProvider();
        var app = new ApplicationBuilder(sp);

        app.UseStatusCodePages();
        app.Run(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });

        var pipeline = app.Build();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        httpContext.Request.Path = path;
        httpContext.TraceIdentifier = "trace-404-test";
        var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;

        await pipeline(httpContext);

        Assert.Equal(StatusCodes.Status404NotFound, httpContext.Response.StatusCode);
        Assert.Contains("application/problem+json", httpContext.Response.ContentType);

        responseBody.Seek(0, SeekOrigin.Begin);
        var json = await new StreamReader(responseBody).ReadToEndAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(json);

        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Equal("Ресурс не найден", problem.GetProperty("title").GetString());
        Assert.Equal("Запрашиваемый ресурс не найден.", problem.GetProperty("detail").GetString());
        Assert.Equal("https://httpstatuses.com/404", problem.GetProperty("type").GetString());
        Assert.Equal(path, problem.GetProperty("instance").GetString());
        Assert.Equal(expectedCode, problem.GetProperty("code").GetString());
        Assert.Equal("trace-404-test", problem.GetProperty("traceId").GetString());
        Assert.False(problem.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task Pipeline_StatusCodePages_ConvertsEmpty400ToProblemDetails()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddFilter(_ => false));
        services.AddProblemDetails(ApiProblemDetails.ConfigureProblemDetails);

        var sp = services.BuildServiceProvider();
        var app = new ApplicationBuilder(sp);

        app.UseStatusCodePages();
        app.Run(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        });

        var pipeline = app.Build();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        httpContext.Request.Path = "/api/projects";
        httpContext.TraceIdentifier = "trace-400-test";
        var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;

        await pipeline(httpContext);

        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
        Assert.Contains("application/problem+json", httpContext.Response.ContentType);

        responseBody.Seek(0, SeekOrigin.Begin);
        var json = await new StreamReader(responseBody).ReadToEndAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(json);

        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal("Некорректный запрос", problem.GetProperty("title").GetString());
        Assert.Equal("Запрос содержит некорректные данные.", problem.GetProperty("detail").GetString());
        Assert.Equal("https://httpstatuses.com/400", problem.GetProperty("type").GetString());
        Assert.Equal("/api/projects", problem.GetProperty("instance").GetString());
        Assert.Equal(ApiErrorCodes.ValidationFailed, problem.GetProperty("code").GetString());
        Assert.Equal("trace-400-test", problem.GetProperty("traceId").GetString());
        Assert.False(problem.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task Pipeline_StatusCodePages_ConvertsEmpty409ToProblemDetails()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddFilter(_ => false));
        services.AddProblemDetails(ApiProblemDetails.ConfigureProblemDetails);

        var sp = services.BuildServiceProvider();
        var app = new ApplicationBuilder(sp);

        app.UseStatusCodePages();
        app.Run(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status409Conflict;
            return Task.CompletedTask;
        });

        var pipeline = app.Build();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        httpContext.Request.Path = "/api/projects";
        httpContext.TraceIdentifier = "trace-409-test";
        var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;

        await pipeline(httpContext);

        Assert.Equal(StatusCodes.Status409Conflict, httpContext.Response.StatusCode);
        Assert.Contains("application/problem+json", httpContext.Response.ContentType);

        responseBody.Seek(0, SeekOrigin.Begin);
        var json = await new StreamReader(responseBody).ReadToEndAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(json);

        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Equal("Конфликт данных", problem.GetProperty("title").GetString());
        Assert.Equal("Запрос конфликтует с текущим состоянием ресурса.", problem.GetProperty("detail").GetString());
        Assert.Equal("https://httpstatuses.com/409", problem.GetProperty("type").GetString());
        Assert.Equal("/api/projects", problem.GetProperty("instance").GetString());
        Assert.Equal("CONFLICT", problem.GetProperty("code").GetString());
        Assert.Equal("trace-409-test", problem.GetProperty("traceId").GetString());
        Assert.False(problem.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task Pipeline_JwtEvents_OnChallenge_Returns401ProblemDetails()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/projects";
        httpContext.TraceIdentifier = "trace-jwt-challenge";
        var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;

        var jwtEvents = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                if (context.Response.HasStarted) return;
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
        };

        var jwtContext = new JwtBearerChallengeContext(
            httpContext,
            new Microsoft.AspNetCore.Authentication.AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions(),
            new Microsoft.AspNetCore.Authentication.AuthenticationProperties());

        await jwtEvents.Challenge(jwtContext);

        Assert.Equal(StatusCodes.Status401Unauthorized, httpContext.Response.StatusCode);
        Assert.Contains("application/problem+json", httpContext.Response.ContentType);

        responseBody.Seek(0, SeekOrigin.Begin);
        var json = await new StreamReader(responseBody).ReadToEndAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(json);

        Assert.Equal(401, problem.GetProperty("status").GetInt32());
        Assert.Equal("Требуется аутентификация", problem.GetProperty("title").GetString());
        Assert.Equal("https://httpstatuses.com/401", problem.GetProperty("type").GetString());
        Assert.Equal("/api/projects", problem.GetProperty("instance").GetString());
        Assert.Equal(ApiErrorCodes.Unauthorized, problem.GetProperty("code").GetString());
        Assert.Equal("trace-jwt-challenge", problem.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task Pipeline_JwtEvents_OnForbidden_Returns403ProblemDetails()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/admin/users";
        httpContext.TraceIdentifier = "trace-jwt-forbidden";
        var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;

        var jwtEvents = new JwtBearerEvents
        {
            OnForbidden = async context =>
            {
                if (context.Response.HasStarted) return;
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

        var forbiddenContext = new ForbiddenContext(
            httpContext,
            new Microsoft.AspNetCore.Authentication.AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions());

        await jwtEvents.Forbidden(forbiddenContext);

        Assert.Equal(StatusCodes.Status403Forbidden, httpContext.Response.StatusCode);
        Assert.Contains("application/problem+json", httpContext.Response.ContentType);

        responseBody.Seek(0, SeekOrigin.Begin);
        var json = await new StreamReader(responseBody).ReadToEndAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(json);

        Assert.Equal(403, problem.GetProperty("status").GetInt32());
        Assert.Equal("Доступ запрещен", problem.GetProperty("title").GetString());
        Assert.Equal("https://httpstatuses.com/403", problem.GetProperty("type").GetString());
        Assert.Equal("/api/admin/users", problem.GetProperty("instance").GetString());
        Assert.Equal(ApiErrorCodes.Forbidden, problem.GetProperty("code").GetString());
        Assert.Equal("trace-jwt-forbidden", problem.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task AuthRegister_DuplicateUsername_Returns409ConflictProblemDetails()
    {
        var userRepo = new Mock<IUserRepository>();
        var roleRepo = new Mock<IRoleRepository>();
        var passwordHasher = new Mock<IPasswordHasher>();
        var tokenService = new Mock<ITokenService>();

        userRepo.Setup(x => x.GetByUsernameAsync("existinguser", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = 1, Username = "existinguser", PasswordHash = "hash", RoleId = 2 });

        var authService = new AuthService(userRepo.Object, passwordHasher.Object, tokenService.Object);
        var userService = new UserService(userRepo.Object, roleRepo.Object);
        var controller = new AuthController(authService, userService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var actionResult = await controller.Register(new RegisterDto
        {
            Username = "existinguser",
            Password = "Password123!"
        }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status409Conflict, problemDetails.Status);
        Assert.Equal("Конфликт данных", problemDetails.Title);
        Assert.Equal(ApiErrorCodes.UsernameAlreadyExists, problemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task AuthLogin_InvalidCredentials_Returns401UnauthorizedProblemDetails()
    {
        var userRepo = new Mock<IUserRepository>();
        var roleRepo = new Mock<IRoleRepository>();
        var passwordHasher = new Mock<IPasswordHasher>();
        var tokenService = new Mock<ITokenService>();

        userRepo.Setup(x => x.GetByUsernameAsync("user", It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        var authService = new AuthService(userRepo.Object, passwordHasher.Object, tokenService.Object);
        var userService = new UserService(userRepo.Object, roleRepo.Object);
        var controller = new AuthController(authService, userService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var actionResult = await controller.Login(new LoginDto
        {
            Username = "user",
            Password = "WrongPassword"
        }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status401Unauthorized, problemDetails.Status);
        Assert.Equal(ApiErrorCodes.InvalidCredentials, problemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task AuthChangeUsername_DuplicateUsername_Returns409ConflictProblemDetails()
    {
        var userRepo = new Mock<IUserRepository>();
        var roleRepo = new Mock<IRoleRepository>();
        var passwordHasher = new Mock<IPasswordHasher>();
        var tokenService = new Mock<ITokenService>();

        userRepo.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = 1, Username = "oldname", PasswordHash = "hash", RoleId = 2 });
        userRepo.Setup(x => x.GetByUsernameAsync("takenname", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = 2, Username = "takenname", PasswordHash = "hash2", RoleId = 2 });

        var authService = new AuthService(userRepo.Object, passwordHasher.Object, tokenService.Object);
        var userService = new UserService(userRepo.Object, roleRepo.Object);
        var controller = new AuthController(authService, userService);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "1") };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };

        var actionResult = await controller.ChangeUsername(new ChangeUsernameDto { Username = "takenname" }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status409Conflict, problemDetails.Status);
        Assert.Equal(ApiErrorCodes.UsernameAlreadyExists, problemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task AuthChangePassword_InvalidCurrentPassword_Returns400BadRequestProblemDetailsWithoutErrorsDict()
    {
        var userRepo = new Mock<IUserRepository>();
        var roleRepo = new Mock<IRoleRepository>();
        var passwordHasher = new Mock<IPasswordHasher>();
        var tokenService = new Mock<ITokenService>();

        userRepo.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = 1, Username = "testuser", PasswordHash = "correcthash", RoleId = 2 });
        passwordHasher.Setup(x => x.Verify("wrongpass", "correcthash")).Returns(false);

        var authService = new AuthService(userRepo.Object, passwordHasher.Object, tokenService.Object);
        var userService = new UserService(userRepo.Object, roleRepo.Object);
        var controller = new AuthController(authService, userService);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "1") };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };

        var actionResult = await controller.ChangePassword(new ChangePasswordDto
        {
            CurrentPassword = "wrongpass",
            NewPassword = "newsecretpassword123"
        }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problemDetails.Status);
        Assert.Equal(ApiErrorCodes.InvalidPassword, problemDetails.Extensions["code"]);
        Assert.False(problemDetails.Extensions.ContainsKey("errors"));
    }

    [Fact]
    public async Task CommitsCreate_DuplicateCommitHash_Returns409ConflictProblemDetails()
    {
        var commitRepo = new Mock<ICommitRepository>();
        var projectRepo = new Mock<IProjectRepository>();

        var project = new Project { Id = 10, Name = "TestProject", OwnerId = 1 };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        commitRepo.Setup(x => x.GetByHashAsync(10, "abc1234", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Commit { Id = 1, ProjectId = 10, CommitHash = "abc1234" });

        var commitService = new CommitService(commitRepo.Object, projectRepo.Object);
        var projectService = new ProjectService(projectRepo.Object);

        var controller = new CommitsController(commitService, projectService);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim(ClaimTypes.Role, "User")
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };

        var actionResult = await controller.Create(10, new CreateCommitDto
        {
            CommitHash = "abc1234",
            CommitDate = DateTime.UtcNow
        }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status409Conflict, problemDetails.Status);
        Assert.Equal("Конфликт данных", problemDetails.Title);
        Assert.Equal(ApiErrorCodes.CommitAlreadyExists, problemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task ProjectsCreate_DuplicateProjectName_Returns409ConflictProblemDetails()
    {
        var projectRepo = new Mock<IProjectRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var gitHubService = new Mock<IGitHubService>();

        projectRepo.Setup(x => x.GetByOwnerIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Project { Id = 1, Name = "ExistingProject", OwnerId = 1 }]);

        var projectService = new ProjectService(projectRepo.Object);
        var gitHubSyncService = new GitHubSyncService(gitHubService.Object, projectRepo.Object, commitRepo.Object);

        var controller = new ProjectsController(projectService, gitHubSyncService);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim(ClaimTypes.Role, "User")
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };

        var actionResult = await controller.Create(new CreateProjectDto
        {
            Name = "ExistingProject",
            RepoUrl = "https://github.com/owner/repo"
        }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status409Conflict, problemDetails.Status);
        Assert.Equal("Конфликт данных", problemDetails.Title);
        Assert.Equal(ApiErrorCodes.ProjectNameDuplicate, problemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task ProjectsUpdate_DuplicateProjectName_Returns409ConflictProblemDetails()
    {
        var projectRepo = new Mock<IProjectRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var gitHubService = new Mock<IGitHubService>();

        var existingProject1 = new Project { Id = 1, Name = "Alpha", OwnerId = 1 };
        var existingProject2 = new Project { Id = 2, Name = "Beta", OwnerId = 1 };

        projectRepo.Setup(x => x.GetByIdAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingProject2);
        projectRepo.Setup(x => x.GetByOwnerIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([existingProject1, existingProject2]);

        var projectService = new ProjectService(projectRepo.Object);
        var gitHubSyncService = new GitHubSyncService(gitHubService.Object, projectRepo.Object, commitRepo.Object);

        var controller = new ProjectsController(projectService, gitHubSyncService);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim(ClaimTypes.Role, "User")
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };

        var actionResult = await controller.Update(2, new UpdateProjectDto
        {
            Name = "Alpha",
            RepoUrl = "https://github.com/owner/beta"
        }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status409Conflict, problemDetails.Status);
        Assert.Equal("Конфликт данных", problemDetails.Title);
        Assert.Equal(ApiErrorCodes.ProjectNameDuplicate, problemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task AdminUsersUpdate_DuplicateUsername_Returns409ConflictProblemDetails()
    {
        var userRepo = new Mock<IUserRepository>();
        var roleRepo = new Mock<IRoleRepository>();

        var targetUser = new User { Id = 10, Username = "user10", RoleId = 2 };
        var conflictingUser = new User { Id = 11, Username = "user11", RoleId = 2 };

        userRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(targetUser);
        userRepo.Setup(x => x.GetByUsernameAsync("user11", It.IsAny<CancellationToken>()))
            .ReturnsAsync(conflictingUser);

        var userService = new UserService(userRepo.Object, roleRepo.Object);
        var controller = new AdminUsersController(userService);

        var actionResult = await controller.Update(10, new UpdateUserDto
        {
            Username = "user11"
        }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status409Conflict, problemDetails.Status);
        Assert.Equal(ApiErrorCodes.UsernameAlreadyExists, problemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task AdminUsersUpdateRole_RoleNotFound_Returns404NotFoundProblemDetails()
    {
        var userRepo = new Mock<IUserRepository>();
        var roleRepo = new Mock<IRoleRepository>();

        var targetUser = new User { Id = 10, Username = "user10", RoleId = 2 };
        userRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(targetUser);
        roleRepo.Setup(x => x.GetByIdAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Role?)null);

        var userService = new UserService(userRepo.Object, roleRepo.Object);
        var controller = new AdminUsersController(userService);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Role, "Admin") };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };

        var actionResult = await controller.UpdateRole(10, new UpdateUserRoleDto { RoleId = 999 }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status404NotFound, problemDetails.Status);
        Assert.Equal(ApiErrorCodes.RoleNotFound, problemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task AdminUsersDelete_UserHasProjects_Returns409ConflictProblemDetails()
    {
        var userRepo = new Mock<IUserRepository>();
        var roleRepo = new Mock<IRoleRepository>();

        var user = new User { Id = 5, Username = "userwithprojects", RoleId = 2 };
        userRepo.Setup(x => x.GetByIdAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        userRepo.Setup(x => x.HasProjectsAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var userService = new UserService(userRepo.Object, roleRepo.Object);
        var controller = new AdminUsersController(userService);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim(ClaimTypes.Role, "Admin")
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };

        var actionResult = await controller.Delete(5, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status409Conflict, problemDetails.Status);
        Assert.Equal("Конфликт данных", problemDetails.Title);
        Assert.Equal(ApiErrorCodes.UserHasProjects, problemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task AnalysesController_GetById_SerializesEnumsAsStringsInActualDtoResponse()
    {
        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var projectRepo = new Mock<IProjectRepository>();

        var project = new Project { Id = 10, Name = "TestProject", OwnerId = 1 };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        var commit = new Commit { Id = 20, ProjectId = 10, CommitHash = "hash123", CommitDate = DateTime.UtcNow };
        commitRepo.Setup(x => x.GetByIdAsync(20, It.IsAny<CancellationToken>())).ReturnsAsync(commit);

        var analysis = new Analysis
        {
            Id = 30,
            CommitId = 20,
            Status = AnalysisStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            Results =
            [
                new AnalysisResult
                {
                    Id = 1,
                    AnalysisId = 30,
                    FilePath = "Program.cs",
                    LineNumber = 10,
                    Rule = "SEC001",
                    Message = "SQL Injection",
                    Severity = Severity.Critical,
                    AnalyzerType = AnalyzerType.Security
                }
            ]
        };
        analysisRepo.Setup(x => x.GetByIdAsync(30, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);

        var sourceService = new Mock<IRepositorySourceService>();
        var analysisService = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, []);
        var commitService = new CommitService(commitRepo.Object, projectRepo.Object);
        var projectService = new ProjectService(projectRepo.Object);

        var controller = new AnalysesController(analysisService, commitService, projectService);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Role, "User") };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };

        var actionResult = await controller.GetById(10, 20, 30, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<AnalysisDto>(okResult.Value);

        var json = JsonSerializer.Serialize(dto, _jsonOptions);

        Assert.Contains("\"status\":\"Completed\"", json);
        Assert.Contains("\"severity\":\"Critical\"", json);
        Assert.Contains("\"analyzerType\":\"Security\"", json);

        Assert.DoesNotContain("\"status\":2", json);
        Assert.DoesNotContain("\"severity\":3", json);
        Assert.DoesNotContain("\"analyzerType\":0", json);
    }
}
