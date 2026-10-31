using Api.Common;
using Api.Controllers;
using Application.Analyzers;
using Application.DTOs;
using Application.Exceptions;
using Application.Interfaces;
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

public sealed class AnalysisDeduplicationTests
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

        var role = new Role { Id = 1, Name = "User" };
        var user = new User { Id = 1, Username = "testuser", PasswordHash = "hash", RoleId = 1 };
        var project = new Project { Id = 10, Name = "Project1", RepoUrl = "https://github.com/o/p", OwnerId = 1 };
        var commit = new Commit { Id = 100, ProjectId = 10, CommitHash = "hash_dedup", CommitDate = DateTime.UtcNow };

        context.Roles.Add(role);
        context.Users.Add(user);
        context.Projects.Add(project);
        context.Commits.Add(commit);
        context.SaveChanges();

        return (connection, options);
    }

    private static (SqliteConnection KeepAliveConnection, string ConnectionString) CreateSharedInMemoryDatabase()
    {
        var dbName = "analysis_dedup_test_" + Guid.NewGuid().ToString("N");
        var connectionString = $"DataSource={dbName};Mode=Memory;Cache=Shared";

        var keepAliveConnection = new SqliteConnection(connectionString);
        keepAliveConnection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(keepAliveConnection)
            .Options;

        using var context = new AppDbContext(options);
        context.Database.EnsureCreated();

        var role = new Role { Id = 1, Name = "User" };
        var user = new User { Id = 1, Username = "testuser", PasswordHash = "hash", RoleId = 1 };
        var project = new Project { Id = 10, Name = "Project1", RepoUrl = "https://github.com/o/p", OwnerId = 1 };
        var commit = new Commit { Id = 100, ProjectId = 10, CommitHash = "hash_dedup", CommitDate = DateTime.UtcNow };

        context.Roles.Add(role);
        context.Users.Add(user);
        context.Projects.Add(project);
        context.Commits.Add(commit);
        context.SaveChanges();

        return (keepAliveConnection, connectionString);
    }

    private static AnalysesController CreateController(AppDbContext context, int userId = 1)
    {
        var analysisRepo = new AnalysisRepository(context);
        var commitRepo = new CommitRepository(context);
        var projectRepo = new ProjectRepository(context);
        var sourceService = Mock.Of<IRepositorySourceService>();

        var analysisService = new AnalysisService(analysisRepo, commitRepo, sourceService, []);
        var commitService = new CommitService(commitRepo, projectRepo);
        var projectService = new ProjectService(projectRepo);

        var controller = new AnalysesController(analysisService, commitService, projectService);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, "User")
        };

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };

        return controller;
    }

    [Fact]
    public async Task AnalysesController_Create_WhenStatusIsPending_Returns409ConflictWithAnalysisAlreadyQueued()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using (connection)
        {
            using (var seedContext = new AppDbContext(options))
            {
                seedContext.Analyses.Add(new Analysis
                {
                    Id = 1,
                    CommitId = 100,
                    Status = AnalysisStatus.Pending,
                    CreatedAt = DateTime.UtcNow
                });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var controller = CreateController(context);

            var result = await controller.Create(10, 100, CancellationToken.None);

            var objectResult = Assert.IsType<ObjectResult>(result.Result);
            Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);

            var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
            Assert.Equal(StatusCodes.Status409Conflict, problemDetails.Status);
            Assert.Equal(ApiErrorCodes.AnalysisAlreadyQueued, problemDetails.Extensions["code"]);
        }
    }

    [Fact]
    public async Task AnalysesController_Create_WhenStatusIsRunning_Returns409ConflictWithAnalysisAlreadyQueued()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using (connection)
        {
            using (var seedContext = new AppDbContext(options))
            {
                seedContext.Analyses.Add(new Analysis
                {
                    Id = 1,
                    CommitId = 100,
                    Status = AnalysisStatus.Running,
                    CreatedAt = DateTime.UtcNow,
                    StartedAt = DateTime.UtcNow,
                    ExecutionToken = Guid.NewGuid()
                });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var controller = CreateController(context);

            var result = await controller.Create(10, 100, CancellationToken.None);

            var objectResult = Assert.IsType<ObjectResult>(result.Result);
            Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);

            var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
            Assert.Equal(StatusCodes.Status409Conflict, problemDetails.Status);
            Assert.Equal(ApiErrorCodes.AnalysisAlreadyQueued, problemDetails.Extensions["code"]);
        }
    }

    [Fact]
    public async Task AnalysesController_Create_WhenPreviousStatusIsCompleted_AllowsNewEnqueue()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using (connection)
        {
            using (var seedContext = new AppDbContext(options))
            {
                seedContext.Analyses.Add(new Analysis
                {
                    Id = 1,
                    CommitId = 100,
                    Status = AnalysisStatus.Completed,
                    CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                    CompletedAt = DateTime.UtcNow.AddMinutes(-5)
                });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var controller = CreateController(context);

            var result = await controller.Create(10, 100, CancellationToken.None);

            var acceptedResult = Assert.IsType<AcceptedAtActionResult>(result.Result);
            Assert.Equal(StatusCodes.Status202Accepted, acceptedResult.StatusCode);

            var analysisDto = Assert.IsType<AnalysisDto>(acceptedResult.Value);
            Assert.Equal(AnalysisStatus.Pending, analysisDto.Status);

            var analyses = await context.Analyses.Where(a => a.CommitId == 100).ToListAsync();
            Assert.Equal(2, analyses.Count);
        }
    }

    [Fact]
    public async Task AnalysesController_Create_WhenPreviousStatusIsFailed_AllowsNewEnqueue()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using (connection)
        {
            using (var seedContext = new AppDbContext(options))
            {
                seedContext.Analyses.Add(new Analysis
                {
                    Id = 1,
                    CommitId = 100,
                    Status = AnalysisStatus.Failed,
                    CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                    CompletedAt = DateTime.UtcNow.AddMinutes(-5),
                    ErrorMessage = "Previous failure"
                });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var controller = CreateController(context);

            var result = await controller.Create(10, 100, CancellationToken.None);

            var acceptedResult = Assert.IsType<AcceptedAtActionResult>(result.Result);
            Assert.Equal(StatusCodes.Status202Accepted, acceptedResult.StatusCode);

            var analysisDto = Assert.IsType<AnalysisDto>(acceptedResult.Value);
            Assert.Equal(AnalysisStatus.Pending, analysisDto.Status);

            var analyses = await context.Analyses.Where(a => a.CommitId == 100).ToListAsync();
            Assert.Equal(2, analyses.Count);
        }
    }

    [Fact]
    public async Task Concurrent_TwoEnqueuesForSameCommit_OneSucceedsOtherReturnsConflict()
    {
        var (keepAlive, connectionString) = CreateSharedInMemoryDatabase();
        using (keepAlive)
        {
            var startBarrier = new TaskCompletionSource();

            var task1 = Task.Run(async () =>
            {
                await startBarrier.Task;
                using var conn = new SqliteConnection(connectionString);
                conn.Open();
                var opt = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
                using var context = new AppDbContext(opt);
                var controller = CreateController(context);
                return await controller.Create(10, 100, CancellationToken.None);
            });

            var task2 = Task.Run(async () =>
            {
                await startBarrier.Task;
                using var conn = new SqliteConnection(connectionString);
                conn.Open();
                var opt = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
                using var context = new AppDbContext(opt);
                var controller = CreateController(context);
                return await controller.Create(10, 100, CancellationToken.None);
            });

            startBarrier.SetResult();

            var result1 = await task1;
            var result2 = await task2;

            var results = new[] { result1.Result, result2.Result };

            var acceptedCount = results.OfType<AcceptedAtActionResult>().Count();
            var conflictCount = results.OfType<ObjectResult>().Count(r => r.StatusCode == StatusCodes.Status409Conflict);

            Assert.Equal(1, acceptedCount);
            Assert.Equal(1, conflictCount);

            // В базе ровно 1 задача со статусом Pending
            using var verifyConn = new SqliteConnection(connectionString);
            verifyConn.Open();
            var verifyOpt = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(verifyConn).Options;
            using var verifyContext = new AppDbContext(verifyOpt);
            var activeAnalyses = await verifyContext.Analyses
                .Where(a => a.CommitId == 100 && (a.Status == AnalysisStatus.Pending || a.Status == AnalysisStatus.Running))
                .ToListAsync();
            Assert.Single(activeAnalyses);
        }
    }
}
