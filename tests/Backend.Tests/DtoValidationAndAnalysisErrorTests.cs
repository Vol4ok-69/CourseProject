using Api.Common;
using Api.Controllers;
using Application.DTOs;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Repository;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Xunit;

namespace Backend.Tests;

public sealed class DtoValidationAndAnalysisErrorTests
{
    private static List<ValidationResult> ValidateModel(object model)
    {
        var validationResults = new List<ValidationResult>();
        var context = new ValidationContext(model, serviceProvider: null, items: null);
        Validator.TryValidateObject(model, context, validationResults, validateAllProperties: true);
        return validationResults;
    }

    private static void SeedDatabase(AppDbContext context)
    {
        context.Roles.Add(new Role { Id = 1, Name = "User" });
        context.Users.Add(new User { Id = 1, Username = "developer", PasswordHash = "hash", RoleId = 1 });
        context.Projects.Add(new Project { Id = 1, Name = "Repo", RepoUrl = "https://github.com/test/repo", OwnerId = 1 });
        context.Commits.Add(new Commit { Id = 1, ProjectId = 1, CommitHash = "hash123", CommitDate = DateTime.UtcNow });
        context.SaveChanges();
    }

    [Fact]
    public void CreateProjectDto_ValidModel_PassesValidation()
    {
        var dto = new CreateProjectDto
        {
            Name = "MyProject",
            RepoUrl = "https://github.com/owner/repo"
        };

        var results = ValidateModel(dto);

        Assert.Empty(results);
    }

    [Theory]
    [InlineData(null, "Имя проекта обязательно для заполнения.")]
    [InlineData("", "Имя проекта обязательно для заполнения.")]
    [InlineData("ab", "Длина имени проекта должна быть от 3 до 50 символов.")]
    [InlineData("123456789012345678901234567890123456789012345678901", "Длина имени проекта должна быть от 3 до 50 символов.")]
    public void CreateProjectDto_InvalidName_FailsValidationWithRussianMessage(string? name, string expectedErrorMessage)
    {
        var dto = new CreateProjectDto
        {
            Name = name!,
            RepoUrl = "https://github.com/owner/repo"
        };

        var results = ValidateModel(dto);

        Assert.Contains(results, r => r.ErrorMessage == expectedErrorMessage);
    }

    [Theory]
    [InlineData(null, "URL репозитория обязателен для заполнения.")]
    [InlineData("", "URL репозитория обязателен для заполнения.")]
    [InlineData("http://gitlab.com/owner/repo", "URL репозитория должен иметь формат https://github.com/{owner}/{repo}.")]
    [InlineData("https://github.com/onlyone", "URL репозитория должен иметь формат https://github.com/{owner}/{repo}.")]
    [InlineData("not-a-url", "URL репозитория должен иметь формат https://github.com/{owner}/{repo}.")]
    public void CreateProjectDto_InvalidRepoUrl_FailsValidationWithRussianMessage(string? repoUrl, string expectedErrorMessage)
    {
        var dto = new CreateProjectDto
        {
            Name = "ValidName",
            RepoUrl = repoUrl!
        };

        var results = ValidateModel(dto);

        Assert.Contains(results, r => r.ErrorMessage == expectedErrorMessage);
    }

    [Fact]
    public void UpdateProjectDto_ValidModel_PassesValidation()
    {
        var dto = new UpdateProjectDto
        {
            Name = "UpdatedProject",
            RepoUrl = "https://github.com/owner/updated-repo.git"
        };

        var results = ValidateModel(dto);

        Assert.Empty(results);
    }

    [Theory]
    [InlineData(null, "Имя проекта обязательно для заполнения.")]
    [InlineData("x", "Длина имени проекта должна быть от 3 до 50 символов.")]
    public void UpdateProjectDto_InvalidName_FailsValidation(string? name, string expectedMessage)
    {
        var dto = new UpdateProjectDto
        {
            Name = name!,
            RepoUrl = "https://github.com/owner/repo"
        };

        var results = ValidateModel(dto);

        Assert.Contains(results, r => r.ErrorMessage == expectedMessage);
    }

    [Fact]
    public void CreateCommitDto_ValidModel_PassesValidation()
    {
        var dto = new CreateCommitDto
        {
            CommitHash = "e9ef5e43b99da445bb548b049d52e0e7cbf2a5ec",
            CommitDate = DateTime.UtcNow
        };

        var results = ValidateModel(dto);

        Assert.Empty(results);
    }

