using Api.Common;
using Api.Controllers;
using Application.DTOs;
using Application.Interfaces.Repositories;
using Application.Services;
using Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using System.Security.Claims;
using Xunit;

namespace Backend.Tests;

public sealed class CommitsControllerTests
{
    private static (CommitsController Controller, Mock<ICommitRepository> CommitRepo, Mock<IProjectRepository> ProjectRepo) CreateController(string? userIdClaim = "1", bool isAdmin = false)
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
        var principal = new ClaimsPrincipal(identity);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        return (controller, commitRepo, projectRepo);
    }

    [Fact]
    public async Task GetByProjectId_OwnProject_Returns200WithCommitDtos()
    {
        var (controller, commitRepo, projectRepo) = CreateController(userIdClaim: "1", isAdmin: false);

        var project = new Project { Id = 10, Name = "P", RepoUrl = "https://github.com/o/r", OwnerId = 1 };
        var commits = new List<Commit>
        {
            new() { Id = 100, ProjectId = 10, CommitHash = "hash1", CommitDate = DateTime.UtcNow },
            new() { Id = 101, ProjectId = 10, CommitHash = "hash2", CommitDate = DateTime.UtcNow.AddMinutes(-5) }
        };

        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        commitRepo.Setup(x => x.GetByProjectIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(commits);

        var actionResult = await controller.GetByProjectId(10, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dtos = Assert.IsAssignableFrom<IReadOnlyList<CommitDto>>(okResult.Value);
        Assert.Equal(2, dtos.Count);
        Assert.Equal("hash1", dtos[0].CommitHash);
    }

    [Fact]
    public async Task GetByProjectId_MissingUserIdClaim_Returns401Unauthorized()
    {
        var (controller, _, _) = CreateController(userIdClaim: null);

        var actionResult = await controller.GetByProjectId(10, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(actionResult.Result);
    }

    [Fact]
    public async Task GetByProjectId_ProjectNotFound_Returns404NotFound()
    {
        var (controller, _, projectRepo) = CreateController(userIdClaim: "1");

        projectRepo.Setup(x => x.GetByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((Project?)null);

        var actionResult = await controller.GetByProjectId(999, CancellationToken.None);

        Assert.IsType<NotFoundResult>(actionResult.Result);
    }

    [Fact]
    public async Task GetByProjectId_OtherUserProject_Returns403Forbidden()
    {
        var (controller, _, projectRepo) = CreateController(userIdClaim: "2", isAdmin: false);

        var project = new Project { Id = 10, Name = "P", RepoUrl = "https://github.com/o/r", OwnerId = 1 };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        var actionResult = await controller.GetByProjectId(10, CancellationToken.None);

        Assert.IsType<ForbidResult>(actionResult.Result);
    }

    [Fact]
    public async Task GetById_OwnProject_Returns200WithCommitDto()
    {
        var (controller, commitRepo, projectRepo) = CreateController(userIdClaim: "1", isAdmin: false);

        var project = new Project { Id = 10, Name = "P", RepoUrl = "https://github.com/o/r", OwnerId = 1 };
        var commit = new Commit { Id = 100, ProjectId = 10, CommitHash = "hash123", CommitDate = DateTime.UtcNow };

        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        commitRepo.Setup(x => x.GetByIdAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(commit);

        var actionResult = await controller.GetById(10, 100, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<CommitDto>(okResult.Value);
        Assert.Equal(100, dto.Id);
        Assert.Equal("hash123", dto.CommitHash);
    }

    [Fact]
    public async Task GetById_CommitNotFoundOrMismatch_Returns404NotFound()
    {
        var (controller, commitRepo, projectRepo) = CreateController(userIdClaim: "1", isAdmin: false);

        var project = new Project { Id = 10, Name = "P", RepoUrl = "https://github.com/o/r", OwnerId = 1 };
        var commitDifferentProject = new Commit { Id = 100, ProjectId = 999, CommitHash = "hash123", CommitDate = DateTime.UtcNow };

        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        commitRepo.Setup(x => x.GetByIdAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(commitDifferentProject);

        var actionResult = await controller.GetById(10, 100, CancellationToken.None);

        Assert.IsType<NotFoundResult>(actionResult.Result);
    }

    [Fact]
    public async Task Create_ValidCommit_Returns201CreatedWithCommitDto()
    {
        var (controller, commitRepo, projectRepo) = CreateController(userIdClaim: "1", isAdmin: false);

        var project = new Project { Id = 10, Name = "P", RepoUrl = "https://github.com/o/r", OwnerId = 1 };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        commitRepo.Setup(x => x.GetByHashAsync(10, "newhash", It.IsAny<CancellationToken>())).ReturnsAsync((Commit?)null);
        commitRepo.Setup(x => x.AddAsync(It.IsAny<Commit>(), It.IsAny<CancellationToken>()))
            .Callback<Commit, CancellationToken>((c, _) => c.Id = 200)
            .Returns(Task.CompletedTask);

        var dto = new CreateCommitDto { CommitHash = "newhash", CommitDate = DateTime.UtcNow };
        var actionResult = await controller.Create(10, dto, CancellationToken.None);

        var createdResult = Assert.IsType<CreatedAtActionResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status201Created, createdResult.StatusCode);

        var commitDto = Assert.IsType<CommitDto>(createdResult.Value);
        Assert.Equal(200, commitDto.Id);
        Assert.Equal("newhash", commitDto.CommitHash);
    }

    [Fact]
    public async Task Create_DuplicateCommitHash_Returns409ConflictWithCommitAlreadyExistsCode()
    {
        var (controller, commitRepo, projectRepo) = CreateController(userIdClaim: "1", isAdmin: false);

        var project = new Project { Id = 10, Name = "P", RepoUrl = "https://github.com/o/r", OwnerId = 1 };
        var existingCommit = new Commit { Id = 150, ProjectId = 10, CommitHash = "duphash" };

        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        commitRepo.Setup(x => x.GetByHashAsync(10, "duphash", It.IsAny<CancellationToken>())).ReturnsAsync(existingCommit);

        var dto = new CreateCommitDto { CommitHash = "duphash", CommitDate = DateTime.UtcNow };
        var actionResult = await controller.Create(10, dto, CancellationToken.None);

        var conflictResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflictResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(conflictResult.Value);
        Assert.Equal(ApiErrorCodes.CommitAlreadyExists, problem.Extensions["code"]?.ToString());
    }

    [Fact]
    public async Task Create_OtherUserProject_Returns403Forbidden()
    {
        var (controller, _, projectRepo) = CreateController(userIdClaim: "2", isAdmin: false);

        var project = new Project { Id = 10, Name = "P", RepoUrl = "https://github.com/o/r", OwnerId = 1 };
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        var dto = new CreateCommitDto { CommitHash = "anyhash", CommitDate = DateTime.UtcNow };
        var actionResult = await controller.Create(10, dto, CancellationToken.None);

        Assert.IsType<ForbidResult>(actionResult.Result);
    }
}
