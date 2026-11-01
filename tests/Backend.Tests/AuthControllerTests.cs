using Api.Common;
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
    private static (AuthController Controller, Mock<IUserRepository> UserRepo, Mock<IRoleRepository> RoleRepo, Mock<IPasswordHasher> Hasher, Mock<ITokenService> TokenService) CreateController(string? userIdClaim = "1", bool isAdmin = false)
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

        return (controller, userRepo, roleRepo, passwordHasher, tokenService);
    }

    [Fact]
    public async Task GetCurrentUser_AuthenticatedUser_Returns200WithCorrectUserDto()
    {
        var (controller, userRepo, _, _, _) = CreateController(userIdClaim: "42", isAdmin: false);

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
        var (controller, userRepo, _, _, _) = CreateController(userIdClaim: "99", isAdmin: false);

        userRepo.Setup(x => x.GetByIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var actionResult = await controller.GetCurrentUser(CancellationToken.None);

        Assert.IsType<NotFoundResult>(actionResult.Result);
    }

    [Fact]
    public async Task GetCurrentUser_MissingUserIdClaim_Returns401Unauthorized()
    {
        var (controller, _, _, _, _) = CreateController(userIdClaim: null);

        var actionResult = await controller.GetCurrentUser(CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(actionResult.Result);
    }

    [Fact]
    public async Task GetCurrentUser_InvalidUserIdClaimFormat_Returns401Unauthorized()
    {
        var (controller, userRepo, _, _, _) = CreateController(userIdClaim: "not_a_valid_integer");

        var actionResult = await controller.GetCurrentUser(CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(actionResult.Result);
        userRepo.Verify(x => x.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetCurrentUser_Isolation_QueriesOnlyCurrentUserIdAndDoesNotReturnOtherUser()
    {
        var (controller, userRepo, _, _, _) = CreateController(userIdClaim: "10", isAdmin: false);

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

    [Fact]
    public async Task Register_ValidDto_Returns200WithAccessToken()
    {
        var (controller, userRepo, _, hasher, tokenService) = CreateController();

        var createdUser = new User { Id = 1, Username = "new_user", PasswordHash = "hashed_password", Role = new Role { Id = 2, Name = "User" } };
        userRepo.Setup(x => x.GetByUsernameAsync("new_user", It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);
        userRepo.Setup(x => x.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(createdUser);
        hasher.Setup(x => x.Hash("securepass")).Returns("hashed_password");
        tokenService.Setup(x => x.GenerateToken(It.IsAny<User>())).Returns("jwt_access_token_123");

        var dto = new RegisterDto { Username = "new_user", Password = "securepass" };
        var actionResult = await controller.Register(dto, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status200OK, okResult.StatusCode);

        var response = Assert.IsType<AuthResponseDto>(okResult.Value);
        Assert.Equal("jwt_access_token_123", response.AccessToken);
    }

    [Fact]
    public async Task Register_ExistingUsername_Returns409ConflictWithUsernameAlreadyExistsCode()
    {
        var (controller, userRepo, _, _, _) = CreateController();

        var existingUser = new User { Id = 5, Username = "existing_user", PasswordHash = "hash" };
        userRepo.Setup(x => x.GetByUsernameAsync("existing_user", It.IsAny<CancellationToken>())).ReturnsAsync(existingUser);

        var dto = new RegisterDto { Username = "existing_user", Password = "securepass" };
        var actionResult = await controller.Register(dto, CancellationToken.None);

        var conflictResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflictResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(conflictResult.Value);
        Assert.Equal(ApiErrorCodes.UsernameAlreadyExists, problem.Extensions["code"]?.ToString());
    }

    [Fact]
    public async Task Login_ValidCredentials_Returns200WithAccessToken()
    {
        var (controller, userRepo, _, hasher, tokenService) = CreateController();

        var user = new User { Id = 7, Username = "login_user", PasswordHash = "correct_hash", Role = new Role { Id = 2, Name = "User" } };
        userRepo.Setup(x => x.GetByUsernameAsync("login_user", It.IsAny<CancellationToken>())).ReturnsAsync(user);
        hasher.Setup(x => x.Verify("secretpass", "correct_hash")).Returns(true);
        tokenService.Setup(x => x.GenerateToken(user)).Returns("valid_jwt_token");

        var dto = new LoginDto { Username = "login_user", Password = "secretpass" };
        var actionResult = await controller.Login(dto, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var response = Assert.IsType<AuthResponseDto>(okResult.Value);
        Assert.Equal("valid_jwt_token", response.AccessToken);
    }

    [Fact]
    public async Task Login_InvalidCredentials_Returns401UnauthorizedWithInvalidCredentialsCode()
    {
        var (controller, userRepo, _, hasher, _) = CreateController();

        var user = new User { Id = 7, Username = "login_user", PasswordHash = "correct_hash" };
        userRepo.Setup(x => x.GetByUsernameAsync("login_user", It.IsAny<CancellationToken>())).ReturnsAsync(user);
        hasher.Setup(x => x.Verify("wrongpass", "correct_hash")).Returns(false);

        var dto = new LoginDto { Username = "login_user", Password = "wrongpass" };
        var actionResult = await controller.Login(dto, CancellationToken.None);

        var unauthorizedResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorizedResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(unauthorizedResult.Value);
        Assert.Equal(ApiErrorCodes.InvalidCredentials, problem.Extensions["code"]?.ToString());
    }

    [Fact]
    public async Task ChangeUsername_Success_Returns200WithNewAccessToken()
    {
        var (controller, userRepo, _, _, tokenService) = CreateController(userIdClaim: "1");

        var user = new User { Id = 1, Username = "old_login", PasswordHash = "hash", Role = new Role { Id = 2, Name = "User" } };
        userRepo.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        userRepo.Setup(x => x.GetByUsernameAsync("new_login", It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);
        tokenService.Setup(x => x.GenerateToken(user)).Returns("new_jwt_token");

        var dto = new ChangeUsernameDto { Username = "new_login" };
        var actionResult = await controller.ChangeUsername(dto, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var response = Assert.IsType<AuthResponseDto>(okResult.Value);
        Assert.Equal("new_jwt_token", response.AccessToken);
    }

    [Fact]
    public async Task ChangeUsername_MissingClaim_Returns401Unauthorized()
    {
        var (controller, _, _, _, _) = CreateController(userIdClaim: null);

        var dto = new ChangeUsernameDto { Username = "new_login" };
        var actionResult = await controller.ChangeUsername(dto, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(actionResult.Result);
    }

    [Fact]
    public async Task ChangeUsername_DuplicateUsername_Returns409ConflictWithUsernameAlreadyExistsCode()
    {
        var (controller, userRepo, _, _, _) = CreateController(userIdClaim: "1");

        var currentUser = new User { Id = 1, Username = "user1", PasswordHash = "hash" };
        var existingUser = new User { Id = 2, Username = "taken_login", PasswordHash = "hash" };

        userRepo.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(currentUser);
        userRepo.Setup(x => x.GetByUsernameAsync("taken_login", It.IsAny<CancellationToken>())).ReturnsAsync(existingUser);

        var dto = new ChangeUsernameDto { Username = "taken_login" };
        var actionResult = await controller.ChangeUsername(dto, CancellationToken.None);

        var conflictResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflictResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(conflictResult.Value);
        Assert.Equal(ApiErrorCodes.UsernameAlreadyExists, problem.Extensions["code"]?.ToString());
    }

    [Fact]
    public async Task ChangePassword_Success_Returns200WithNewAccessToken()
    {
        var (controller, userRepo, _, hasher, tokenService) = CreateController(userIdClaim: "1");

        var user = new User { Id = 1, Username = "user1", PasswordHash = "old_hash", Role = new Role { Id = 2, Name = "User" } };
        userRepo.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        hasher.Setup(x => x.Verify("current_pass", "old_hash")).Returns(true);
        hasher.Setup(x => x.Hash("new_pass")).Returns("new_hash");
        tokenService.Setup(x => x.GenerateToken(user)).Returns("password_changed_token");

        var dto = new ChangePasswordDto { CurrentPassword = "current_pass", NewPassword = "new_pass" };
        var actionResult = await controller.ChangePassword(dto, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var response = Assert.IsType<AuthResponseDto>(okResult.Value);
        Assert.Equal("password_changed_token", response.AccessToken);
    }

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_Returns400BadRequestWithInvalidPasswordCode()
    {
        var (controller, userRepo, _, hasher, _) = CreateController(userIdClaim: "1");

        var user = new User { Id = 1, Username = "user1", PasswordHash = "old_hash" };
        userRepo.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        hasher.Setup(x => x.Verify("wrong_pass", "old_hash")).Returns(false);

        var dto = new ChangePasswordDto { CurrentPassword = "wrong_pass", NewPassword = "new_pass" };
        var actionResult = await controller.ChangePassword(dto, CancellationToken.None);

        var badRequestResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequestResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(badRequestResult.Value);
        Assert.Equal(ApiErrorCodes.InvalidPassword, problem.Extensions["code"]?.ToString());
    }
}
