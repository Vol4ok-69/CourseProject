using Api.Controllers;
using Application.DTOs;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using System.Security.Claims;
using Xunit;

namespace Backend.Tests;

public sealed class AnalysesControllerTests
{
    private static (AnalysesController Controller, Mock<IAnalysisRepository> AnalysisRepo, Mock<ICommitRepository> CommitRepo, Mock<IProjectRepository> ProjectRepo) CreateController(int userId = 1, bool isAdmin = false)
    {
        var analysisRepo = new Mock<IAnalysisRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var projectRepo = new Mock<IProjectRepository>();
        var sourceService = new Mock<IRepositorySourceService>();

        var analysisService = new AnalysisService(analysisRepo.Object, commitRepo.Object, sourceService.Object, []);
        var projectService = new ProjectService(projectRepo.Object);
        var commitService = new CommitService(commitRepo.Object, projectRepo.Object);

        var controller = new AnalysesController(analysisService, commitService, projectService);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "testuser"),
            new(ClaimTypes.Role, isAdmin ? "Admin" : "User")
        };

        var identity = new ClaimsIdentity(claims, "TestAuth");
        var claimsPrincipal = new ClaimsPrincipal(identity);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claimsPrincipal }
        };

        return (controller, analysisRepo, commitRepo, projectRepo);
    }

    [Fact]
    public async Task Create_ValidRequest_Returns202AcceptedWithPendingStatusAndLocation()
    {
        var (controller, analysisRepo, commitRepo, projectRepo) = CreateController(userId: 1, isAdmin: false);

        var project = new Project { Id = 10, Name = "P", RepoUrl = "https://github.com/repo", OwnerId = 1 };
        var commit = new Commit { Id = 20, ProjectId = 10, CommitHash = "c123" };

        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        commitRepo.Setup(x => x.GetByIdAsync(20, It.IsAny<CancellationToken>())).ReturnsAsync(commit);

        analysisRepo.Setup(x => x.AddAsync(It.IsAny<Analysis>(), It.IsAny<CancellationToken>()))
            .Callback<Analysis, CancellationToken>((a, _) => a.Id = 300)
            .Returns(Task.CompletedTask);

        var actionResult = await controller.Create(10, 20, CancellationToken.None);

        var acceptedResult = Assert.IsType<AcceptedAtActionResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status202Accepted, acceptedResult.StatusCode);
        Assert.Equal(nameof(AnalysesController.GetById), acceptedResult.ActionName);

        Assert.Equal(10, acceptedResult.RouteValues!["projectId"]);
        Assert.Equal(20, acceptedResult.RouteValues["commitId"]);
        Assert.Equal(300, acceptedResult.RouteValues["analysisId"]);

        var dto = Assert.IsType<AnalysisDto>(acceptedResult.Value);
        Assert.Equal(300, dto.Id);
        Assert.Equal(20, dto.CommitId);
        Assert.Equal(AnalysisStatus.Pending, dto.Status);
        Assert.Null(dto.StartedAt);
        Assert.Null(dto.CompletedAt);
    }

    [Fact]
    public async Task Create_UserDoesNotOwnProjectAndNotAdmin_ReturnsForbid()
    {
        var (controller, _, _, projectRepo) = CreateController(userId: 2, isAdmin: false);

        var project = new Project { Id = 10, Name = "P", RepoUrl = "https://github.com/repo", OwnerId = 1 }; // владелец - 1, запрашивает - 2
        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        var actionResult = await controller.Create(10, 20, CancellationToken.None);

        Assert.IsType<ForbidResult>(actionResult.Result);
    }

    [Fact]
    public async Task Create_AdminUser_CanCreateForAnyProject()
    {
        var (controller, analysisRepo, commitRepo, projectRepo) = CreateController(userId: 99, isAdmin: true);

        var project = new Project { Id = 10, Name = "P", RepoUrl = "url", OwnerId = 1 };
        var commit = new Commit { Id = 20, ProjectId = 10, CommitHash = "c123" };

        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        commitRepo.Setup(x => x.GetByIdAsync(20, It.IsAny<CancellationToken>())).ReturnsAsync(commit);
        analysisRepo.Setup(x => x.AddAsync(It.IsAny<Analysis>(), It.IsAny<CancellationToken>()))
            .Callback<Analysis, CancellationToken>((a, _) => a.Id = 400)
            .Returns(Task.CompletedTask);

        var actionResult = await controller.Create(10, 20, CancellationToken.None);

        var acceptedResult = Assert.IsType<AcceptedAtActionResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status202Accepted, acceptedResult.StatusCode);
    }

    [Fact]
    public async Task Create_CommitDoesNotBelongToProject_ReturnsNotFound()
    {
        var (controller, _, commitRepo, projectRepo) = CreateController(userId: 1, isAdmin: false);

        var project = new Project { Id = 10, Name = "P", RepoUrl = "url", OwnerId = 1 };
        var foreignCommit = new Commit { Id = 20, ProjectId = 999, CommitHash = "c123" }; // коммит другого проекта

        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        commitRepo.Setup(x => x.GetByIdAsync(20, It.IsAny<CancellationToken>())).ReturnsAsync(foreignCommit);

        var actionResult = await controller.Create(10, 20, CancellationToken.None);

        Assert.IsType<NotFoundResult>(actionResult.Result);
    }

    [Fact]
    public async Task GetById_ReturnsAnalysisWithResults()
    {
        var (controller, analysisRepo, commitRepo, projectRepo) = CreateController(userId: 1, isAdmin: false);

        var project = new Project { Id = 10, Name = "P", RepoUrl = "url", OwnerId = 1 };
        var commit = new Commit { Id = 20, ProjectId = 10, CommitHash = "c123" };

        var analysis = new Analysis
        {
            Id = 50,
            CommitId = 20,
            Status = AnalysisStatus.Completed,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            StartedAt = DateTime.UtcNow.AddMinutes(-4),
            CompletedAt = DateTime.UtcNow.AddMinutes(-3),
            Results =
            [
                new AnalysisResult
                {
                    Id = 1,
                    FilePath = "Foo.cs",
                    LineNumber = 10,
                    Rule = "SEC001",
                    Message = "Violation",
                    Severity = Severity.Critical,
                    AnalyzerType = AnalyzerType.Security
                }
            ]
        };

        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        commitRepo.Setup(x => x.GetByIdAsync(20, It.IsAny<CancellationToken>())).ReturnsAsync(commit);
        analysisRepo.Setup(x => x.GetByIdAsync(50, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);

        var actionResult = await controller.GetById(10, 20, 50, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<AnalysisDto>(okResult.Value);

        Assert.Equal(50, dto.Id);
        Assert.Equal(AnalysisStatus.Completed, dto.Status);
        Assert.Single(dto.Results);
        Assert.Equal("Foo.cs", dto.Results[0].FilePath);
    }

    [Fact]
    public async Task GetByCommitId_ReturnsAnalyses()
    {
        var (controller, analysisRepo, commitRepo, projectRepo) = CreateController(userId: 1, isAdmin: false);

        var project = new Project { Id = 10, Name = "P", RepoUrl = "url", OwnerId = 1 };
        var commit = new Commit { Id = 20, ProjectId = 10, CommitHash = "c123" };

        var analyses = new List<Analysis>
        {
            new() { Id = 2, CommitId = 20, Status = AnalysisStatus.Completed, CreatedAt = DateTime.UtcNow },
            new() { Id = 1, CommitId = 20, Status = AnalysisStatus.Pending, CreatedAt = DateTime.UtcNow.AddMinutes(-10) }
        };

        projectRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        commitRepo.Setup(x => x.GetByIdAsync(20, It.IsAny<CancellationToken>())).ReturnsAsync(commit);
        analysisRepo.Setup(x => x.GetByCommitIdAsync(20, It.IsAny<CancellationToken>())).ReturnsAsync(analyses);

        var actionResult = await controller.GetByCommitId(10, 20, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dtos = Assert.IsAssignableFrom<IReadOnlyList<AnalysisDto>>(okResult.Value);

        Assert.Equal(2, dtos.Count);
        Assert.Equal(2, dtos[0].Id);
        Assert.Equal(1, dtos[1].Id);
    }
}