    [Theory]
    [InlineData(null, "Хэш коммита обязателен для заполнения.")]
    [InlineData("", "Хэш коммита обязателен для заполнения.")]
    [InlineData("123456", "Хэш коммита должен содержать от 7 до 40 символов.")]
    [InlineData("12345678901234567890123456789012345678901", "Хэш коммита должен содержать от 7 до 40 символов.")]
    public void CreateCommitDto_InvalidHash_FailsValidation(string? hash, string expectedMessage)
    {
        var dto = new CreateCommitDto
        {
            CommitHash = hash!,
            CommitDate = DateTime.UtcNow
        };

        var results = ValidateModel(dto);

        Assert.Contains(results, r => r.ErrorMessage == expectedMessage);
    }

    [Fact]
    public void ChangeUsernameDto_ValidModel_PassesValidation()
    {
        var dto = new ChangeUsernameDto { Username = "newusername" };

        var results = ValidateModel(dto);

        Assert.Empty(results);
    }

    [Theory]
    [InlineData(null, "Имя пользователя обязательно для заполнения.")]
    [InlineData("", "Имя пользователя обязательно для заполнения.")]
    [InlineData("ab", "Имя пользователя должно содержать от 3 до 50 символов.")]
    public void ChangeUsernameDto_InvalidUsername_FailsValidation(string? username, string expectedMessage)
    {
        var dto = new ChangeUsernameDto { Username = username! };

        var results = ValidateModel(dto);

        Assert.Contains(results, r => r.ErrorMessage == expectedMessage);
    }

    [Fact]
    public void ChangePasswordDto_ValidModel_PassesValidation()
    {
        var dto = new ChangePasswordDto
        {
            CurrentPassword = "OldPassword123!",
            NewPassword = "NewSecretPassword456!"
        };

        var results = ValidateModel(dto);

        Assert.Empty(results);
    }

    [Theory]
    [InlineData(null, "CurrentPassword", "Текущий пароль обязателен для заполнения.")]
    [InlineData("", "CurrentPassword", "Текущий пароль обязателен для заполнения.")]
    public void ChangePasswordDto_InvalidCurrentPassword_FailsValidation(string? pass, string propertyName, string expectedMessage)
    {
        var dto = new ChangePasswordDto
        {
            CurrentPassword = pass!,
            NewPassword = "ValidPassword123"
        };

        var results = ValidateModel(dto);

        Assert.Contains(results, r => r.MemberNames.Contains(propertyName) && r.ErrorMessage == expectedMessage);
    }

    [Theory]
    [InlineData(null, "Новый пароль обязателен для заполнения.")]
    [InlineData("", "Новый пароль обязателен для заполнения.")]
    [InlineData("12345", "Новый пароль должен содержать не менее 6 символов.")]
    public void ChangePasswordDto_InvalidNewPassword_FailsValidation(string? pass, string expectedMessage)
    {
        var dto = new ChangePasswordDto
        {
            CurrentPassword = "ValidCurrentPassword123",
            NewPassword = pass!
        };

        var results = ValidateModel(dto);

        Assert.Contains(results, r => r.ErrorMessage == expectedMessage);
    }

    [Fact]
    public void UpdateUserDto_ValidModel_PassesValidation()
    {
        var dto = new UpdateUserDto { Username = "adminupdated" };

        var results = ValidateModel(dto);

        Assert.Empty(results);
    }

    [Theory]
    [InlineData(null, "Имя пользователя обязательно для заполнения.")]
    [InlineData("ab", "Имя пользователя должно содержать от 3 до 50 символов.")]
    public void UpdateUserDto_InvalidUsername_FailsValidation(string? username, string expectedMessage)
    {
        var dto = new UpdateUserDto { Username = username! };

        var results = ValidateModel(dto);

        Assert.Contains(results, r => r.ErrorMessage == expectedMessage);
    }

    [Fact]
    public void UpdateUserRoleDto_ValidModel_PassesValidation()
    {
        var dto = new UpdateUserRoleDto { RoleId = 1 };

        var results = ValidateModel(dto);

        Assert.Empty(results);
    }

    [Theory]
    [InlineData(0, "Идентификатор роли должен быть положительным числом.")]
    [InlineData(-1, "Идентификатор роли должен быть положительным числом.")]
    public void UpdateUserRoleDto_InvalidRoleId_FailsValidation(int roleId, string expectedMessage)
    {
        var dto = new UpdateUserRoleDto { RoleId = roleId };

        var results = ValidateModel(dto);

        Assert.Contains(results, r => r.ErrorMessage == expectedMessage);
    }

