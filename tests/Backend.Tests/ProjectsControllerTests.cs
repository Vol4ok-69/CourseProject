using Api.Controllers;
using Application.DTOs;
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
    private static (ProjectsController Controller, Mock<IProjectRepository> ProjectRepo, Mock<IGitHubService> GitHubService) CreateController(string? userIdClaim = "1", bool isAdmin = false)
    {
        var projectRepo = new Mock<IProjectRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var gitHubService = new Mock<IGitHubService>();

        var projectService = new ProjectService(projectRepo.Object);
        var gitHubSyncService = new GitHubSyncService(gitHubService.Object, projectRepo.Object, commitRepo.Object);

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
}
