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

public sealed class AuthControllerTests
{
    private static (AuthController Controller, Mock<IUserRepository> UserRepo, Mock<IRoleRepository> RoleRepo) CreateController(string? userIdClaim = "1", bool isAdmin = false)
    {
        var userRepo = new Mock<IUserRepository>();
        var roleRepo = new Mock<IRoleRepository>();
        var passwordHasher = new Mock<IPasswordHasher>();
        var tokenService = new Mock<ITokenService>();

        var authService = new AuthService(userRepo.Object, passwordHasher.Object, tokenService.Object);
        var userService = new UserService(userRepo.Object, roleRepo.Object);

        var controller = new AuthController(authService, userService);

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

        return (controller, userRepo, roleRepo);
    }

    [Fact]
    public async Task GetCurrentUser_AuthenticatedUser_Returns200WithCorrectUserDto()
    {
        var (controller, userRepo, _) = CreateController(userIdClaim: "42", isAdmin: false);

        var user = new User
        {
            Id = 42,
            Username = "developer_ivan",
            PasswordHash = "secret_hash",
            RoleId = 2,
            Role = new Role { Id = 2, Name = "User" }
        };

        userRepo.Setup(x => x.GetByIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var actionResult = await controller.GetCurrentUser(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status200OK, okResult.StatusCode);

        var dto = Assert.IsType<UserDto>(okResult.Value);
        Assert.Equal(42, dto.Id);
        Assert.Equal("developer_ivan", dto.Username);
        Assert.Equal(2, dto.RoleId);
        Assert.Equal("User", dto.RoleName);
    }

    [Fact]
    public async Task GetCurrentUser_UserNotFoundInDatabase_Returns404NotFound()
    {
        var (controller, userRepo, _) = CreateController(userIdClaim: "99", isAdmin: false);

        userRepo.Setup(x => x.GetByIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var actionResult = await controller.GetCurrentUser(CancellationToken.None);

        Assert.IsType<NotFoundResult>(actionResult.Result);
    }

    [Fact]
    public async Task GetCurrentUser_MissingUserIdClaim_Returns401Unauthorized()
    {
        var (controller, _, _) = CreateController(userIdClaim: null);

        var actionResult = await controller.GetCurrentUser(CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(actionResult.Result);
    }

    [Fact]
    public async Task GetCurrentUser_InvalidUserIdClaimFormat_Returns401Unauthorized()
    {
        var (controller, userRepo, _) = CreateController(userIdClaim: "not_a_valid_integer");

        var actionResult = await controller.GetCurrentUser(CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(actionResult.Result);
        userRepo.Verify(x => x.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetCurrentUser_Isolation_QueriesOnlyCurrentUserIdAndDoesNotReturnOtherUser()
    {
        var (controller, userRepo, _) = CreateController(userIdClaim: "10", isAdmin: false);

        var currentUser = new User
        {
            Id = 10,
            Username = "user10",
            RoleId = 2,
            Role = new Role { Id = 2, Name = "User" }
        };

        userRepo.Setup(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(currentUser);

        var actionResult = await controller.GetCurrentUser(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<UserDto>(okResult.Value);

        Assert.Equal(10, dto.Id);
        Assert.Equal("user10", dto.Username);

        userRepo.Verify(x => x.GetByIdAsync(10, It.IsAny<CancellationToken>()), Times.Once);
        userRepo.Verify(x => x.GetByIdAsync(It.Is<int>(id => id != 10), It.IsAny<CancellationToken>()), Times.Never);
    }
}