    [Fact]
    public void Pipeline_InvalidModelState_Returns400ValidationProblemDetailsWithErrorsDictionary()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddFilter(_ => false));
        services.AddProblemDetails(ApiProblemDetails.ConfigureProblemDetails);
        services.Configure<ApiBehaviorOptions>(ApiProblemDetails.ConfigureInvalidModelStateResponse);

        var sp = services.BuildServiceProvider();
        var actionContext = new ActionContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = sp },
            RouteData = new Microsoft.AspNetCore.Routing.RouteData(),
            ActionDescriptor = new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()
        };
        actionContext.HttpContext.Request.Path = "/api/projects";
        actionContext.HttpContext.TraceIdentifier = "trace-val-123";

        actionContext.ModelState.AddModelError("Name", "Длина имени проекта должна быть от 3 до 50 символов.");
        actionContext.ModelState.AddModelError("RepoUrl", "URL репозитория должен иметь формат https://github.com/{owner}/{repo}.");

        var options = new ApiBehaviorOptions();
        ApiProblemDetails.ConfigureInvalidModelStateResponse(options);

        var actionResult = options.InvalidModelStateResponseFactory(actionContext);
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(actionResult);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequestResult.StatusCode);
        Assert.Contains("application/problem+json", badRequestResult.ContentTypes);

        var problemDetails = Assert.IsType<ValidationProblemDetails>(badRequestResult.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problemDetails.Status);
        Assert.Equal("Ошибка валидации входных данных", problemDetails.Title);
        Assert.Equal("Одно или несколько полей содержат некорректные данные.", problemDetails.Detail);
        Assert.Equal("/api/projects", problemDetails.Instance);
        Assert.Equal(ApiErrorCodes.ValidationFailed, problemDetails.Extensions["code"]);
        Assert.Equal("trace-val-123", problemDetails.Extensions["traceId"]);
        Assert.True(problemDetails.Errors.ContainsKey("Name"));
        Assert.True(problemDetails.Errors.ContainsKey("RepoUrl"));
        Assert.Equal("Длина имени проекта должна быть от 3 до 50 символов.", problemDetails.Errors["Name"].Single());
        Assert.Equal("URL репозитория должен иметь формат https://github.com/{owner}/{repo}.", problemDetails.Errors["RepoUrl"].Single());
    }

    [Fact]
    public async Task AnalysisRepository_FailAsync_SavesErrorMessageInDatabase()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        SeedDatabase(context);

        var repository = new AnalysisRepository(context);
        var token = Guid.NewGuid();

        var analysis = new Analysis
        {
            Id = 1,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            StartedAt = DateTime.UtcNow.AddSeconds(-30),
            LeaseExpiration = DateTime.UtcNow.AddMinutes(2),
            ErrorMessage = null
        };
        await repository.AddAsync(analysis);

        var failed = await repository.FailAsync(1, token, "Тестовая ошибка компиляции проекта.");

        Assert.True(failed);

        var updated = await repository.GetByIdAsync(1);
        Assert.NotNull(updated);
        Assert.Equal(AnalysisStatus.Failed, updated.Status);
        Assert.NotNull(updated.CompletedAt);
        Assert.Null(updated.LeaseExpiration);
        Assert.Equal("Тестовая ошибка компиляции проекта.", updated.ErrorMessage);
    }

    [Fact]
    public async Task AnalysisRepository_CompleteAsync_LeavesErrorMessageNull()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        SeedDatabase(context);

        var repository = new AnalysisRepository(context);
        var token = Guid.NewGuid();

        var analysis = new Analysis
        {
            Id = 2,
            CommitId = 1,
            Status = AnalysisStatus.Running,
            ExecutionToken = token,
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            StartedAt = DateTime.UtcNow.AddSeconds(-30),
            LeaseExpiration = DateTime.UtcNow.AddMinutes(2),
            ErrorMessage = null
        };
        await repository.AddAsync(analysis);

        var completed = await repository.CompleteAsync(2, token, []);

        Assert.True(completed);

        var updated = await repository.GetByIdAsync(2);
        Assert.NotNull(updated);
        Assert.Equal(AnalysisStatus.Completed, updated.Status);
        Assert.Null(updated.ErrorMessage);
    }

    [Fact]
    public async Task AnalysesController_GetById_ProvidesErrorMessageInDto()
    {
        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var projectRepo = new Mock<IProjectRepository>();

        var project = new Project { Id = 10, Name = "P", OwnerId = 1 };
        var commit = new Commit { Id = 20, ProjectId = 10, CommitHash = "hash123" };
        var analysis = new Analysis
        {
            Id = 30,
            CommitId = 20,
            Status = AnalysisStatus.Failed,
            CreatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            ErrorMessage = "Не удалось скомпилировать целевой проект: сборка завершилась с ошибками компилятора C#."
        };

        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        commitRepo.Setup(x => x.GetByIdAsync(20, It.IsAny<CancellationToken>())).ReturnsAsync(commit);
        analysisRepo.Setup(x => x.GetByIdAsync(30, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);

        var analysisService = new AnalysisService(analysisRepo.Object, commitRepo.Object, Mock.Of<IRepositorySourceService>(), []);
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

        Assert.Equal(AnalysisStatus.Failed, dto.Status);
        Assert.Equal("Не удалось скомпилировать целевой проект: сборка завершилась с ошибками компилятора C#.", dto.ErrorMessage);
    }

    [Theory]
    [InlineData("Коммит не найден.", "Коммит не найден.")]
    [InlineData("Не удалось распаковать архив: повреждённый файл", "Не удалось распаковать архив: повреждённый файл")]
    public void AnalysisService_SanitizeErrorMessage_PreservesSafeMessages(string input, string expected)
    {
        var ex = new InvalidOperationException(input);

        var sanitized = AnalysisService.SanitizeErrorMessage(ex);

        Assert.Equal(expected, sanitized);
    }

    [Theory]
    [InlineData("Database connection failed: Server=127.0.0.1;Password=secretpass;User ID=admin")]
    [InlineData("Failed to authenticate to GitHub with Bearer ghp_secret1234567890")]
    [InlineData("Invalid Token: Authorization header corrupted")]
    [InlineData("Connection string is missing Host=localhost")]
    public void AnalysisService_SanitizeErrorMessage_MasksSensitiveData(string sensitiveMessage)
    {
        var ex = new InvalidOperationException(sensitiveMessage);

        var sanitized = AnalysisService.SanitizeErrorMessage(ex);

        Assert.Equal("Во время выполнения анализа произошла внутренняя ошибка.", sanitized);
        Assert.DoesNotContain("secretpass", sanitized);
        Assert.DoesNotContain("ghp_secret", sanitized);
        Assert.DoesNotContain("127.0.0.1", sanitized);
    }

    [Fact]
    public void AnalysisService_SanitizeErrorMessage_MasksRuntimeExceptions()
    {
        var ex = new NullReferenceException("Object reference not set to an instance of an object.");

        var sanitized = AnalysisService.SanitizeErrorMessage(ex);

        Assert.Equal("Во время выполнения анализа произошла непредвиденная ошибка.", sanitized);
    }

    [Fact]
    public void AnalysisService_SanitizeErrorMessage_TruncatesOverlyLongMessages()
    {
        var longMessage = new string('A', 800);
        var ex = new InvalidOperationException(longMessage);

        var sanitized = AnalysisService.SanitizeErrorMessage(ex);

        Assert.Equal(500, sanitized.Length);
    }

    [Theory]
    [InlineData(@"Repository path not found: C:\Users\ivank\Desktop\CourseProject\repo", "Repository path not found: [путь]")]
    [InlineData(@"Error in C:\app\source\Program.cs: syntax error", "Error in Program.cs: syntax error")]
    [InlineData("/home/deploy/apps/src/Controller.cs: compiler warning CS0168", "Controller.cs: compiler warning CS0168")]
    public void AnalysisService_SanitizeErrorMessage_MasksInternalFilePaths(string pathMessage, string expected)
    {
        var ex = new InvalidOperationException(pathMessage);

        var sanitized = AnalysisService.SanitizeErrorMessage(ex);

        Assert.Equal(expected, sanitized);
        Assert.DoesNotContain(@"C:\Users\ivank", sanitized);
        Assert.DoesNotContain("/home/deploy", sanitized);
    }

    [Theory]
    [InlineData("Database connection timed out at 10.0.1.5:5432")]
    [InlineData("Internal service 192.168.0.24 returned 500")]
    [InlineData("Proxy at 172.20.10.2 failed")]
    public void AnalysisService_SanitizeErrorMessage_MasksInternalIpAddresses(string ipMessage)
    {
        var ex = new InvalidOperationException(ipMessage);

        var sanitized = AnalysisService.SanitizeErrorMessage(ex);

        Assert.Equal("Во время выполнения анализа произошла внутренняя ошибка.", sanitized);
    }

    [Fact]
    public void AnalysisService_SanitizeErrorMessage_DetectsSensitiveDataInInnerExceptions()
    {
        var inner = new InvalidOperationException("Database error: password=supersecret");
        var outer = new Exception("Processing failed", inner);

        var sanitized = AnalysisService.SanitizeErrorMessage(outer);

        Assert.Equal("Во время выполнения анализа произошла внутренняя ошибка.", sanitized);
        Assert.DoesNotContain("supersecret", sanitized);
    }

    [Fact]
    public void AnalysisService_SanitizeErrorMessage_StripsStackTrace()
    {
        var message = "Compilation error in file\r\n   at System.IO.FileStream.Validate() in /runtime/FileStream.cs:line 120";
        var ex = new InvalidOperationException(message);

        var sanitized = AnalysisService.SanitizeErrorMessage(ex);

        Assert.Equal("Compilation error in file", sanitized);
        Assert.DoesNotContain("at System.IO.FileStream", sanitized);
    }
}
