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
using Moq;
using System.Security.Claims;
using Xunit;

namespace Backend.Tests;

public sealed class PaginationTests
{
    private static (SqliteConnection Connection, DbContextOptions<AppDbContext> Options) CreateInMemoryDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new AppDbContext(options);
        context.Database.EnsureCreated();

        var adminRole = new Role { Id = 1, Name = "Admin" };
        var userRole = new Role { Id = 2, Name = "User" };
        context.Roles.AddRange(adminRole, userRole);
        context.SaveChanges();

        return (connection, options);
    }

    private static (CommitsController Controller, Mock<ICommitRepository> CommitRepo, Mock<IProjectRepository> ProjectRepo) CreateCommitsController(string? userIdClaim = "1", bool isAdmin = false)
    {
        var commitRepo = new Mock<ICommitRepository>();
        var projectRepo = new Mock<IProjectRepository>();
        var commitService = new CommitService(commitRepo.Object, projectRepo.Object);
        var projectService = new ProjectService(projectRepo.Object);

        var controller = new CommitsController(commitService, projectService);

        var claims = new List<Claim>();
        if (userIdClaim is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userIdClaim));
            claims.Add(new Claim(ClaimTypes.Name, "testuser"));
            claims.Add(new Claim(ClaimTypes.Role, isAdmin ? "Admin" : "User"));
        }

        var identity = new ClaimsIdentity(claims, userIdClaim is not null ? "TestAuth" : null);
        var claimsPrincipal = new ClaimsPrincipal(identity);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claimsPrincipal }
        };

        return (controller, commitRepo, projectRepo);
    }

    private static (AnalysesController Controller, Mock<IAnalysisRepository> AnalysisRepo, Mock<ICommitRepository> CommitRepo, Mock<IProjectRepository> ProjectRepo) CreateAnalysesController(string? userIdClaim = "1", bool isAdmin = false)
    {
        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var projectRepo = new Mock<IProjectRepository>();
        var sourceService = new Mock<IRepositorySourceService>();

        var analysisService = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, []);
        var projectService = new ProjectService(projectRepo.Object);
        var commitService = new CommitService(commitRepo.Object, projectRepo.Object);

        var controller = new AnalysesController(analysisService, commitService, projectService);

        var claims = new List<Claim>();
        if (userIdClaim is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userIdClaim));
            claims.Add(new Claim(ClaimTypes.Name, "testuser"));
            claims.Add(new Claim(ClaimTypes.Role, isAdmin ? "Admin" : "User"));
        }

        var identity = new ClaimsIdentity(claims, userIdClaim is not null ? "TestAuth" : null);
        var claimsPrincipal = new ClaimsPrincipal(identity);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claimsPrincipal }
        };

        return (controller, analysisRepo, commitRepo, projectRepo);
    }

    private static (AdminUsersController Controller, Mock<IUserRepository> UserRepo) CreateAdminUsersController()
    {
        var userRepo = new Mock<IUserRepository>();
        var roleRepo = new Mock<IRoleRepository>();
        var userService = new UserService(userRepo.Object, roleRepo.Object);
        var controller = new AdminUsersController(userService);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "999"),
            new(ClaimTypes.Name, "admin"),
            new(ClaimTypes.Role, "Admin")
        };

        var identity = new ClaimsIdentity(claims, "TestAuth");
        var claimsPrincipal = new ClaimsPrincipal(identity);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claimsPrincipal }
        };

        return (controller, userRepo);
    }

    // =========================================================================
    // 1. CommitsController tests
    // =========================================================================

    [Fact]
    public async Task Commits_DefaultParameters_ReturnsPage1Size20()
    {
        var (controller, commitRepo, projectRepo) = CreateCommitsController(userIdClaim: "1");
        var project = new Project { Id = 10, Name = "P", RepoUrl = "https://github.com/o/r", OwnerId = 1 };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        var commits = new List<Commit>
        {
            new() { Id = 1, ProjectId = 10, CommitHash = "hash1", CommitDate = DateTime.UtcNow }
        };
        commitRepo.Setup(x => x.GetByProjectIdAsync(10, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((commits, 1));

        var actionResult = await controller.GetByProjectId(10, cancellationToken: CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var pagedResult = Assert.IsType<PagedResultDto<CommitDto>>(okResult.Value);
        Assert.Equal(1, pagedResult.TotalCount);
        Assert.Equal(1, pagedResult.Page);
        Assert.Equal(20, pagedResult.PageSize);
        Assert.Single(pagedResult.Items);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task Commits_InvalidPage_Returns400ValidationFailed(int invalidPage)
    {
        var (controller, _, projectRepo) = CreateCommitsController(userIdClaim: "1");
        var project = new Project { Id = 10, Name = "P", RepoUrl = "url", OwnerId = 1 };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        var actionResult = await controller.GetByProjectId(10, page: invalidPage, pageSize: 20);

        var objectResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(ApiErrorCodes.ValidationFailed, problem.Extensions["code"]?.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-50)]
    public async Task Commits_InvalidPageSize_Returns400ValidationFailed(int invalidPageSize)
    {
        var (controller, _, projectRepo) = CreateCommitsController(userIdClaim: "1");
        var project = new Project { Id = 10, Name = "P", RepoUrl = "url", OwnerId = 1 };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        var actionResult = await controller.GetByProjectId(10, page: 1, pageSize: invalidPageSize);

        var objectResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(ApiErrorCodes.ValidationFailed, problem.Extensions["code"]?.ToString());
    }

    [Fact]
    public async Task Commits_PageSizeGreaterThan100_ClampsTo100()
    {
        var (controller, commitRepo, projectRepo) = CreateCommitsController(userIdClaim: "1");
        var project = new Project { Id = 10, Name = "P", RepoUrl = "url", OwnerId = 1 };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        commitRepo.Setup(x => x.GetByProjectIdAsync(10, 1, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(([], 0));

        var actionResult = await controller.GetByProjectId(10, page: 1, pageSize: 500);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var pagedResult = Assert.IsType<PagedResultDto<CommitDto>>(okResult.Value);
        Assert.Equal(100, pagedResult.PageSize);
        commitRepo.Verify(x => x.GetByProjectIdAsync(10, 1, 100, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Commits_NonOwnerAndNotAdmin_Returns403WithoutQueryingCommits()
    {
        var (controller, commitRepo, projectRepo) = CreateCommitsController(userIdClaim: "2", isAdmin: false);
        var project = new Project { Id = 10, Name = "P", RepoUrl = "url", OwnerId = 1 };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        var actionResult = await controller.GetByProjectId(10, page: 1, pageSize: 20);

        Assert.IsType<ForbidResult>(actionResult.Result);
        commitRepo.Verify(x => x.GetByProjectIdAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // =========================================================================
    // 2. AnalysesController tests
    // =========================================================================

    [Fact]
    public async Task Analyses_DefaultParameters_ReturnsPage1Size20()
    {
        var (controller, analysisRepo, commitRepo, projectRepo) = CreateAnalysesController(userIdClaim: "1");
        var project = new Project { Id = 10, Name = "P", RepoUrl = "url", OwnerId = 1 };
        var commit = new Commit { Id = 20, ProjectId = 10, CommitHash = "h" };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        commitRepo.Setup(x => x.GetByIdAsync(20, It.IsAny<CancellationToken>())).ReturnsAsync(commit);

        var analyses = new List<Analysis>
        {
            new() { Id = 1, CommitId = 20, Status = AnalysisStatus.Completed, CreatedAt = DateTime.UtcNow }
        };
        analysisRepo.Setup(x => x.GetByCommitIdAsync(20, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((analyses, 1));

        var actionResult = await controller.GetByCommitId(10, 20, cancellationToken: CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var pagedResult = Assert.IsType<PagedResultDto<AnalysisDto>>(okResult.Value);
        Assert.Equal(1, pagedResult.TotalCount);
        Assert.Equal(1, pagedResult.Page);
        Assert.Equal(20, pagedResult.PageSize);
        Assert.Single(pagedResult.Items);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Analyses_InvalidPageOrPageSize_Returns400ValidationFailed(int invalidVal)
    {
        var (controller, _, commitRepo, projectRepo) = CreateAnalysesController(userIdClaim: "1");
        var project = new Project { Id = 10, Name = "P", RepoUrl = "url", OwnerId = 1 };
        var commit = new Commit { Id = 20, ProjectId = 10, CommitHash = "h" };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        commitRepo.Setup(x => x.GetByIdAsync(20, It.IsAny<CancellationToken>())).ReturnsAsync(commit);

        var pageResult = await controller.GetByCommitId(10, 20, page: invalidVal, pageSize: 20);
        var sizeResult = await controller.GetByCommitId(10, 20, page: 1, pageSize: invalidVal);

        var pageObj = Assert.IsType<ObjectResult>(pageResult.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, pageObj.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, Assert.IsType<ProblemDetails>(pageObj.Value).Extensions["code"]?.ToString());

        var sizeObj = Assert.IsType<ObjectResult>(sizeResult.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, sizeObj.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, Assert.IsType<ProblemDetails>(sizeObj.Value).Extensions["code"]?.ToString());
    }

    [Fact]
    public async Task Analyses_PageSizeGreaterThan100_ClampsTo100()
    {
        var (controller, analysisRepo, commitRepo, projectRepo) = CreateAnalysesController(userIdClaim: "1");
        var project = new Project { Id = 10, Name = "P", RepoUrl = "url", OwnerId = 1 };
        var commit = new Commit { Id = 20, ProjectId = 10, CommitHash = "h" };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        commitRepo.Setup(x => x.GetByIdAsync(20, It.IsAny<CancellationToken>())).ReturnsAsync(commit);

        analysisRepo.Setup(x => x.GetByCommitIdAsync(20, 1, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(([], 0));

        var actionResult = await controller.GetByCommitId(10, 20, page: 1, pageSize: 250);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var pagedResult = Assert.IsType<PagedResultDto<AnalysisDto>>(okResult.Value);
        Assert.Equal(100, pagedResult.PageSize);
        analysisRepo.Verify(x => x.GetByCommitIdAsync(20, 1, 100, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Analyses_CommitMismatch_Returns404WithoutQueryingAnalyses()
    {
        var (controller, analysisRepo, commitRepo, projectRepo) = CreateAnalysesController(userIdClaim: "1");
        var project = new Project { Id = 10, Name = "P", RepoUrl = "url", OwnerId = 1 };
        var commit = new Commit { Id = 20, ProjectId = 999, CommitHash = "h" }; // принадлежит другому проекту
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        commitRepo.Setup(x => x.GetByIdAsync(20, It.IsAny<CancellationToken>())).ReturnsAsync(commit);

        var actionResult = await controller.GetByCommitId(10, 20, page: 1, pageSize: 20);

        Assert.IsType<NotFoundResult>(actionResult.Result);
        analysisRepo.Verify(x => x.GetByCommitIdAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // =========================================================================
    // 3. AdminUsersController tests
    // =========================================================================

    [Fact]
    public async Task Users_DefaultParameters_ReturnsPage1Size20()
    {
        var (controller, userRepo) = CreateAdminUsersController();
        var users = new List<User>
        {
            new() { Id = 1, Username = "u1", RoleId = 2, Role = new Role { Id = 2, Name = "User" } }
        };
        userRepo.Setup(x => x.GetAllAsync(1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((users, 1));

        var actionResult = await controller.GetAll(cancellationToken: CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var pagedResult = Assert.IsType<PagedResultDto<UserDto>>(okResult.Value);
        Assert.Equal(1, pagedResult.TotalCount);
        Assert.Equal(1, pagedResult.Page);
        Assert.Equal(20, pagedResult.PageSize);
        Assert.Single(pagedResult.Items);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task Users_InvalidPageOrPageSize_Returns400ValidationFailed(int invalidVal)
    {
        var (controller, _) = CreateAdminUsersController();

        var pageResult = await controller.GetAll(page: invalidVal, pageSize: 20);
        var sizeResult = await controller.GetAll(page: 1, pageSize: invalidVal);

        var pageObj = Assert.IsType<ObjectResult>(pageResult.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, pageObj.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, Assert.IsType<ProblemDetails>(pageObj.Value).Extensions["code"]?.ToString());

        var sizeObj = Assert.IsType<ObjectResult>(sizeResult.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, sizeObj.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, Assert.IsType<ProblemDetails>(sizeObj.Value).Extensions["code"]?.ToString());
    }

    [Fact]
    public async Task Users_PageSizeGreaterThan100_ClampsTo100()
    {
        var (controller, userRepo) = CreateAdminUsersController();
        userRepo.Setup(x => x.GetAllAsync(1, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(([], 0));

        var actionResult = await controller.GetAll(page: 1, pageSize: 999);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var pagedResult = Assert.IsType<PagedResultDto<UserDto>>(okResult.Value);
        Assert.Equal(100, pagedResult.PageSize);
        userRepo.Verify(x => x.GetAllAsync(1, 100, It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================================
    // 4. Database-level execution & deterministic sorting tests (SQLite in-memory)
    // =========================================================================

    [Fact]
    public async Task Database_CommitRepository_PaginationAndDeterministicSorting()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using var _ = connection;
        using var context = new AppDbContext(options);

        var user = new User { Id = 10, Username = "dev", PasswordHash = "h", RoleId = 2 };
        var project = new Project { Id = 100, Name = "P", RepoUrl = "https://github.com/o/r", OwnerId = 10 };
        var foreignProject = new Project { Id = 999, Name = "OtherP", RepoUrl = "https://github.com/o/other", OwnerId = 10 };
        context.Users.Add(user);
        context.Projects.AddRange(project, foreignProject);

        var fixedDate = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

        // Добавляем 25 коммитов с абсолютно одинаковой датой для проверки детерминированной вторичной сортировки по Id DESC
        for (var i = 1; i <= 25; i++)
        {
            context.Commits.Add(new Commit
            {
                Id = i,
                ProjectId = 100,
                CommitHash = $"hash_{i:D2}",
                CommitDate = fixedDate
            });
        }

        // Также добавляем коммиты другого проекта для проверки корректности фильтрации
        context.Commits.Add(new Commit
        {
            Id = 999,
            ProjectId = 999,
            CommitHash = "foreign_hash",
            CommitDate = fixedDate
        });

        await context.SaveChangesAsync();

        var repo = new CommitRepository(context);

        // Страница 1 (20 элементов)
        var (page1Items, totalCount1) = await repo.GetByProjectIdAsync(100, page: 1, pageSize: 20);
        Assert.Equal(25, totalCount1);
        Assert.Equal(20, page1Items.Count);

        // Проверяем детерминированную сортировку по Id DESC: 25, 24, 23 ... 6
        Assert.Equal(25, page1Items[0].Id);
        Assert.Equal(6, page1Items[^1].Id);

        // Страница 2 (5 элементов)
        var (page2Items, totalCount2) = await repo.GetByProjectIdAsync(100, page: 2, pageSize: 20);
        Assert.Equal(25, totalCount2);
        Assert.Equal(5, page2Items.Count);
        Assert.Equal(5, page2Items[0].Id);
        Assert.Equal(1, page2Items[^1].Id);

        // Проверяем: нет пересечений между страницами 1 и 2
        var page1Ids = page1Items.Select(c => c.Id).ToHashSet();
        var page2Ids = page2Items.Select(c => c.Id).ToHashSet();
        Assert.Empty(page1Ids.Intersect(page2Ids));

        // Объединение содержит ровно 25 элементов проекта 100
        Assert.Equal(25, page1Ids.Union(page2Ids).Count());

        // Страница за пределами (page = 5, pageSize = 20)
        var (page5Items, totalCount5) = await repo.GetByProjectIdAsync(100, page: 5, pageSize: 20);
        Assert.Equal(25, totalCount5);
        Assert.Empty(page5Items);
    }

    [Fact]
    public async Task Database_AnalysisRepository_PaginationAndDeterministicSorting()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using var _ = connection;
        using var context = new AppDbContext(options);

        var user = new User { Id = 10, Username = "dev", PasswordHash = "h", RoleId = 2 };
        var project = new Project { Id = 100, Name = "P", RepoUrl = "https://github.com/o/r", OwnerId = 10 };
        var commit = new Commit { Id = 200, ProjectId = 100, CommitHash = "h1", CommitDate = DateTime.UtcNow };
        context.Users.Add(user);
        context.Projects.Add(project);
        context.Commits.Add(commit);

        var fixedCreatedAt = new DateTime(2026, 11, 1, 15, 0, 0, DateTimeKind.Utc);

        // 25 анализов с идентичным временем CreatedAt для проверки вторичной сортировки по Id DESC
        for (var i = 1; i <= 25; i++)
        {
            context.Analyses.Add(new Analysis
            {
                Id = i,
                CommitId = 200,
                Status = AnalysisStatus.Completed,
                CreatedAt = fixedCreatedAt,
                Results =
                [
                    new AnalysisResult
                    {
                        FilePath = $"File_{i}.cs",
                        Rule = "SEC001",
                        Message = "Test message",
                        Severity = Severity.Warning,
                        AnalyzerType = AnalyzerType.Security
                    }
                ]
            });
        }

        await context.SaveChangesAsync();

        var repo = new AnalysisRepository(context);

        // Страница 1 (20 элементов)
        var (page1Items, totalCount1) = await repo.GetByCommitIdAsync(200, page: 1, pageSize: 20);
        Assert.Equal(25, totalCount1);
        Assert.Equal(20, page1Items.Count);
        Assert.Equal(25, page1Items[0].Id);
        Assert.Equal(6, page1Items[^1].Id);
        Assert.Single(page1Items[0].Results); // Eager load результатов проверен

        // Страница 2 (5 элементов)
        var (page2Items, totalCount2) = await repo.GetByCommitIdAsync(200, page: 2, pageSize: 20);
        Assert.Equal(25, totalCount2);
        Assert.Equal(5, page2Items.Count);
        Assert.Equal(5, page2Items[0].Id);
        Assert.Equal(1, page2Items[^1].Id);

        // Проверяем отсутствие дубликатов между страницами
        var page1Ids = page1Items.Select(a => a.Id).ToHashSet();
        var page2Ids = page2Items.Select(a => a.Id).ToHashSet();
        Assert.Empty(page1Ids.Intersect(page2Ids));
        Assert.Equal(25, page1Ids.Union(page2Ids).Count());

        // Запрос страницы за пределами
        var (emptyItems, totalEmpty) = await repo.GetByCommitIdAsync(200, page: 10, pageSize: 20);
        Assert.Equal(25, totalEmpty);
        Assert.Empty(emptyItems);
    }

    [Fact]
    public async Task Database_UserRepository_PaginationAndDeterministicSorting()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using var _ = connection;
        using var context = new AppDbContext(options);

        // В БД уже есть 2 роли: Admin (1) и User (2)
        // Добавляем 25 пользователей
        for (var i = 1; i <= 25; i++)
        {
            context.Users.Add(new User
            {
                Id = 100 + i,
                Username = $"user_{i:D2}",
                PasswordHash = "hash",
                RoleId = 2
            });
        }

        await context.SaveChangesAsync();

        var repo = new UserRepository(context);

        // Страница 1 (20 элементов)
        var (page1Items, totalCount1) = await repo.GetAllAsync(page: 1, pageSize: 20);
        Assert.Equal(25, totalCount1);
        Assert.Equal(20, page1Items.Count);
        Assert.Equal(101, page1Items[0].Id);
        Assert.Equal(120, page1Items[^1].Id);
        Assert.NotNull(page1Items[0].Role); // Проверка Include(u => u.Role)

        // Страница 2 (5 элементов)
        var (page2Items, totalCount2) = await repo.GetAllAsync(page: 2, pageSize: 20);
        Assert.Equal(25, totalCount2);
        Assert.Equal(5, page2Items.Count);
        Assert.Equal(121, page2Items[0].Id);
        Assert.Equal(125, page2Items[^1].Id);

        // Проверяем: нет дубликатов
        var page1Ids = page1Items.Select(u => u.Id).ToHashSet();
        var page2Ids = page2Items.Select(u => u.Id).ToHashSet();
        Assert.Empty(page1Ids.Intersect(page2Ids));
        Assert.Equal(25, page1Ids.Union(page2Ids).Count());

        // Страница за пределами
        var (emptyItems, totalEmpty) = await repo.GetAllAsync(page: 3, pageSize: 20);
        Assert.Equal(25, totalEmpty);
        Assert.Empty(emptyItems);
    }

    // =========================================================================
    // 5. Overflow tests with large page values, including int.MaxValue
    // =========================================================================

    [Theory]
    [InlineData(int.MaxValue, 20)]
    [InlineData(int.MaxValue, 100)]
    [InlineData(100_000_000, 100)] // 100M * 100 = 10 млрд > int.MaxValue (2.14 млрд)
    [InlineData(25_000_000, 100)]  // 25M * 100 = 2.5 млрд > int.MaxValue
    public async Task CommitRepository_LargePageValue_DoesNotOverflowAndReturnsEmptyListWithCorrectCount(int largePage, int pageSize)
    {
        var (connection, options) = CreateInMemoryDatabase();
        using var _ = connection;
        using var context = new AppDbContext(options);

        var user = new User { Id = 10, Username = "dev", PasswordHash = "h", RoleId = 2 };
        var project = new Project { Id = 100, Name = "P", RepoUrl = "https://github.com/o/r", OwnerId = 10 };
        context.Users.Add(user);
        context.Projects.Add(project);

        for (var i = 1; i <= 5; i++)
        {
            context.Commits.Add(new Commit
            {
                Id = i,
                ProjectId = 100,
                CommitHash = $"hash_{i}",
                CommitDate = DateTime.UtcNow
            });
        }
        await context.SaveChangesAsync();

        var repo = new CommitRepository(context);

        var (items, totalCount) = await repo.GetByProjectIdAsync(100, page: largePage, pageSize: pageSize);

        Assert.Equal(5, totalCount);
        Assert.Empty(items);
    }

    [Theory]
    [InlineData(int.MaxValue, 20)]
    [InlineData(int.MaxValue, 100)]
    [InlineData(50_000_000, 50)]
    public async Task AnalysisRepository_LargePageValue_DoesNotOverflowAndReturnsEmptyListWithCorrectCount(int largePage, int pageSize)
    {
        var (connection, options) = CreateInMemoryDatabase();
        using var _ = connection;
        using var context = new AppDbContext(options);

        var user = new User { Id = 10, Username = "dev", PasswordHash = "h", RoleId = 2 };
        var project = new Project { Id = 100, Name = "P", RepoUrl = "https://github.com/o/r", OwnerId = 10 };
        var commit = new Commit { Id = 200, ProjectId = 100, CommitHash = "h", CommitDate = DateTime.UtcNow };
        context.Users.Add(user);
        context.Projects.Add(project);
        context.Commits.Add(commit);

        for (var i = 1; i <= 3; i++)
        {
            context.Analyses.Add(new Analysis
            {
                Id = i,
                CommitId = 200,
                Status = AnalysisStatus.Completed,
                CreatedAt = DateTime.UtcNow
            });
        }
        await context.SaveChangesAsync();

        var repo = new AnalysisRepository(context);

        var (items, totalCount) = await repo.GetByCommitIdAsync(200, page: largePage, pageSize: pageSize);

        Assert.Equal(3, totalCount);
        Assert.Empty(items);
    }

    [Theory]
    [InlineData(int.MaxValue, 20)]
    [InlineData(int.MaxValue, 100)]
    [InlineData(30_000_000, 100)]
    public async Task UserRepository_LargePageValue_DoesNotOverflowAndReturnsEmptyListWithCorrectCount(int largePage, int pageSize)
    {
        var (connection, options) = CreateInMemoryDatabase();
        using var _ = connection;
        using var context = new AppDbContext(options);

        for (var i = 1; i <= 4; i++)
        {
            context.Users.Add(new User
            {
                Id = 100 + i,
                Username = $"user_{i}",
                PasswordHash = "h",
                RoleId = 2
            });
        }
        await context.SaveChangesAsync();

        var repo = new UserRepository(context);

        var (items, totalCount) = await repo.GetAllAsync(page: largePage, pageSize: pageSize);

        Assert.Equal(4, totalCount);
        Assert.Empty(items);
    }

    [Fact]
    public async Task CommitsController_IntMaxValuePage_Returns200WithEmptyItemsAndCorrectTotalCount()
    {
        var (controller, commitRepo, projectRepo) = CreateCommitsController(userIdClaim: "1");
        var project = new Project { Id = 10, Name = "P", RepoUrl = "url", OwnerId = 1 };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        commitRepo.Setup(x => x.GetByProjectIdAsync(10, int.MaxValue, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(([], 15));

        var actionResult = await controller.GetByProjectId(10, page: int.MaxValue, pageSize: 20);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var pagedResult = Assert.IsType<PagedResultDto<CommitDto>>(okResult.Value);
        Assert.Equal(15, pagedResult.TotalCount);
        Assert.Equal(int.MaxValue, pagedResult.Page);
        Assert.Equal(20, pagedResult.PageSize);
        Assert.Empty(pagedResult.Items);
    }

    [Fact]
    public async Task AnalysesController_IntMaxValuePage_Returns200WithEmptyItemsAndCorrectTotalCount()
    {
        var (controller, analysisRepo, commitRepo, projectRepo) = CreateAnalysesController(userIdClaim: "1");
        var project = new Project { Id = 10, Name = "P", RepoUrl = "url", OwnerId = 1 };
        var commit = new Commit { Id = 20, ProjectId = 10, CommitHash = "h" };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        commitRepo.Setup(x => x.GetByIdAsync(20, It.IsAny<CancellationToken>())).ReturnsAsync(commit);

        analysisRepo.Setup(x => x.GetByCommitIdAsync(20, int.MaxValue, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(([], 8));

        var actionResult = await controller.GetByCommitId(10, 20, page: int.MaxValue, pageSize: 20);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var pagedResult = Assert.IsType<PagedResultDto<AnalysisDto>>(okResult.Value);
        Assert.Equal(8, pagedResult.TotalCount);
        Assert.Equal(int.MaxValue, pagedResult.Page);
        Assert.Equal(20, pagedResult.PageSize);
        Assert.Empty(pagedResult.Items);
    }

    [Fact]
    public async Task AdminUsersController_IntMaxValuePage_Returns200WithEmptyItemsAndCorrectTotalCount()
    {
        var (controller, userRepo) = CreateAdminUsersController();
        userRepo.Setup(x => x.GetAllAsync(int.MaxValue, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(([], 50));

        var actionResult = await controller.GetAll(page: int.MaxValue, pageSize: 20);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var pagedResult = Assert.IsType<PagedResultDto<UserDto>>(okResult.Value);
        Assert.Equal(50, pagedResult.TotalCount);
        Assert.Equal(int.MaxValue, pagedResult.Page);
        Assert.Equal(20, pagedResult.PageSize);
        Assert.Empty(pagedResult.Items);
    }
}
