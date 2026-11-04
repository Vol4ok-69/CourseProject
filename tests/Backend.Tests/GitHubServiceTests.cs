using Api.Common;
using Api.Controllers;
using Application.Exceptions;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Application.Services;
using Domain.Entities;
using Infrastructure.GitHub;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Xunit;

namespace Backend.Tests;

public sealed class GitHubServiceTests
{
    private const string TestPat = "ghp_valid_test_token_1234567890abcdef";

    private sealed class TestHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handlerFunc) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return handlerFunc(request);
        }
    }

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> handlerFunc)
    {
        return new HttpClient(new TestHttpMessageHandler(handlerFunc))
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_WithoutPat_ThrowsInvalidOperationException_AndDoesNotSendHttpRequest()
    {
        var requestSent = false;
        var httpClient = CreateHttpClient(_ =>
        {
            requestSent = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var service = new GitHubService(httpClient);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetCommitsAsync("https://github.com/owner/repo"));

        Assert.Contains("требуется персональный токен доступа (PAT)", ex.Message);
        Assert.False(requestSent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task GitHubService_GetCommitsAsync_EmptyOrWhitespacePat_ThrowsArgumentException_AndDoesNotSendHttpRequest(string? invalidPat)
    {
        var requestSent = false;
        var httpClient = CreateHttpClient(_ =>
        {
            requestSent = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var service = new GitHubService(httpClient);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetCommitsAsync("https://github.com/owner/repo", invalidPat!));

        Assert.False(requestSent);
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_SetsBearerAuthorizationHeader_OnHttpRequestMessage()
    {
        HttpRequestMessage? capturedRequest = null;
        var httpClient = CreateHttpClient(req =>
        {
            capturedRequest = req;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            };
            return Task.FromResult(response);
        });

        var service = new GitHubService(httpClient);
        await service.GetCommitsAsync("https://github.com/owner/repo", TestPat);

        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest.Headers.Authorization);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization.Scheme);
        Assert.Equal(TestPat, capturedRequest.Headers.Authorization.Parameter);
        Assert.Null(httpClient.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_SuccessfulResponse_ParsesCommitsCorrectly()
    {
        var commitsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                sha = "abc1234567890",
                commit = new
                {
                    author = new
                    {
                        date = "2026-11-03T12:00:00Z"
                    }
                }
            }
        });

        var httpClient = CreateHttpClient(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(commitsJson)
            };
            return Task.FromResult(response);
        });

        var service = new GitHubService(httpClient);
        var commits = await service.GetCommitsAsync("https://github.com/owner/repo", TestPat);

        Assert.Single(commits);
        Assert.Equal("abc1234567890", commits[0].Hash);
        Assert.Equal(DateTime.Parse("2026-11-03T12:00:00Z").ToUniversalTime(), commits[0].Date?.ToUniversalTime());
    }

    [Theory]
    [InlineData("not-a-valid-url")]
    [InlineData("https://gitlab.com/owner/repo")]
    [InlineData("https://github.com/onlyone")]
    public async Task GitHubService_GetCommitsAsync_InvalidUrl_ThrowsGitHubIntegrationException(string invalidUrl)
    {
        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var service = new GitHubService(httpClient);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.GetCommitsAsync(invalidUrl, TestPat));

        Assert.Equal(GitHubErrorKind.InvalidRepoUrl, ex.Kind);
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_401Unauthorized_ThrowsGitHubIntegrationException_TokenExpired()
    {
        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var service = new GitHubService(httpClient);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.GetCommitsAsync("https://github.com/owner/repo", TestPat));

        Assert.Equal(GitHubErrorKind.TokenExpired, ex.Kind);
        Assert.DoesNotContain(TestPat, ex.Message);
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_404NotFound_ThrowsGitHubIntegrationException_RepoNotFound()
    {
        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var service = new GitHubService(httpClient);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.GetCommitsAsync("https://github.com/owner/repo", TestPat));

        Assert.Equal(GitHubErrorKind.RepoNotFound, ex.Kind);
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_429TooManyRequests_ThrowsGitHubIntegrationException_RateLimited()
    {
        var httpClient = CreateHttpClient(_ =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)));
        var service = new GitHubService(httpClient);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.GetCommitsAsync("https://github.com/owner/repo", TestPat));

        Assert.Equal(GitHubErrorKind.RateLimited, ex.Kind);
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_403WithRateLimitZeroHeader_ThrowsGitHubIntegrationException_RateLimited()
    {
        var httpClient = CreateHttpClient(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            return Task.FromResult(response);
        });
        var service = new GitHubService(httpClient);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.GetCommitsAsync("https://github.com/owner/repo", TestPat));

        Assert.Equal(GitHubErrorKind.RateLimited, ex.Kind);
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_403WithoutRateLimit_ThrowsGitHubIntegrationException_AccessDenied()
    {
        var httpClient = CreateHttpClient(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "59");
            return Task.FromResult(response);
        });
        var service = new GitHubService(httpClient);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.GetCommitsAsync("https://github.com/owner/repo", TestPat));

        Assert.Equal(GitHubErrorKind.AccessDenied, ex.Kind);
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_NetworkTimeout_ThrowsGitHubIntegrationException_ServiceUnavailable()
    {
        var httpClient = CreateHttpClient(_ =>
            throw new HttpRequestException("Connection refused"));
        var service = new GitHubService(httpClient);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.GetCommitsAsync("https://github.com/owner/repo", TestPat));

        Assert.Equal(GitHubErrorKind.ServiceUnavailable, ex.Kind);
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_502ServerError_ThrowsGitHubIntegrationException_ServiceUnavailable()
    {
        var httpClient = CreateHttpClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        var service = new GitHubService(httpClient);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.GetCommitsAsync("https://github.com/owner/repo", TestPat));

        Assert.Equal(GitHubErrorKind.ServiceUnavailable, ex.Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData((HttpStatusCode)429)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task GitHubService_GetCommitsAsync_ErrorStatuses_NeverLeakTokenInException(HttpStatusCode statusCode)
    {
        const string SecretToken = "ghp_super_secret_pat_never_leak_xyz_123456789";
        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("{\"message\":\"error details with some sensitive data\"}")
        }));

        var service = new GitHubService(httpClient);
        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.GetCommitsAsync("https://github.com/owner/repo", SecretToken));

        Assert.DoesNotContain(SecretToken, ex.Message);
        Assert.DoesNotContain(SecretToken, ex.ToString());
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_NetworkException_NeverLeaksTokenInException()
    {
        const string SecretToken = "ghp_super_secret_pat_never_leak_xyz_123456789";
        var httpClient = CreateHttpClient(_ => throw new HttpRequestException("Socket error"));

        var service = new GitHubService(httpClient);
        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.GetCommitsAsync("https://github.com/owner/repo", SecretToken));

        Assert.DoesNotContain(SecretToken, ex.Message);
        Assert.DoesNotContain(SecretToken, ex.ToString());
    }

    [Fact]
    public async Task GitHubService_SequentialRequests_WithDifferentTokens_UsesRespectiveTokens()
    {
        var capturedTokens = new List<string?>();
        var httpClient = CreateHttpClient(req =>
        {
            capturedTokens.Add(req.Headers.Authorization?.Parameter);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            };
            return Task.FromResult(response);
        });

        var service = new GitHubService(httpClient);
        await service.GetCommitsAsync("https://github.com/owner/repo1", "token-user-1");
        await service.GetCommitsAsync("https://github.com/owner/repo2", "token-user-2");

        Assert.Equal(2, capturedTokens.Count);
        Assert.Equal("token-user-1", capturedTokens[0]);
        Assert.Equal("token-user-2", capturedTokens[1]);
        Assert.Null(httpClient.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task GitHubService_ConcurrentRequests_WithDifferentTokens_DoNotCrossContaminate()
    {
        var recordedTokens = new ConcurrentDictionary<string, string>();
        var httpClient = CreateHttpClient(req =>
        {
            var url = req.RequestUri?.ToString() ?? string.Empty;
            var token = req.Headers.Authorization?.Parameter ?? string.Empty;
            recordedTokens[url] = token;

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            };
            return Task.FromResult(response);
        });

        var service = new GitHubService(httpClient);
        var tasks = Enumerable.Range(1, 20).Select(async i =>
        {
            var repoUrl = $"https://github.com/owner/repo{i}";
            var token = $"user-pat-{i}";
            await Task.Yield();
            await service.GetCommitsAsync(repoUrl, token);
        });

        await Task.WhenAll(tasks);

        Assert.Equal(20, recordedTokens.Count);
        for (int i = 1; i <= 20; i++)
        {
            var expectedUrl = $"https://api.github.com/repos/owner/repo{i}/commits";
            Assert.True(recordedTokens.TryGetValue(expectedUrl, out var actualToken));
            Assert.Equal($"user-pat-{i}", actualToken);
        }
        Assert.Null(httpClient.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task GitHubService_VerifyTokenAndGetUsernameAsync_ValidToken_ReturnsLogin()
    {
        var userJson = JsonSerializer.Serialize(new
        {
            login = "octocat",
            id = 1
        });

        HttpRequestMessage? capturedRequest = null;
        var httpClient = CreateHttpClient(req =>
        {
            capturedRequest = req;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(userJson)
            };
            return Task.FromResult(response);
        });

        var service = new GitHubService(httpClient);
        var username = await service.VerifyTokenAndGetUsernameAsync(TestPat);

        Assert.Equal("octocat", username);
        Assert.NotNull(capturedRequest);
        Assert.Equal("https://api.github.com/user", capturedRequest.RequestUri?.ToString());
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal(TestPat, capturedRequest.Headers.Authorization?.Parameter);
        Assert.Null(httpClient.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task GitHubService_VerifyTokenAndGetUsernameAsync_401Unauthorized_ThrowsTokenExpired()
    {
        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var service = new GitHubService(httpClient);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.VerifyTokenAndGetUsernameAsync(TestPat));

        Assert.Equal(GitHubErrorKind.TokenExpired, ex.Kind);
        Assert.DoesNotContain(TestPat, ex.Message);
        Assert.DoesNotContain(TestPat, ex.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GitHubService_VerifyTokenAndGetUsernameAsync_ErrorStatuses_NeverLeakTokenInException(HttpStatusCode statusCode)
    {
        const string SecretToken = "ghp_super_secret_pat_never_leak_xyz_123456789";
        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("{\"message\":\"bad token\"}")
        }));

        var service = new GitHubService(httpClient);
        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.VerifyTokenAndGetUsernameAsync(SecretToken));

        Assert.DoesNotContain(SecretToken, ex.Message);
        Assert.DoesNotContain(SecretToken, ex.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task GitHubService_VerifyTokenAndGetUsernameAsync_EmptyToken_ThrowsArgumentException_AndDoesNotSendHttpRequest(string? invalidPat)
    {
        var requestSent = false;
        var httpClient = CreateHttpClient(_ =>
        {
            requestSent = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var service = new GitHubService(httpClient);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.VerifyTokenAndGetUsernameAsync(invalidPat!));

        Assert.False(requestSent);
    }

    private static ProjectsController CreateProjectsControllerWithGitHubMock(IGitHubService gitHubService, int userId = 1)
    {
        var projectRepo = new Mock<IProjectRepository>();
        var commitRepo = new Mock<ICommitRepository>();

        projectRepo.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 1, Name = "TestProj", RepoUrl = "https://github.com/test/repo", OwnerId = userId });

        var projectService = new ProjectService(projectRepo.Object);
        var syncService = new GitHubSyncService(gitHubService, projectRepo.Object, commitRepo.Object);

        var controller = new ProjectsController(projectService, syncService);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, "User") };
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
    public async Task ProjectsController_SyncGitHub_InvalidRepoUrl_Returns400BadRequestWithProblemDetails()
    {
        var gitHubMock = new Mock<IGitHubService>();
        gitHubMock.Setup(x => x.GetCommitsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.InvalidRepoUrl, "Некорректный URL"));

        var controller = CreateProjectsControllerWithGitHubMock(gitHubMock.Object);

        var result = await controller.SyncGitHub(1, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problemDetails.Status);
        Assert.Equal(ApiErrorCodes.InvalidRepoUrl, problemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task ProjectsController_SyncGitHub_RepoNotFound_Returns404NotFoundWithProblemDetails()
    {
        var gitHubMock = new Mock<IGitHubService>();
        gitHubMock.Setup(x => x.GetCommitsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.RepoNotFound, "Репозиторий не найден"));

        var controller = CreateProjectsControllerWithGitHubMock(gitHubMock.Object);

        var result = await controller.SyncGitHub(1, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status404NotFound, problemDetails.Status);
        Assert.Equal(ApiErrorCodes.GitHubRepoNotFound, problemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task ProjectsController_SyncGitHub_RateLimited_Returns502BadGatewayWithProblemDetails()
    {
        var gitHubMock = new Mock<IGitHubService>();
        gitHubMock.Setup(x => x.GetCommitsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.RateLimited, "Превышен лимит"));

        var controller = CreateProjectsControllerWithGitHubMock(gitHubMock.Object);

        var result = await controller.SyncGitHub(1, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status502BadGateway, problemDetails.Status);
        Assert.Equal(ApiErrorCodes.GitHubRateLimited, problemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task ProjectsController_SyncGitHub_AccessDenied_Returns502BadGatewayWithProblemDetails()
    {
        var gitHubMock = new Mock<IGitHubService>();
        gitHubMock.Setup(x => x.GetCommitsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.AccessDenied, "Доступ запрещен"));

        var controller = CreateProjectsControllerWithGitHubMock(gitHubMock.Object);

        var result = await controller.SyncGitHub(1, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status502BadGateway, problemDetails.Status);
        Assert.Equal(ApiErrorCodes.GitHubAccessDenied, problemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task ProjectsController_SyncGitHub_ServiceUnavailable_Returns502BadGatewayWithProblemDetails()
    {
        var gitHubMock = new Mock<IGitHubService>();
        gitHubMock.Setup(x => x.GetCommitsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.ServiceUnavailable, "Сервис недоступен"));

        var controller = CreateProjectsControllerWithGitHubMock(gitHubMock.Object);

        var result = await controller.SyncGitHub(1, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status502BadGateway, problemDetails.Status);
        Assert.Equal(ApiErrorCodes.GitHubServiceUnavailable, problemDetails.Extensions["code"]);
    }
}
