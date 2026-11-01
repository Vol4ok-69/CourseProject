using Api.Common;
using Api.Controllers;
using Application.DTOs;
using Application.Exceptions;
using Application.Interfaces.Repositories;
using Application.Services;
using Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using System.Security.Claims;
using Xunit;

namespace Backend.Tests;

public sealed class AdminUsersControllerTests
{
    private static (AdminUsersController Controller, Mock<IUserRepository> UserRepo, Mock<IRoleRepository> RoleRepo) CreateController(string? currentUserId = "1")
    {
        var userRepo = new Mock<IUserRepository>();
        var roleRepo = new Mock<IRoleRepository>();
        var userService = new UserService(userRepo.Object, roleRepo.Object);

        var controller = new AdminUsersController(userService);

        var claims = new List<Claim>();
        if (currentUserId is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, currentUserId));
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));
            claims.Add(new Claim(ClaimTypes.Name, "admin_user"));
        }

        var identity = new ClaimsIdentity(claims, currentUserId is not null ? "TestAuth" : null);
        var principal = new ClaimsPrincipal(identity);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        return (controller, userRepo, roleRepo);
    }

    [Fact]
    public async Task GetAll_ReturnsListOfUserDtos()
    {
        var (controller, userRepo, _) = CreateController();

        var users = new List<User>
        {
            new() { Id = 1, Username = "admin", RoleId = 1, Role = new Role { Id = 1, Name = "Admin" } },
            new() { Id = 2, Username = "user1", RoleId = 2, Role = new Role { Id = 2, Name = "User" } }
        };

        userRepo.Setup(x => x.GetAllAsync(1, 20, It.IsAny<CancellationToken>())).ReturnsAsync((users, 2));

        var result = await controller.GetAll(1, 20, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var pagedResult = Assert.IsType<PagedResultDto<UserDto>>(okResult.Value);
        Assert.Equal(2, pagedResult.TotalCount);
        Assert.Equal(1, pagedResult.Page);
        Assert.Equal(20, pagedResult.PageSize);
        Assert.Equal(2, pagedResult.Items.Count);
        Assert.Equal("admin", pagedResult.Items[0].Username);
        Assert.Equal("user1", pagedResult.Items[1].Username);
    }

    [Fact]
    public async Task GetById_UserFound_ReturnsUserDto()
    {
        var (controller, userRepo, _) = CreateController();

        var user = new User { Id = 2, Username = "user1", RoleId = 2, Role = new Role { Id = 2, Name = "User" } };
        userRepo.Setup(x => x.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var result = await controller.GetById(2, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<UserDto>(okResult.Value);
        Assert.Equal(2, dto.Id);
        Assert.Equal("user1", dto.Username);
    }

    [Fact]
    public async Task GetById_UserNotFound_Returns404NotFound()
    {
        var (controller, userRepo, _) = CreateController();

        userRepo.Setup(x => x.GetByIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var result = await controller.GetById(99, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Update_UserFound_ReturnsUpdatedUserDto()
    {
        var (controller, userRepo, _) = CreateController();

        var existing = new User { Id = 2, Username = "oldname", RoleId = 2, Role = new Role { Id = 2, Name = "User" } };
        userRepo.Setup(x => x.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        userRepo.Setup(x => x.GetByUsernameAsync("newname", It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);
        userRepo.Setup(x => x.UpdateAsync(It.IsAny<User>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var dto = new UpdateUserDto { Username = "newname" };
        var result = await controller.Update(2, dto, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var responseDto = Assert.IsType<UserDto>(okResult.Value);
        Assert.Equal("newname", responseDto.Username);
    }

    [Fact]
    public async Task Update_UserNotFound_Returns404NotFound()
    {
        var (controller, userRepo, _) = CreateController();

        userRepo.Setup(x => x.GetByIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var dto = new UpdateUserDto { Username = "newname" };
        var result = await controller.Update(99, dto, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Update_DuplicateUsername_Returns409ConflictWithProblemDetails()
    {
        var (controller, userRepo, _) = CreateController();

        var existing = new User { Id = 2, Username = "user2", RoleId = 2, Role = new Role { Id = 2, Name = "User" } };
        var otherUser = new User { Id = 3, Username = "taken_name", RoleId = 2, Role = new Role { Id = 2, Name = "User" } };

        userRepo.Setup(x => x.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        userRepo.Setup(x => x.GetByUsernameAsync("taken_name", It.IsAny<CancellationToken>())).ReturnsAsync(otherUser);

        var dto = new UpdateUserDto { Username = "taken_name" };
        var result = await controller.Update(2, dto, CancellationToken.None);

        var conflictResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflictResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(conflictResult.Value);
        Assert.Equal(ApiErrorCodes.UsernameAlreadyExists, problem.Extensions["code"]?.ToString());
    }

    [Fact]
    public async Task UpdateRole_SelfUpdateRole_Returns403Forbidden()
    {
        var (controller, _, _) = CreateController(currentUserId: "1");

        var dto = new UpdateUserRoleDto { RoleId = 2 };
        var result = await controller.UpdateRole(1, dto, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task UpdateRole_MissingAuthClaim_Returns401Unauthorized()
    {
        var (controller, _, _) = CreateController(currentUserId: null);

        var dto = new UpdateUserRoleDto { RoleId = 2 };
        var result = await controller.UpdateRole(2, dto, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    [Fact]
    public async Task UpdateRole_RoleNotFound_Returns404NotFoundWithRoleNotFoundCode()
    {
        var (controller, userRepo, roleRepo) = CreateController(currentUserId: "1");

        var targetUser = new User { Id = 2, Username = "user2", RoleId = 2, Role = new Role { Id = 2, Name = "User" } };
        userRepo.Setup(x => x.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(targetUser);
        roleRepo.Setup(x => x.GetByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((Role?)null);

        var dto = new UpdateUserRoleDto { RoleId = 999 };
        var result = await controller.UpdateRole(2, dto, CancellationToken.None);

        var notFoundResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, notFoundResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(notFoundResult.Value);
        Assert.Equal(ApiErrorCodes.RoleNotFound, problem.Extensions["code"]?.ToString());
    }

    [Fact]
    public async Task UpdateRole_LastAdminProtection_Returns409ConflictWithLastAdminProtectedCode()
    {
        var (controller, userRepo, roleRepo) = CreateController(currentUserId: "1");

        var targetUser = new User { Id = 2, Username = "admin2", RoleId = 1, Role = new Role { Id = 1, Name = "Admin" } };
        var userRole = new Role { Id = 2, Name = "User" };

        userRepo.Setup(x => x.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(targetUser);
        roleRepo.Setup(x => x.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(userRole);
        userRepo.Setup(x => x.CountAdminsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var dto = new UpdateUserRoleDto { RoleId = 2 };
        var result = await controller.UpdateRole(2, dto, CancellationToken.None);

        var conflictResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflictResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(conflictResult.Value);
        Assert.Equal(ApiErrorCodes.LastAdminProtected, problem.Extensions["code"]?.ToString());
    }

    [Fact]
    public async Task Delete_SelfDelete_Returns403Forbidden()
    {
        var (controller, _, _) = CreateController(currentUserId: "1");

        var result = await controller.Delete(1, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Delete_MissingAuthClaim_Returns401Unauthorized()
    {
        var (controller, _, _) = CreateController(currentUserId: null);

        var result = await controller.Delete(2, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Delete_UserNotFound_Returns404NotFound()
    {
        var (controller, userRepo, _) = CreateController(currentUserId: "1");

        userRepo.Setup(x => x.GetByIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var result = await controller.Delete(99, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Delete_UserHasProjects_Returns409ConflictWithUserHasProjectsCode()
    {
        var (controller, userRepo, _) = CreateController(currentUserId: "1");

        var targetUser = new User
        {
            Id = 2,
            Username = "user2",
            RoleId = 2,
            Role = new Role { Id = 2, Name = "User" }
        };

        userRepo.Setup(x => x.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(targetUser);
        userRepo.Setup(x => x.HasProjectsAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await controller.Delete(2, CancellationToken.None);

        var conflictResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflictResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(conflictResult.Value);
        Assert.Equal(ApiErrorCodes.UserHasProjects, problem.Extensions["code"]?.ToString());
    }

    [Fact]
    public async Task Delete_Success_Returns204NoContent()
    {
        var (controller, userRepo, _) = CreateController(currentUserId: "1");

        var targetUser = new User
        {
            Id = 2,
            Username = "user2",
            RoleId = 2,
            Role = new Role { Id = 2, Name = "User" },
            Projects = []
        };

        userRepo.Setup(x => x.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(targetUser);
        userRepo.Setup(x => x.DeleteAsync(It.IsAny<User>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var result = await controller.Delete(2, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }
}
