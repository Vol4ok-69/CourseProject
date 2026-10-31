using Api.Common;
using Api.Controllers;
using Application.DTOs;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Application.Services;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Repository;
using Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using System.Security.Claims;
using Xunit;

namespace Backend.Tests;

public sealed class DatabaseUniqueConstraintTests
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

    private static (SqliteConnection KeepAliveConnection, string ConnectionString) CreateSharedInMemoryDatabase()
    {
        var dbName = "unique_constraint_test_" + Guid.NewGuid().ToString("N");
        var connectionString = $"DataSource={dbName};Mode=Memory;Cache=Shared";

        var keepAliveConnection = new SqliteConnection(connectionString);
        keepAliveConnection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(keepAliveConnection)
            .Options;

        using var context = new AppDbContext(options);
        context.Database.EnsureCreated();

        var adminRole = new Role { Id = 1, Name = "Admin" };
        var userRole = new Role { Id = 2, Name = "User" };
        context.Roles.AddRange(adminRole, userRole);
        context.SaveChanges();

        return (keepAliveConnection, connectionString);
    }

    [Fact]
    public async Task AuthController_Register_DuplicateUsername_Returns409ConflictWithUsernameAlreadyExists()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using (connection)
        {
            using (var seedContext = new AppDbContext(options))
            {
                seedContext.Users.Add(new User
                {
                    Id = 1,
                    Username = "duplicate_user",
                    PasswordHash = "hash",
                    RoleId = 1
                });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var userRepo = new UserRepository(context);
            var roleRepo = new RoleRepository(context);
            var tokenService = new Mock<ITokenService>();
            var passwordHasher = new PasswordHasher();

            var authService = new AuthService(userRepo, passwordHasher, tokenService.Object);
            var userService = new UserService(userRepo, roleRepo);
            var controller = new AuthController(authService, userService)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };

            var dto = new RegisterDto
            {
                Username = "duplicate_user",
                Password = "Password123!"
            };

            var result = await controller.Register(dto, CancellationToken.None);

            var objectResult = Assert.IsType<ObjectResult>(result.Result);
            Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);

            var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
            Assert.Equal(StatusCodes.Status409Conflict, problemDetails.Status);
            Assert.Equal(ApiErrorCodes.UsernameAlreadyExists, problemDetails.Extensions["code"]);
        }
    }

    [Fact]
    public async Task CommitsController_Create_DuplicateCommitHashInSameProject_Returns409ConflictWithCommitAlreadyExists()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using (connection)
        {
            using (var seedContext = new AppDbContext(options))
            {
                seedContext.Users.Add(new User { Id = 1, Username = "owner", PasswordHash = "hash", RoleId = 1 });
                seedContext.Projects.Add(new Project { Id = 10, Name = "TestProj", RepoUrl = "https://github.com/o/p", OwnerId = 1 });
                seedContext.Commits.Add(new Commit { Id = 100, ProjectId = 10, CommitHash = "hash123", CommitDate = DateTime.UtcNow });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var commitRepo = new CommitRepository(context);
            var projectRepo = new ProjectRepository(context);
            var commitService = new CommitService(commitRepo, projectRepo);
            var projectService = new ProjectService(projectRepo);

            var controller = new CommitsController(commitService, projectService)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                        {
                            new Claim(ClaimTypes.NameIdentifier, "1"),
                            new Claim(ClaimTypes.Role, "User")
                        }, "TestAuth"))
                    }
                }
            };

            var dto = new CreateCommitDto
            {
                CommitHash = "hash123",
                CommitDate = DateTime.UtcNow
            };

            var result = await controller.Create(10, dto, CancellationToken.None);

            var objectResult = Assert.IsType<ObjectResult>(result.Result);
            Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);

            var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
            Assert.Equal(StatusCodes.Status409Conflict, problemDetails.Status);
            Assert.Equal(ApiErrorCodes.CommitAlreadyExists, problemDetails.Extensions["code"]);
        }
    }

    [Fact]
    public async Task Concurrent_RegistrationWithSameUsername_OneSucceedsOtherFailsWithConflict()
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
                var controller = new AuthController(
                    new AuthService(new UserRepository(context), new PasswordHasher(), Mock.Of<ITokenService>()),
                    new UserService(new UserRepository(context), new RoleRepository(context)))
                {
                    ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
                };
                return await controller.Register(new RegisterDto { Username = "concurrent_user", Password = "Pass123!" }, CancellationToken.None);
            });

            var task2 = Task.Run(async () =>
            {
                await startBarrier.Task;
                using var conn = new SqliteConnection(connectionString);
                conn.Open();
                var opt = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
                using var context = new AppDbContext(opt);
                var controller = new AuthController(
                    new AuthService(new UserRepository(context), new PasswordHasher(), Mock.Of<ITokenService>()),
                    new UserService(new UserRepository(context), new RoleRepository(context)))
                {
                    ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
                };
                return await controller.Register(new RegisterDto { Username = "concurrent_user", Password = "Pass123!" }, CancellationToken.None);
            });

            startBarrier.SetResult();

            var result1 = await task1;
            var result2 = await task2;

            var results = new[] { result1.Result, result2.Result };

            var okCount = results.OfType<OkObjectResult>().Count();
            var conflictCount = results.OfType<ObjectResult>().Count(r => r.StatusCode == StatusCodes.Status409Conflict);

            Assert.Equal(1, okCount);
            Assert.Equal(1, conflictCount);

            // В базе данных ровно 1 пользователь с этим логином
            using var verifyConn = new SqliteConnection(connectionString);
            verifyConn.Open();
            var verifyOpt = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(verifyConn).Options;
            using var verifyContext = new AppDbContext(verifyOpt);
            var users = await verifyContext.Users.Where(u => u.Username == "concurrent_user").ToListAsync();
            Assert.Single(users);
        }
    }

    [Fact]
    public async Task AuthController_Register_OtherDbUpdateException_DoesNotConvertTo409()
    {
        var userRepoMock = new Mock<IUserRepository>();
        userRepoMock.Setup(x => x.GetByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        userRepoMock.Setup(x => x.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("Database connection terminated", new TimeoutException()));

        var authService = new AuthService(userRepoMock.Object, new PasswordHasher(), Mock.Of<ITokenService>());
        var userService = new UserService(userRepoMock.Object, Mock.Of<IRoleRepository>());
        var controller = new AuthController(authService, userService)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var dto = new RegisterDto { Username = "user1", Password = "Password123!" };

        // Исключение не должно маскироваться под 409 Conflict и должно пробрасываться дальше
        await Assert.ThrowsAsync<DbUpdateException>(() => controller.Register(dto, CancellationToken.None));
    }

    [Fact]
    public async Task CommitsController_Create_OtherDbUpdateException_DoesNotConvertTo409()
    {
        var commitRepoMock = new Mock<ICommitRepository>();
        var projectRepoMock = new Mock<IProjectRepository>();

        projectRepoMock.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 1, OwnerId = 1, Name = "P1", RepoUrl = "https://github.com/o/r" });
        commitRepoMock.Setup(x => x.GetByHashAsync(1, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Commit?)null);
        commitRepoMock.Setup(x => x.AddAsync(It.IsAny<Commit>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("General disk failure", new IOException()));

        var commitService = new CommitService(commitRepoMock.Object, projectRepoMock.Object);
        var projectService = new ProjectService(projectRepoMock.Object);

        var controller = new CommitsController(commitService, projectService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, "1"),
                        new Claim(ClaimTypes.Role, "User")
                    }, "TestAuth"))
                }
            }
        };

        var dto = new CreateCommitDto { CommitHash = "hash1", CommitDate = DateTime.UtcNow };

        await Assert.ThrowsAsync<DbUpdateException>(() => controller.Create(1, dto, CancellationToken.None));
    }
}
