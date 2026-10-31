using Api.Common;
using Api.Controllers;
using Application.DTOs;
using Application.Exceptions;
using Application.Interfaces.Repositories;
using Application.Services;
using Domain.Entities;
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

public sealed class AdminProtectionTests
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
        var dbName = "admin_protection_test_" + Guid.NewGuid().ToString("N");
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

    private static ControllerContext CreateControllerContext(int currentUserId, string role = "Admin")
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, currentUserId.ToString()),
            new Claim(ClaimTypes.Role, role)
        };

        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };
    }

    [Fact]
    public async Task UserService_DeleteAsync_WhenLastAdmin_ThrowsLastAdminProtectionException()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using (connection)
        {
            using (var seedContext = new AppDbContext(options))
            {
                seedContext.Users.Add(new User { Id = 1, Username = "admin1", PasswordHash = "hash", RoleId = 1 });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var userRepo = new UserRepository(context);
            var roleRepo = new RoleRepository(context);
            var service = new UserService(userRepo, roleRepo);

            var ex = await Assert.ThrowsAsync<LastAdminProtectionException>(() => service.DeleteAsync(1));
            Assert.Equal("Невозможно удалить или понизить роль последнего администратора системы.", ex.Message);

            // Проверяем сохранность администратора в базе
            var userInDb = await context.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == 1);
            Assert.NotNull(userInDb);
            Assert.Equal("Admin", userInDb.Role.Name);
        }
    }

    [Fact]
    public async Task UserService_UpdateRoleAsync_WhenLastAdminDemotedToUser_ThrowsLastAdminProtectionException()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using (connection)
        {
            using (var seedContext = new AppDbContext(options))
            {
                seedContext.Users.Add(new User { Id = 1, Username = "admin1", PasswordHash = "hash", RoleId = 1 });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var userRepo = new UserRepository(context);
            var roleRepo = new RoleRepository(context);
            var service = new UserService(userRepo, roleRepo);

            var ex = await Assert.ThrowsAsync<LastAdminProtectionException>(() =>
                service.UpdateRoleAsync(1, new UpdateUserRoleDto { RoleId = 2 }));
            Assert.Equal("Невозможно удалить или понизить роль последнего администратора системы.", ex.Message);

            // Проверяем, что роль осталась Admin
            var userInDb = await context.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == 1);
            Assert.NotNull(userInDb);
            Assert.Equal("Admin", userInDb.Role.Name);
        }
    }

    [Fact]
    public async Task UserService_DeleteAsync_WhenMultipleAdmins_SuccessfullyDeletesAdmin()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using (connection)
        {
            using (var seedContext = new AppDbContext(options))
            {
                seedContext.Users.AddRange(
                    new User { Id = 1, Username = "admin1", PasswordHash = "hash", RoleId = 1 },
                    new User { Id = 2, Username = "admin2", PasswordHash = "hash", RoleId = 1 }
                );
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var userRepo = new UserRepository(context);
            var roleRepo = new RoleRepository(context);
            var service = new UserService(userRepo, roleRepo);

            var result = await service.DeleteAsync(2);
            Assert.True(result);

            // Проверяем, что admin2 удален, а admin1 остался
            var remaining = await context.Users.Include(u => u.Role).ToListAsync();
            Assert.Single(remaining);
            Assert.Equal(1, remaining[0].Id);
            Assert.Equal("admin1", remaining[0].Username);
            Assert.Equal("Admin", remaining[0].Role.Name);
        }
    }

    [Fact]
    public async Task UserService_UpdateRoleAsync_WhenMultipleAdmins_SuccessfullyDemotesAdmin()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using (connection)
        {
            using (var seedContext = new AppDbContext(options))
            {
                seedContext.Users.AddRange(
                    new User { Id = 1, Username = "admin1", PasswordHash = "hash", RoleId = 1 },
                    new User { Id = 2, Username = "admin2", PasswordHash = "hash", RoleId = 1 }
                );
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var userRepo = new UserRepository(context);
            var roleRepo = new RoleRepository(context);
            var service = new UserService(userRepo, roleRepo);

            var updatedUser = await service.UpdateRoleAsync(2, new UpdateUserRoleDto { RoleId = 2 });
            Assert.NotNull(updatedUser);
            Assert.Equal(2, updatedUser.RoleId);

            // Проверяем, что в базе admin2 стал User, а admin1 остался Admin
            var admin1 = await context.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == 1);
            var user2 = await context.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == 2);

            Assert.NotNull(admin1);
            Assert.Equal("Admin", admin1.Role.Name);
            Assert.NotNull(user2);
            Assert.Equal("User", user2.Role.Name);
        }
    }

    [Fact]
    public async Task UserService_DeleteAsync_WhenRegularUser_SuccessfullyDeletesUser()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using (connection)
        {
            using (var seedContext = new AppDbContext(options))
            {
                seedContext.Users.AddRange(
                    new User { Id = 1, Username = "admin1", PasswordHash = "hash", RoleId = 1 },
                    new User { Id = 2, Username = "regularuser", PasswordHash = "hash", RoleId = 2 }
                );
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var userRepo = new UserRepository(context);
            var roleRepo = new RoleRepository(context);
            var service = new UserService(userRepo, roleRepo);

            var result = await service.DeleteAsync(2);
            Assert.True(result);

            var remaining = await context.Users.ToListAsync();
            Assert.Single(remaining);
            Assert.Equal(1, remaining[0].Id);
        }
    }

    [Fact]
    public async Task UserService_UpdateRoleAsync_WhenPromotingUserToAdmin_SuccessfullyPromotes()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using (connection)
        {
            using (var seedContext = new AppDbContext(options))
            {
                seedContext.Users.AddRange(
                    new User { Id = 1, Username = "admin1", PasswordHash = "hash", RoleId = 1 },
                    new User { Id = 2, Username = "user2", PasswordHash = "hash", RoleId = 2 }
                );
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var userRepo = new UserRepository(context);
            var roleRepo = new RoleRepository(context);
            var service = new UserService(userRepo, roleRepo);

            var updated = await service.UpdateRoleAsync(2, new UpdateUserRoleDto { RoleId = 1 });
            Assert.NotNull(updated);
            Assert.Equal(1, updated.RoleId);

            var adminCount = await userRepo.CountAdminsAsync();
            Assert.Equal(2, adminCount);
        }
    }

    [Fact]
    public async Task AdminUsersController_Delete_WhenLastAdmin_Returns409ConflictWithLastAdminProtectedProblemDetails()
    {
        var userRepo = new Mock<IUserRepository>();
        var roleRepo = new Mock<IRoleRepository>();

        var adminUser = new User { Id = 2, Username = "admin2", Role = new Role { Id = 1, Name = "Admin" } };
        userRepo.Setup(x => x.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(adminUser);
        userRepo.Setup(x => x.BeginAdminProtectionTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUserRepositoryTransaction>());
        userRepo.Setup(x => x.CountAdminsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var service = new UserService(userRepo.Object, roleRepo.Object);
        var controller = new AdminUsersController(service)
        {
            ControllerContext = CreateControllerContext(currentUserId: 1) // current admin is 1, deleting 2
        };

        var result = await controller.Delete(2, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status409Conflict, problemDetails.Status);
        Assert.Equal(ApiErrorCodes.LastAdminProtected, problemDetails.Extensions["code"]);
        Assert.Equal("Невозможно удалить или понизить роль последнего администратора системы.", problemDetails.Detail);
        Assert.Equal("Конфликт данных", problemDetails.Title);
    }

    [Fact]
    public async Task AdminUsersController_UpdateRole_WhenLastAdminDemoted_Returns409ConflictWithLastAdminProtectedProblemDetails()
    {
        var userRepo = new Mock<IUserRepository>();
        var roleRepo = new Mock<IRoleRepository>();

        var adminUser = new User { Id = 2, Username = "admin2", RoleId = 1, Role = new Role { Id = 1, Name = "Admin" } };
        var userRole = new Role { Id = 2, Name = "User" };

        userRepo.Setup(x => x.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(adminUser);
        roleRepo.Setup(x => x.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(userRole);
        userRepo.Setup(x => x.BeginAdminProtectionTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUserRepositoryTransaction>());
        userRepo.Setup(x => x.CountAdminsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var service = new UserService(userRepo.Object, roleRepo.Object);
        var controller = new AdminUsersController(service)
        {
            ControllerContext = CreateControllerContext(currentUserId: 1) // current admin is 1, updating 2
        };

        var result = await controller.UpdateRole(2, new UpdateUserRoleDto { RoleId = 2 }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status409Conflict, problemDetails.Status);
        Assert.Equal(ApiErrorCodes.LastAdminProtected, problemDetails.Extensions["code"]);
        Assert.Equal("Невозможно удалить или понизить роль последнего администратора системы.", problemDetails.Detail);
    }

    [Fact]
    public async Task AdminUsersController_SelfDelete_Returns403Forbidden()
    {
        var service = new UserService(Mock.Of<IUserRepository>(), Mock.Of<IRoleRepository>());
        var controller = new AdminUsersController(service)
        {
            ControllerContext = CreateControllerContext(currentUserId: 1)
        };

        var result = await controller.Delete(1, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task AdminUsersController_SelfUpdateRole_Returns403Forbidden()
    {
        var service = new UserService(Mock.Of<IUserRepository>(), Mock.Of<IRoleRepository>());
        var controller = new AdminUsersController(service)
        {
            ControllerContext = CreateControllerContext(currentUserId: 1)
        };

        var result = await controller.UpdateRole(1, new UpdateUserRoleDto { RoleId = 2 }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Concurrent_TwoAdmins_ConcurrentDeletions_PreservesAtLeastOneAdmin()
    {
        var (keepAlive, connectionString) = CreateSharedInMemoryDatabase();
        using (keepAlive)
        {
            using (var seedConn = new SqliteConnection(connectionString))
            {
                seedConn.Open();
                var seedOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(seedConn).Options;
                using var seedContext = new AppDbContext(seedOptions);
                seedContext.Users.AddRange(
                    new User { Id = 1, Username = "admin1", PasswordHash = "hash", RoleId = 1 },
                    new User { Id = 2, Username = "admin2", PasswordHash = "hash", RoleId = 1 }
                );
                seedContext.SaveChanges();
            }

            var startBarrier = new TaskCompletionSource();

            var task1 = Task.Run(async () =>
            {
                await startBarrier.Task;
                using var conn = new SqliteConnection(connectionString);
                conn.Open();
                var opt = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
                using var context = new AppDbContext(opt);
                var service = new UserService(new UserRepository(context), new RoleRepository(context));
                return await service.DeleteAsync(1);
            });

            var task2 = Task.Run(async () =>
            {
                await startBarrier.Task;
                using var conn = new SqliteConnection(connectionString);
                conn.Open();
                var opt = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
                using var context = new AppDbContext(opt);
                var service = new UserService(new UserRepository(context), new RoleRepository(context));
                return await service.DeleteAsync(2);
            });

            startBarrier.SetResult();

            var results = await WhenSettled(task1, task2);

            // Ровно одна операция должна завершиться успешно, а вторая — выбросить LastAdminProtectionException
            var successCount = 0;
            var exceptionCount = 0;

            foreach (var result in results)
            {
                if (result.IsSuccess && result.Value)
                {
                    successCount++;
                }
                else if (result.Exception is LastAdminProtectionException)
                {
                    exceptionCount++;
                }
            }

            Assert.Equal(1, successCount);
            Assert.Equal(1, exceptionCount);

            // Проверяем состояние БД: ровно один администратор гарантированно остался активным
            using var verifyConn = new SqliteConnection(connectionString);
            verifyConn.Open();
            var verifyOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(verifyConn).Options;
            using var verifyContext = new AppDbContext(verifyOptions);
            var remainingAdmins = await verifyContext.Users.Include(u => u.Role).Where(u => u.Role.Name == "Admin").ToListAsync();
            Assert.Single(remainingAdmins);
        }
    }

    [Fact]
    public async Task Concurrent_TwoAdmins_ConcurrentDemotions_PreservesAtLeastOneAdmin()
    {
        var (keepAlive, connectionString) = CreateSharedInMemoryDatabase();
        using (keepAlive)
        {
            using (var seedConn = new SqliteConnection(connectionString))
            {
                seedConn.Open();
                var seedOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(seedConn).Options;
                using var seedContext = new AppDbContext(seedOptions);
                seedContext.Users.AddRange(
                    new User { Id = 1, Username = "admin1", PasswordHash = "hash", RoleId = 1 },
                    new User { Id = 2, Username = "admin2", PasswordHash = "hash", RoleId = 1 }
                );
                seedContext.SaveChanges();
            }

            var startBarrier = new TaskCompletionSource();

            var task1 = Task.Run(async () =>
            {
                await startBarrier.Task;
                using var conn = new SqliteConnection(connectionString);
                conn.Open();
                var opt = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
                using var context = new AppDbContext(opt);
                var service = new UserService(new UserRepository(context), new RoleRepository(context));
                return await service.UpdateRoleAsync(1, new UpdateUserRoleDto { RoleId = 2 });
            });

            var task2 = Task.Run(async () =>
            {
                await startBarrier.Task;
                using var conn = new SqliteConnection(connectionString);
                conn.Open();
                var opt = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
                using var context = new AppDbContext(opt);
                var service = new UserService(new UserRepository(context), new RoleRepository(context));
                return await service.UpdateRoleAsync(2, new UpdateUserRoleDto { RoleId = 2 });
            });

            startBarrier.SetResult();

            var results = await WhenSettled(task1, task2);

            var successCount = 0;
            var exceptionCount = 0;

            foreach (var result in results)
            {
                if (result.IsSuccess && result.Value != null)
                {
                    successCount++;
                }
                else if (result.Exception is LastAdminProtectionException)
                {
                    exceptionCount++;
                }
            }

            Assert.Equal(1, successCount);
            Assert.Equal(1, exceptionCount);

            using var verifyConn = new SqliteConnection(connectionString);
            verifyConn.Open();
            var verifyOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(verifyConn).Options;
            using var verifyContext = new AppDbContext(verifyOptions);
            var remainingAdmins = await verifyContext.Users.Include(u => u.Role).Where(u => u.Role.Name == "Admin").ToListAsync();
            Assert.Single(remainingAdmins);
        }
    }

    [Fact]
    public async Task Concurrent_TwoAdmins_MixedDeleteAndDemote_PreservesAtLeastOneAdmin()
    {
        var (keepAlive, connectionString) = CreateSharedInMemoryDatabase();
        using (keepAlive)
        {
            using (var seedConn = new SqliteConnection(connectionString))
            {
                seedConn.Open();
                var seedOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(seedConn).Options;
                using var seedContext = new AppDbContext(seedOptions);
                seedContext.Users.AddRange(
                    new User { Id = 1, Username = "admin1", PasswordHash = "hash", RoleId = 1 },
                    new User { Id = 2, Username = "admin2", PasswordHash = "hash", RoleId = 1 }
                );
                seedContext.SaveChanges();
            }

            var startBarrier = new TaskCompletionSource();

            var deleteTask = Task.Run(async () =>
            {
                await startBarrier.Task;
                using var conn = new SqliteConnection(connectionString);
                conn.Open();
                var opt = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
                using var context = new AppDbContext(opt);
                var service = new UserService(new UserRepository(context), new RoleRepository(context));
                return await service.DeleteAsync(1);
            });

            var demoteTask = Task.Run(async () =>
            {
                await startBarrier.Task;
                using var conn = new SqliteConnection(connectionString);
                conn.Open();
                var opt = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
                using var context = new AppDbContext(opt);
                var service = new UserService(new UserRepository(context), new RoleRepository(context));
                var user = await service.UpdateRoleAsync(2, new UpdateUserRoleDto { RoleId = 2 });
                return user != null;
            });

            startBarrier.SetResult();

            var results = await WhenSettled(deleteTask, demoteTask);

            var successCount = 0;
            var exceptionCount = 0;

            foreach (var result in results)
            {
                if (result.IsSuccess && result.Value)
                {
                    successCount++;
                }
                else if (result.Exception is LastAdminProtectionException)
                {
                    exceptionCount++;
                }
            }

            Assert.Equal(1, successCount);
            Assert.Equal(1, exceptionCount);

            using var verifyConn = new SqliteConnection(connectionString);
            verifyConn.Open();
            var verifyOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(verifyConn).Options;
            using var verifyContext = new AppDbContext(verifyOptions);
            var remainingAdmins = await verifyContext.Users.Include(u => u.Role).Where(u => u.Role.Name == "Admin").ToListAsync();
            Assert.Single(remainingAdmins);
        }
    }

    private sealed class TaskResult<T>
    {
        public bool IsSuccess { get; init; }
        public T? Value { get; init; }
        public Exception? Exception { get; init; }
    }

    private static async Task<IReadOnlyList<TaskResult<T>>> WhenSettled<T>(params Task<T>[] tasks)
    {
        var results = new List<TaskResult<T>>();
        foreach (var task in tasks)
        {
            try
            {
                var val = await task;
                results.Add(new TaskResult<T> { IsSuccess = true, Value = val });
            }
            catch (Exception ex)
            {
                var unwrapped = ex is AggregateException agg ? agg.Flatten().InnerExceptions[0] : ex;
                results.Add(new TaskResult<T> { IsSuccess = false, Exception = unwrapped });
            }
        }
        return results;
    }
}
