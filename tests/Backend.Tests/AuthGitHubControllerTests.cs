using Api.Common;
using Api.Controllers;
using Application.DTOs;
using Application.Exceptions;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Application.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using System.Security.Claims;
using Xunit;

namespace Backend.Tests;

public sealed class AuthGitHubControllerTests
{
    private static (AuthController Controller, Mock<IGitHubCredentialService> CredentialMock) CreateController(
        string? userIdClaim = "1",
        Mock<IGitHubCredentialService>? credentialMock = null)
    {
        var userRepo = new Mock<IUserRepository>();
        var roleRepo = new Mock<IRoleRepository>();
        var passwordHasher = new Mock<IPasswordHasher>();
        var tokenService = new Mock<ITokenService>();

        var authService = new AuthService(userRepo.Object, passwordHasher.Object, tokenService.Object);
        var userService = new UserService(userRepo.Object, roleRepo.Object);

        credentialMock ??= new Mock<IGitHubCredentialService>();

        var controller = new AuthController(authService, userService, credentialMock.Object);

        var claims = new List<Claim>();
        if (userIdClaim is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userIdClaim));
            claims.Add(new Claim(ClaimTypes.Name, "testuser"));
            claims.Add(new Claim(ClaimTypes.Role, "User"));
        }

        var identity = new ClaimsIdentity(claims, userIdClaim is not null ? "TestAuth" : null);
        var claimsPrincipal = new ClaimsPrincipal(identity);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claimsPrincipal }
        };

        return (controller, credentialMock);
    }

    [Fact]
    public async Task GetGitHubStatus_AuthenticatedUser_Returns200WithStatus()
    {
        var (controller, credMock) = CreateController(userIdClaim: "5");
        var expectedStatus = new GitHubIntegrationStatusDto
        {
            IsConnected = true,
            GitHubUsername = "octocat",
            ConnectedAt = DateTime.UtcNow
        };

        credMock.Setup(s => s.GetIntegrationStatusAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedStatus);

        var response = await controller.GetGitHubStatus(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(response.Result);
        var status = Assert.IsType<GitHubIntegrationStatusDto>(okResult.Value);
        Assert.True(status.IsConnected);
        Assert.Equal("octocat", status.GitHubUsername);
    }

    [Fact]
    public async Task GetGitHubStatus_UnauthenticatedUser_Returns401()
    {
        var (controller, _) = CreateController(userIdClaim: null);

        var response = await controller.GetGitHubStatus(CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(response.Result);
    }

    [Fact]
    public async Task GetGitHubStatus_UserNotFound_Returns404WithProblemDetails()
    {
        var (controller, credMock) = CreateController(userIdClaim: "999");
        credMock.Setup(s => s.GetIntegrationStatusAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((GitHubIntegrationStatusDto?)null);

        var response = await controller.GetGitHubStatus(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(ApiErrorCodes.UserNotFound, problem.Extensions["code"]);
    }

    [Fact]
    public async Task SetGitHubToken_ValidToken_Returns200WithUpdatedStatus_AndNeverReturnsPat()
    {
        const string Pat = "ghp_valid_token_123456789";
        var (controller, credMock) = CreateController(userIdClaim: "10");
        var expectedStatus = new GitHubIntegrationStatusDto
        {
            IsConnected = true,
            GitHubUsername = "developer_pro",
            ConnectedAt = DateTime.UtcNow
        };

        credMock.Setup(s => s.SetUserTokenAsync(10, Pat, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedStatus);

        var response = await controller.SetGitHubToken(new GitHubTokenRequestDto { Token = Pat }, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(response.Result);
        var status = Assert.IsType<GitHubIntegrationStatusDto>(okResult.Value);
        Assert.True(status.IsConnected);
        Assert.Equal("developer_pro", status.GitHubUsername);

        // Убеждаемся, что в ответе отсутствуют поля токена
        var properties = typeof(GitHubIntegrationStatusDto).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("Token", properties);
        Assert.DoesNotContain("EncryptedToken", properties);
        Assert.DoesNotContain("Pat", properties);
    }

    [Fact]
    public async Task SetGitHubToken_UnauthenticatedUser_Returns401()
    {
        var (controller, _) = CreateController(userIdClaim: null);

        var response = await controller.SetGitHubToken(new GitHubTokenRequestDto { Token = "ghp_test" }, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(response.Result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task SetGitHubToken_EmptyOrWhitespaceToken_Returns400ValidationFailed(string? emptyToken)
    {
        var (controller, _) = CreateController(userIdClaim: "1");

        var response = await controller.SetGitHubToken(new GitHubTokenRequestDto { Token = emptyToken! }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(ApiErrorCodes.ValidationFailed, problem.Extensions["code"]);
    }

    [Fact]
    public async Task SetGitHubToken_InvalidOrExpiredToken_Returns400WithGitHubTokenExpiredCode()
    {
        const string BadToken = "ghp_expired_token";
        var (controller, credMock) = CreateController(userIdClaim: "1");

        credMock.Setup(s => s.SetUserTokenAsync(1, BadToken, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.TokenExpired, "Персональный токен GitHub недействителен или отозван."));

        var response = await controller.SetGitHubToken(new GitHubTokenRequestDto { Token = BadToken }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(ApiErrorCodes.GitHubTokenExpired, problem.Extensions["code"]);
    }

    [Fact]
    public async Task SetGitHubToken_AccessDenied_Returns502WithGitHubAccessDeniedCode()
    {
        const string Token = "ghp_token_no_rights";
        var (controller, credMock) = CreateController(userIdClaim: "1");

        credMock.Setup(s => s.SetUserTokenAsync(1, Token, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.AccessDenied, "Доступ запрещен"));

        var response = await controller.SetGitHubToken(new GitHubTokenRequestDto { Token = Token }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, objectResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(ApiErrorCodes.GitHubAccessDenied, problem.Extensions["code"]);
    }

    [Fact]
    public async Task SetGitHubToken_RateLimited_Returns502WithGitHubRateLimitedCode()
    {
        const string Token = "ghp_token_rate_limited";
        var (controller, credMock) = CreateController(userIdClaim: "1");

        credMock.Setup(s => s.SetUserTokenAsync(1, Token, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.RateLimited, "Превышен лимит запросов"));

        var response = await controller.SetGitHubToken(new GitHubTokenRequestDto { Token = Token }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, objectResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(ApiErrorCodes.GitHubRateLimited, problem.Extensions["code"]);
    }

    [Fact]
    public async Task SetGitHubToken_ServiceUnavailable_Returns502WithGitHubServiceUnavailableCode()
    {
        const string Token = "ghp_token_service_down";
        var (controller, credMock) = CreateController(userIdClaim: "1");

        credMock.Setup(s => s.SetUserTokenAsync(1, Token, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.ServiceUnavailable, "Сервис недоступен"));

        var response = await controller.SetGitHubToken(new GitHubTokenRequestDto { Token = Token }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, objectResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(ApiErrorCodes.GitHubServiceUnavailable, problem.Extensions["code"]);
    }

    [Fact]
    public async Task SetGitHubToken_UnexpectedException_Returns500WithoutLeakingCryptoDetails()
    {
        const string SecretPat = "ghp_secret_token_crypto_fail";
        var (controller, credMock) = CreateController(userIdClaim: "1");

        credMock.Setup(s => s.SetUserTokenAsync(1, SecretPat, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Cryptographic failure: Tag mismatch during AES-GCM"));

        var response = await controller.SetGitHubToken(new GitHubTokenRequestDto { Token = SecretPat }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(ApiErrorCodes.InternalServerError, problem.Extensions["code"]);
        Assert.DoesNotContain("AES-GCM", problem.Detail);
        Assert.DoesNotContain(SecretPat, problem.Detail);
    }

    [Fact]
    public async Task SetGitHubToken_UserNotFound_Returns404WithUserNotFoundCode()
    {
        var (controller, credMock) = CreateController(userIdClaim: "888");
        credMock.Setup(s => s.SetUserTokenAsync(888, "ghp_token", It.IsAny<CancellationToken>()))
            .ReturnsAsync((GitHubIntegrationStatusDto?)null);

        var response = await controller.SetGitHubToken(new GitHubTokenRequestDto { Token = "ghp_token" }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(ApiErrorCodes.UserNotFound, problem.Extensions["code"]);
    }

    [Fact]
    public async Task RemoveGitHubToken_AuthenticatedUser_Returns200WithDisconnectedStatus()
    {
        var (controller, credMock) = CreateController(userIdClaim: "7");
        var expectedStatus = new GitHubIntegrationStatusDto
        {
            IsConnected = false,
            GitHubUsername = null,
            ConnectedAt = null
        };

        credMock.Setup(s => s.RemoveUserTokenAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedStatus);

        var response = await controller.RemoveGitHubToken(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(response.Result);
        var status = Assert.IsType<GitHubIntegrationStatusDto>(okResult.Value);
        Assert.False(status.IsConnected);
        Assert.Null(status.GitHubUsername);
    }

    [Fact]
    public async Task RemoveGitHubToken_UnauthenticatedUser_Returns401()
    {
        var (controller, _) = CreateController(userIdClaim: null);

        var response = await controller.RemoveGitHubToken(CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(response.Result);
    }

    [Fact]
    public async Task RemoveGitHubToken_UserNotFound_Returns404WithUserNotFoundCode()
    {
        var (controller, credMock) = CreateController(userIdClaim: "777");
        credMock.Setup(s => s.RemoveUserTokenAsync(777, It.IsAny<CancellationToken>()))
            .ReturnsAsync((GitHubIntegrationStatusDto?)null);

        var response = await controller.RemoveGitHubToken(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(ApiErrorCodes.UserNotFound, problem.Extensions["code"]);
    }

    [Fact]
    public async Task Controller_IdentifiesUserOnlyFromClaims_DoesNotAllowManipulatingOtherUsers()
    {
        // Аутентифицированный пользователь имеет ID 42 в claims
        var (controller, credMock) = CreateController(userIdClaim: "42");

        credMock.Setup(s => s.SetUserTokenAsync(42, "ghp_token_42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubIntegrationStatusDto { IsConnected = true });

        await controller.SetGitHubToken(new GitHubTokenRequestDto { Token = "ghp_token_42" }, CancellationToken.None);

        // Сервис был вызван строго с идентификатором 42 из claims principal
        credMock.Verify(s => s.SetUserTokenAsync(42, "ghp_token_42", It.IsAny<CancellationToken>()), Times.Once);

        // Никакой другой идентификатор пользователя не мог быть затронут
        credMock.Verify(s => s.SetUserTokenAsync(It.Is<int>(id => id != 42), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Controller_TokenLeakageTest_ErrorResponsesNeverContainPat()
    {
        const string SuperSecretPat = "ghp_super_secret_pat_999999999_never_leak_in_api";
        var (controller, credMock) = CreateController(userIdClaim: "1");

        credMock.Setup(s => s.SetUserTokenAsync(1, SuperSecretPat, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.TokenExpired, "Персональный токен недействителен"));

        var response = await controller.SetGitHubToken(new GitHubTokenRequestDto { Token = SuperSecretPat }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(response.Result);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);

        Assert.DoesNotContain(SuperSecretPat, problem.Title);
        Assert.DoesNotContain(SuperSecretPat, problem.Detail);
        foreach (var extension in problem.Extensions)
        {
            Assert.DoesNotContain(SuperSecretPat, extension.Value?.ToString());
        }
    }
}
