using Api.Common;
using Api.Controllers;
using Application.DTOs;
using Application.Exceptions;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Application.Services;
using Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using System.Security.Claims;
using Xunit;

namespace Backend.Tests;

public sealed class ProjectsControllerTests
{
    private static (ProjectsController Controller, Mock<IProjectRepository> ProjectRepo, Mock<IGitHubService> GitHubService) CreateController(
        string? userIdClaim = "1",
        bool isAdmin = false,
        Mock<IGitHubCredentialService>? credentialServiceMock = null)
    {
        var projectRepo = new Mock<IProjectRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var gitHubService = new Mock<IGitHubService>();
        var credentialService = credentialServiceMock ?? new Mock<IGitHubCredentialService>();

        if (credentialServiceMock is null)
        {
            credentialService.Setup(c => c.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("ghp_default_test_token");
        }

        var projectService = new ProjectService(projectRepo.Object);
        var gitHubSyncService = new GitHubSyncService(gitHubService.Object, projectRepo.Object, commitRepo.Object, credentialService.Object);

        var controller = new ProjectsController(projectService, gitHubSyncService);

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

        return (controller, projectRepo, gitHubService);
    }

    [Fact]
    public async Task GetAll_UserRole_ReturnsOnlyOwnProjects()
    {
        var (controller, projectRepo, _) = CreateController(userIdClaim: "1", isAdmin: false);

        var userProjects = new List<Project>
        {
            new() { Id = 10, Name = "UserProject1", RepoUrl = "https://github.com/u/p1", OwnerId = 1 },
            new() { Id = 11, Name = "UserProject2", RepoUrl = "https://github.com/u/p2", OwnerId = 1 }
        };

        projectRepo.Setup(x => x.GetByOwnerIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(userProjects);

        var actionResult = await controller.GetAll(all: false, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dtos = Assert.IsAssignableFrom<IReadOnlyList<ProjectDto>>(okResult.Value);

        Assert.Equal(2, dtos.Count);
        Assert.All(dtos, p => Assert.Equal(1, p.OwnerId));
        projectRepo.Verify(x => x.GetByOwnerIdAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        projectRepo.Verify(x => x.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAll_UserRole_WithAllTrue_IgnoresAllAndReturnsOnlyOwnProjects()
    {
        var (controller, projectRepo, _) = CreateController(userIdClaim: "1", isAdmin: false);

        var userProjects = new List<Project>
        {
            new() { Id = 10, Name = "UserProject1", RepoUrl = "https://github.com/u/p1", OwnerId = 1 }
        };

        projectRepo.Setup(x => x.GetByOwnerIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(userProjects);

        var actionResult = await controller.GetAll(all: true, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dtos = Assert.IsAssignableFrom<IReadOnlyList<ProjectDto>>(okResult.Value);

        Assert.Single(dtos);
        Assert.Equal(1, dtos[0].OwnerId);
        projectRepo.Verify(x => x.GetByOwnerIdAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        projectRepo.Verify(x => x.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAll_AdminRole_WithoutAll_ReturnsOnlyOwnProjects()
    {
        var (controller, projectRepo, _) = CreateController(userIdClaim: "99", isAdmin: true);

        var adminProjects = new List<Project>
        {
            new() { Id = 50, Name = "AdminProject", RepoUrl = "https://github.com/a/p", OwnerId = 99 }
        };

        projectRepo.Setup(x => x.GetByOwnerIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync(adminProjects);

        var actionResult = await controller.GetAll(all: false, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dtos = Assert.IsAssignableFrom<IReadOnlyList<ProjectDto>>(okResult.Value);

        Assert.Single(dtos);
        Assert.Equal(99, dtos[0].OwnerId);
        projectRepo.Verify(x => x.GetByOwnerIdAsync(99, It.IsAny<CancellationToken>()), Times.Once);
        projectRepo.Verify(x => x.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAll_AdminRole_WithAllFalse_ReturnsOnlyOwnProjects()
    {
        var (controller, projectRepo, _) = CreateController(userIdClaim: "99", isAdmin: true);

        var adminProjects = new List<Project>
        {
            new() { Id = 50, Name = "AdminProject", RepoUrl = "https://github.com/a/p", OwnerId = 99 }
        };

        projectRepo.Setup(x => x.GetByOwnerIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync(adminProjects);

        var actionResult = await controller.GetAll(all: false, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dtos = Assert.IsAssignableFrom<IReadOnlyList<ProjectDto>>(okResult.Value);

        Assert.Single(dtos);
        Assert.Equal(99, dtos[0].OwnerId);
        projectRepo.Verify(x => x.GetByOwnerIdAsync(99, It.IsAny<CancellationToken>()), Times.Once);
        projectRepo.Verify(x => x.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAll_AdminRole_WithAllTrue_ReturnsAllUsersProjects()
    {
        var (controller, projectRepo, _) = CreateController(userIdClaim: "99", isAdmin: true);

        var allProjects = new List<Project>
        {
            new() { Id = 1, Name = "User1Project", RepoUrl = "https://github.com/u1/p", OwnerId = 1 },
            new() { Id = 2, Name = "User2Project", RepoUrl = "https://github.com/u2/p", OwnerId = 2 },
            new() { Id = 50, Name = "AdminProject", RepoUrl = "https://github.com/a/p", OwnerId = 99 }
        };

        projectRepo.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(allProjects);

        var actionResult = await controller.GetAll(all: true, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dtos = Assert.IsAssignableFrom<IReadOnlyList<ProjectDto>>(okResult.Value);

        Assert.Equal(3, dtos.Count);
        projectRepo.Verify(x => x.GetAllAsync(It.IsAny<CancellationToken>()), Times.Once);
        projectRepo.Verify(x => x.GetByOwnerIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAll_MissingUserIdClaim_Returns401Unauthorized()
    {
        var (controller, projectRepo, _) = CreateController(userIdClaim: null);

        var actionResult = await controller.GetAll(all: false, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(actionResult.Result);
        projectRepo.Verify(x => x.GetByOwnerIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        projectRepo.Verify(x => x.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAll_InvalidUserIdClaimFormat_Returns401Unauthorized()
    {
        var (controller, projectRepo, _) = CreateController(userIdClaim: "invalid_id_format");

        var actionResult = await controller.GetAll(all: false, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(actionResult.Result);
        projectRepo.Verify(x => x.GetByOwnerIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        projectRepo.Verify(x => x.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetById_OwnProject_Returns200WithProjectDto()
    {
        var (controller, projectRepo, _) = CreateController(userIdClaim: "1", isAdmin: false);

        var project = new Project { Id = 10, Name = "MyProject", RepoUrl = "https://github.com/u/p", OwnerId = 1 };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        var actionResult = await controller.GetById(10, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<ProjectDto>(okResult.Value);
        Assert.Equal(10, dto.Id);
        Assert.Equal("MyProject", dto.Name);
    }

    [Fact]
    public async Task GetById_AdminViewingOtherUserProject_Returns200WithProjectDto()
    {
        var (controller, projectRepo, _) = CreateController(userIdClaim: "99", isAdmin: true);

        var project = new Project { Id = 10, Name = "UserProject", RepoUrl = "https://github.com/u/p", OwnerId = 1 };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        var actionResult = await controller.GetById(10, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<ProjectDto>(okResult.Value);
        Assert.Equal(10, dto.Id);
        Assert.Equal(1, dto.OwnerId);
    }

    [Fact]
    public async Task GetById_OtherUserProject_Returns403Forbidden()
    {
        var (controller, projectRepo, _) = CreateController(userIdClaim: "2", isAdmin: false);

        var project = new Project { Id = 10, Name = "User1Project", RepoUrl = "https://github.com/u/p", OwnerId = 1 };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        var actionResult = await controller.GetById(10, CancellationToken.None);

        Assert.IsType<ForbidResult>(actionResult.Result);
    }

    [Fact]
    public async Task GetById_NotFound_Returns404NotFound()
    {
        var (controller, projectRepo, _) = CreateController(userIdClaim: "1");

        projectRepo.Setup(x => x.GetByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((Project?)null);

        var actionResult = await controller.GetById(999, CancellationToken.None);

        Assert.IsType<NotFoundResult>(actionResult.Result);
    }

    [Fact]
    public async Task GetByOwnerId_OwnProjects_Returns200WithList()
    {
        var (controller, projectRepo, _) = CreateController(userIdClaim: "1", isAdmin: false);

        var projects = new List<Project>
        {
            new() { Id = 1, Name = "P1", RepoUrl = "https://github.com/u/p1", OwnerId = 1 }
        };
        projectRepo.Setup(x => x.GetByOwnerIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(projects);

        var actionResult = await controller.GetByOwnerId(1, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dtos = Assert.IsAssignableFrom<IReadOnlyList<ProjectDto>>(okResult.Value);
        Assert.Single(dtos);
    }

    [Fact]
    public async Task GetByOwnerId_OtherUser_Returns403Forbidden()
    {
        var (controller, _, _) = CreateController(userIdClaim: "2", isAdmin: false);

        var actionResult = await controller.GetByOwnerId(1, CancellationToken.None);

        Assert.IsType<ForbidResult>(actionResult.Result);
    }

    [Fact]
    public async Task Create_ValidDto_Returns201CreatedWithProjectDto()
    {
        var (controller, projectRepo, _) = CreateController(userIdClaim: "1");

        projectRepo.Setup(x => x.GetByOwnerIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new List<Project>());
        projectRepo.Setup(x => x.AddAsync(It.IsAny<Project>(), It.IsAny<CancellationToken>()))
            .Callback<Project, CancellationToken>((p, _) => p.Id = 25)
            .Returns(Task.CompletedTask);

        var dto = new CreateProjectDto { Name = "NewProject", RepoUrl = "https://github.com/owner/repo" };
        var actionResult = await controller.Create(dto, CancellationToken.None);

        var createdResult = Assert.IsType<CreatedAtActionResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status201Created, createdResult.StatusCode);

        var projectDto = Assert.IsType<ProjectDto>(createdResult.Value);
        Assert.Equal(25, projectDto.Id);
        Assert.Equal("NewProject", projectDto.Name);
    }

    [Fact]
    public async Task Create_DuplicateName_Returns409ConflictWithProjectNameDuplicateCode()
    {
        var (controller, projectRepo, _) = CreateController(userIdClaim: "1");

        var existing = new Project { Id = 10, Name = "DupProject", OwnerId = 1, RepoUrl = "https://github.com/o/r" };
        projectRepo.Setup(x => x.GetByOwnerIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new List<Project> { existing });

        var dto = new CreateProjectDto { Name = "DupProject", RepoUrl = "https://github.com/owner/repo" };
        var actionResult = await controller.Create(dto, CancellationToken.None);

        var conflictResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflictResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(conflictResult.Value);
        Assert.Equal(ApiErrorCodes.ProjectNameDuplicate, problem.Extensions["code"]?.ToString());
    }

    [Fact]
    public async Task Update_OwnProject_Returns200WithUpdatedProjectDto()
    {
        var (controller, projectRepo, _) = CreateController(userIdClaim: "1", isAdmin: false);

        var project = new Project { Id = 10, Name = "OldName", OwnerId = 1, RepoUrl = "https://github.com/o/r" };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        projectRepo.Setup(x => x.GetByOwnerIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new List<Project> { project });
        projectRepo.Setup(x => x.UpdateAsync(It.IsAny<Project>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var dto = new UpdateProjectDto { Name = "UpdatedName", RepoUrl = "https://github.com/o/r" };
        var actionResult = await controller.Update(10, dto, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var projectDto = Assert.IsType<ProjectDto>(okResult.Value);
        Assert.Equal("UpdatedName", projectDto.Name);
    }

    [Fact]
    public async Task Update_OtherUserProject_Returns403Forbidden()
    {
        var (controller, projectRepo, _) = CreateController(userIdClaim: "2", isAdmin: false);

        var project = new Project { Id = 10, Name = "User1Project", OwnerId = 1, RepoUrl = "https://github.com/o/r" };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        var dto = new UpdateProjectDto { Name = "UpdatedName", RepoUrl = "https://github.com/o/r" };
        var actionResult = await controller.Update(10, dto, CancellationToken.None);

        Assert.IsType<ForbidResult>(actionResult.Result);
    }

    [Fact]
    public async Task Delete_OwnProject_Returns204NoContent()
    {
        var (controller, projectRepo, _) = CreateController(userIdClaim: "1", isAdmin: false);

        var project = new Project { Id = 10, Name = "Proj", OwnerId = 1, RepoUrl = "https://github.com/o/r" };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        projectRepo.Setup(x => x.DeleteAsync(It.IsAny<Project>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var actionResult = await controller.Delete(10, CancellationToken.None);

        Assert.IsType<NoContentResult>(actionResult);
    }

    [Fact]
    public async Task Delete_OtherUserProject_Returns403Forbidden()
    {
        var (controller, projectRepo, _) = CreateController(userIdClaim: "2", isAdmin: false);

        var project = new Project { Id = 10, Name = "Proj", OwnerId = 1, RepoUrl = "https://github.com/o/r" };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        var actionResult = await controller.Delete(10, CancellationToken.None);

        Assert.IsType<ForbidResult>(actionResult);
    }

    [Fact]
    public async Task SyncGitHub_Success_Returns200WithResult()
    {
        var (controller, projectRepo, gitHubService) = CreateController(userIdClaim: "1", isAdmin: false);

        var project = new Project { Id = 10, Name = "Proj", OwnerId = 1, RepoUrl = "https://github.com/owner/repo" };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        gitHubService.Setup(x => x.GetCommitsAsync("https://github.com/owner/repo", "ghp_default_test_token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GitHubCommitInfo>());

        var actionResult = await controller.SyncGitHub(10, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var result = Assert.IsType<GitHubSyncResultDto>(okResult.Value);
        Assert.Equal(0, result.Added);
    }

    [Fact]
    public async Task SyncGitHub_RateLimited_Returns502WithGitHubRateLimitedCode()
    {
        var (controller, projectRepo, gitHubService) = CreateController(userIdClaim: "1", isAdmin: false);

        var project = new Project { Id = 10, Name = "Proj", OwnerId = 1, RepoUrl = "https://github.com/owner/repo" };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        gitHubService.Setup(x => x.GetCommitsAsync("https://github.com/owner/repo", "ghp_default_test_token", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.RateLimited, "Превышен лимит запросов."));

        var actionResult = await controller.SyncGitHub(10, CancellationToken.None);

        var errorResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, errorResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(errorResult.Value);
        Assert.Equal(ApiErrorCodes.GitHubRateLimited, problem.Extensions["code"]?.ToString());
    }

    [Fact]
    public async Task SyncGitHub_RepoNotFound_Returns404WithGitHubRepoNotFoundCode()
    {
        var (controller, projectRepo, gitHubService) = CreateController(userIdClaim: "1", isAdmin: false);

        var project = new Project { Id = 10, Name = "Proj", OwnerId = 1, RepoUrl = "https://github.com/owner/repo" };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        gitHubService.Setup(x => x.GetCommitsAsync("https://github.com/owner/repo", "ghp_default_test_token", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.RepoNotFound, "Репозиторий не найден."));

        var actionResult = await controller.SyncGitHub(10, CancellationToken.None);

        var errorResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status404NotFound, errorResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(errorResult.Value);
        Assert.Equal(ApiErrorCodes.GitHubRepoNotFound, problem.Extensions["code"]?.ToString());
    }

    [Fact]
    public async Task SyncGitHub_TokenRequired_Returns400BadRequestWithGitHubTokenRequiredCode()
    {
        var credentialMock = new Mock<IGitHubCredentialService>();
        credentialMock.Setup(c => c.GetUserTokenAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var (controller, projectRepo, gitHubService) = CreateController(
            userIdClaim: "1",
            isAdmin: false,
            credentialServiceMock: credentialMock);

        var project = new Project { Id = 10, Name = "Proj", OwnerId = 1, RepoUrl = "https://github.com/owner/repo" };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        var actionResult = await controller.SyncGitHub(10, CancellationToken.None);

        var errorResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, errorResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(errorResult.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal(ApiErrorCodes.GitHubTokenRequired, problem.Extensions["code"]?.ToString());

        gitHubService.Verify(g => g.GetCommitsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncGitHub_TokenExpired_Returns400BadRequestWithGitHubTokenExpiredCode()
    {
        var (controller, projectRepo, gitHubService) = CreateController(userIdClaim: "1", isAdmin: false);

        var project = new Project { Id = 10, Name = "Proj", OwnerId = 1, RepoUrl = "https://github.com/owner/repo" };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        gitHubService.Setup(x => x.GetCommitsAsync("https://github.com/owner/repo", "ghp_default_test_token", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.TokenExpired, "Токен недействителен или отозван."));

        var actionResult = await controller.SyncGitHub(10, CancellationToken.None);

        var errorResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, errorResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(errorResult.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal(ApiErrorCodes.GitHubTokenExpired, problem.Extensions["code"]?.ToString());
    }

    [Fact]
    public async Task SyncGitHub_AdminSyncsOtherUserProject_UsesProjectOwnerPat()
    {
        var credentialMock = new Mock<IGitHubCredentialService>();
        const int ownerId = 2;
        const int adminId = 99;
        const string ownerPat = "ghp_owner_pat_222";
        const string adminPat = "ghp_admin_pat_999";

        credentialMock.Setup(c => c.GetUserTokenAsync(ownerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ownerPat);
        credentialMock.Setup(c => c.GetUserTokenAsync(adminId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(adminPat);

        var (controller, projectRepo, gitHubService) = CreateController(
            userIdClaim: adminId.ToString(),
            isAdmin: true,
            credentialServiceMock: credentialMock);

        var project = new Project { Id = 10, Name = "User2Proj", OwnerId = ownerId, RepoUrl = "https://github.com/owner/repo" };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        gitHubService.Setup(x => x.GetCommitsAsync("https://github.com/owner/repo", ownerPat, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GitHubCommitInfo>());

        var actionResult = await controller.SyncGitHub(10, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status200OK, okResult.StatusCode);

        credentialMock.Verify(c => c.GetUserTokenAsync(ownerId, It.IsAny<CancellationToken>()), Times.Once);
        credentialMock.Verify(c => c.GetUserTokenAsync(adminId, It.IsAny<CancellationToken>()), Times.Never);
        gitHubService.Verify(g => g.GetCommitsAsync("https://github.com/owner/repo", ownerPat, It.IsAny<CancellationToken>()), Times.Once);
        gitHubService.Verify(g => g.GetCommitsAsync(It.IsAny<string>(), adminPat, It.IsAny<CancellationToken>()), Times.Never);
    }
}
