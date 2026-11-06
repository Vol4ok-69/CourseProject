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
            var expectedUrl = $"https://api.github.com/repos/owner/repo{i}/commits?per_page=100";
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

    private static ProjectsController CreateProjectsControllerWithGitHubMock(
        IGitHubService gitHubService,
        int userId = 1,
        string? token = "ghp_mock_token_123")
    {
        var projectRepo = new Mock<IProjectRepository>();
        var commitRepo = new Mock<ICommitRepository>();
        var credentialMock = new Mock<IGitHubCredentialService>();

        projectRepo.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 1, Name = "TestProj", RepoUrl = "https://github.com/test/repo", OwnerId = userId });

        credentialMock.Setup(x => x.GetUserTokenAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(token);

        var projectService = new ProjectService(projectRepo.Object);
        var syncService = new GitHubSyncService(gitHubService, projectRepo.Object, commitRepo.Object, credentialMock.Object);

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
        gitHubMock.Setup(x => x.GetCommitsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        gitHubMock.Setup(x => x.GetCommitsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        gitHubMock.Setup(x => x.GetCommitsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        gitHubMock.Setup(x => x.GetCommitsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        gitHubMock.Setup(x => x.GetCommitsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.ServiceUnavailable, "Сервис недоступен"));

        var controller = CreateProjectsControllerWithGitHubMock(gitHubMock.Object);

        var result = await controller.SyncGitHub(1, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status502BadGateway, problemDetails.Status);
        Assert.Equal(ApiErrorCodes.GitHubServiceUnavailable, problemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task ProjectsController_SyncGitHub_TokenRequired_Returns400BadRequestWithProblemDetails()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var controller = CreateProjectsControllerWithGitHubMock(gitHubMock.Object, userId: 1, token: null);

        var result = await controller.SyncGitHub(1, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problemDetails.Status);
        Assert.Equal(ApiErrorCodes.GitHubTokenRequired, problemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task ProjectsController_SyncGitHub_TokenExpired_Returns400BadRequestWithProblemDetails()
    {
        var gitHubMock = new Mock<IGitHubService>();
        gitHubMock.Setup(x => x.GetCommitsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.TokenExpired, "Токен недействителен или отозван"));

        var controller = CreateProjectsControllerWithGitHubMock(gitHubMock.Object);

        var result = await controller.SyncGitHub(1, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problemDetails.Status);
        Assert.Equal(ApiErrorCodes.GitHubTokenExpired, problemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_SinglePageWithoutLinkHeader_MakesSingleRequest()
    {
        var requestCount = 0;
        HttpRequestMessage? capturedRequest = null;
        var commitsJson = JsonSerializer.Serialize(new[]
        {
            new { sha = "sha-page-1", commit = new { author = new { date = "2026-11-01T10:00:00Z" } } }
        });

        var httpClient = CreateHttpClient(req =>
        {
            requestCount++;
            capturedRequest = req;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(commitsJson)
            };
            return Task.FromResult(response);
        });

        var service = new GitHubService(httpClient);
        var commits = await service.GetCommitsAsync("https://github.com/owner/repo", TestPat);

        Assert.Equal(1, requestCount);
        Assert.Single(commits);
        Assert.Equal("sha-page-1", commits[0].Hash);
        Assert.NotNull(capturedRequest);
        Assert.Equal("https://api.github.com/repos/owner/repo/commits?per_page=100", capturedRequest.RequestUri?.ToString());
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal(TestPat, capturedRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_MultiplePages_ReturnsAllCommitsFromAllPages()
    {
        var requestUrls = new List<string>();
        var capturedTokens = new List<string?>();

        var page1Json = JsonSerializer.Serialize(new[]
        {
            new { sha = "sha-1", commit = new { author = new { date = "2026-11-01T10:00:00Z" } } }
        });
        var page2Json = JsonSerializer.Serialize(new[]
        {
            new { sha = "sha-2", commit = new { author = new { date = "2026-11-02T10:00:00Z" } } }
        });
        var page3Json = JsonSerializer.Serialize(new[]
        {
            new { sha = "sha-3", commit = new { author = new { date = "2026-11-03T10:00:00Z" } } }
        });

        var httpClient = CreateHttpClient(req =>
        {
            var url = req.RequestUri?.ToString() ?? string.Empty;
            requestUrls.Add(url);
            capturedTokens.Add(req.Headers.Authorization?.Parameter);

            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (url.Contains("page=2"))
            {
                response.Content = new StringContent(page2Json);
                response.Headers.Add("Link", "<https://api.github.com/repos/owner/repo/commits?per_page=100&page=3>; rel=\"next\"");
            }
            else if (url.Contains("page=3"))
            {
                response.Content = new StringContent(page3Json);
            }
            else
            {
                response.Content = new StringContent(page1Json);
                response.Headers.Add("Link", "<https://api.github.com/repos/owner/repo/commits?per_page=100&page=2>; rel=\"next\"");
            }

            return Task.FromResult(response);
        });

        var service = new GitHubService(httpClient);
        var commits = await service.GetCommitsAsync("https://github.com/owner/repo", TestPat);

        Assert.Equal(3, requestUrls.Count);
        Assert.Equal(3, commits.Count);
        Assert.Equal("sha-1", commits[0].Hash);
        Assert.Equal("sha-2", commits[1].Hash);
        Assert.Equal("sha-3", commits[2].Hash);

        Assert.All(capturedTokens, token => Assert.Equal(TestPat, token));
        Assert.All(requestUrls, url => Assert.DoesNotContain(TestPat, url));
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_LinkHeaderWithLastBeforeNext_ExtractsNextCorrectly()
    {
        var requestCount = 0;
        var page1Json = JsonSerializer.Serialize(new[]
        {
            new { sha = "sha-1", commit = new { author = new { date = "2026-11-01T10:00:00Z" } } }
        });
        var page2Json = JsonSerializer.Serialize(new[]
        {
            new { sha = "sha-2", commit = new { author = new { date = "2026-11-02T10:00:00Z" } } }
        });

        var httpClient = CreateHttpClient(req =>
        {
            requestCount++;
            var response = new HttpResponseMessage(HttpStatusCode.OK);

            if (requestCount == 1)
            {
                response.Content = new StringContent(page1Json);
                response.Headers.Add("Link", "<https://api.github.com/repos/owner/repo/commits?per_page=100&page=5>; rel=\"last\", <https://api.github.com/repos/owner/repo/commits?per_page=100&page=2>; rel=\"next\"");
            }
            else
            {
                response.Content = new StringContent(page2Json);
            }

            return Task.FromResult(response);
        });

        var service = new GitHubService(httpClient);
        var commits = await service.GetCommitsAsync("https://github.com/owner/repo", TestPat);

        Assert.Equal(2, requestCount);
        Assert.Equal(2, commits.Count);
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_LinkHeaderWithoutRelNext_TerminatesPagination()
    {
        var requestCount = 0;
        var commitsJson = JsonSerializer.Serialize(new[]
        {
            new { sha = "sha-1", commit = new { author = new { date = "2026-11-01T10:00:00Z" } } }
        });

        var httpClient = CreateHttpClient(req =>
        {
            requestCount++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(commitsJson)
            };
            response.Headers.Add("Link", "<https://api.github.com/repos/owner/repo/commits?per_page=100&page=1>; rel=\"prev\"");
            return Task.FromResult(response);
        });

        var service = new GitHubService(httpClient);
        var commits = await service.GetCommitsAsync("https://github.com/owner/repo", TestPat);

        Assert.Equal(1, requestCount);
        Assert.Single(commits);
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_ErrorOnSubsequentPage_ThrowsGitHubIntegrationException()
    {
        var requestCount = 0;
        var page1Json = JsonSerializer.Serialize(new[]
        {
            new { sha = "sha-1", commit = new { author = new { date = "2026-11-01T10:00:00Z" } } }
        });

        var httpClient = CreateHttpClient(req =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(page1Json)
                };
                response.Headers.Add("Link", "<https://api.github.com/repos/owner/repo/commits?per_page=100&page=2>; rel=\"next\"");
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });

        var service = new GitHubService(httpClient);
        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.GetCommitsAsync("https://github.com/owner/repo", TestPat));

        Assert.Equal(GitHubErrorKind.TokenExpired, ex.Kind);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_CancellationRequested_StopsPagination()
    {
        using var cts = new CancellationTokenSource();
        var requestCount = 0;
        var page1Json = JsonSerializer.Serialize(new[]
        {
            new { sha = "sha-1", commit = new { author = new { date = "2026-11-01T10:00:00Z" } } }
        });

        var httpClient = CreateHttpClient(req =>
        {
            requestCount++;
            cts.Cancel();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(page1Json)
            };
            response.Headers.Add("Link", "<https://api.github.com/repos/owner/repo/commits?per_page=100&page=2>; rel=\"next\"");
            return Task.FromResult(response);
        });

        var service = new GitHubService(httpClient);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetCommitsAsync("https://github.com/owner/repo", TestPat, cts.Token));

        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_NextUrlWithExternalHost_ThrowsBeforeSendingRequest()
    {
        var requestCount = 0;
        var page1Json = JsonSerializer.Serialize(new[]
        {
            new { sha = "sha-1", commit = new { author = new { date = "2026-11-01T10:00:00Z" } } }
        });

        var httpClient = CreateHttpClient(req =>
        {
            requestCount++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(page1Json)
            };
            response.Headers.Add("Link", "<https://malicious-external-domain.com/commits?page=2>; rel=\"next\"");
            return Task.FromResult(response);
        });

        var service = new GitHubService(httpClient);
        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.GetCommitsAsync("https://github.com/owner/repo", TestPat));

        Assert.Equal(GitHubErrorKind.ServiceUnavailable, ex.Kind);
        Assert.Contains("недопустимый хост или протокол", ex.Message);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_NextUrlWithHttpScheme_ThrowsBeforeSendingRequest()
    {
        var requestCount = 0;
        var page1Json = JsonSerializer.Serialize(new[]
        {
            new { sha = "sha-1", commit = new { author = new { date = "2026-11-01T10:00:00Z" } } }
        });

        var httpClient = CreateHttpClient(req =>
        {
            requestCount++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(page1Json)
            };
            response.Headers.Add("Link", "<http://api.github.com/repos/owner/repo/commits?per_page=100&page=2>; rel=\"next\"");
            return Task.FromResult(response);
        });

        var service = new GitHubService(httpClient);
        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.GetCommitsAsync("https://github.com/owner/repo", TestPat));

        Assert.Equal(GitHubErrorKind.ServiceUnavailable, ex.Kind);
        Assert.Contains("недопустимый хост или протокол", ex.Message);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_NextUrlWithDifferentResourcePath_ThrowsBeforeSendingRequest()
    {
        var requestCount = 0;
        var page1Json = JsonSerializer.Serialize(new[]
        {
            new { sha = "sha-1", commit = new { author = new { date = "2026-11-01T10:00:00Z" } } }
        });

        var httpClient = CreateHttpClient(req =>
        {
            requestCount++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(page1Json)
            };
            response.Headers.Add("Link", "<https://api.github.com/user>; rel=\"next\"");
            return Task.FromResult(response);
        });

        var service = new GitHubService(httpClient);
        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.GetCommitsAsync("https://github.com/owner/repo", TestPat));

        Assert.Equal(GitHubErrorKind.ServiceUnavailable, ex.Kind);
        Assert.Contains("недопустимый путь ресурса", ex.Message);
        Assert.Equal(1, requestCount);
    }
}
