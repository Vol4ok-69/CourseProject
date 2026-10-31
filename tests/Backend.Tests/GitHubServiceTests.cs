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
using System.Net;
using System.Security.Claims;
using Xunit;

namespace Backend.Tests;

public sealed class GitHubServiceTests
{
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

    [Theory]
    [InlineData("not-a-valid-url")]
    [InlineData("https://gitlab.com/owner/repo")]
    [InlineData("https://github.com/onlyone")]
    public async Task GitHubService_GetCommitsAsync_InvalidUrl_ThrowsGitHubIntegrationException(string invalidUrl)
    {
        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var service = new GitHubService(httpClient);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() => service.GetCommitsAsync(invalidUrl));
        Assert.Equal(GitHubErrorKind.InvalidRepoUrl, ex.Kind);
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_404NotFound_ThrowsGitHubIntegrationException_RepoNotFound()
    {
        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var service = new GitHubService(httpClient);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.GetCommitsAsync("https://github.com/owner/repo"));

        Assert.Equal(GitHubErrorKind.RepoNotFound, ex.Kind);
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_429TooManyRequests_ThrowsGitHubIntegrationException_RateLimited()
    {
        var httpClient = CreateHttpClient(_ =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)));
        var service = new GitHubService(httpClient);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.GetCommitsAsync("https://github.com/owner/repo"));

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
            service.GetCommitsAsync("https://github.com/owner/repo"));

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
            service.GetCommitsAsync("https://github.com/owner/repo"));

        Assert.Equal(GitHubErrorKind.AccessDenied, ex.Kind);
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_NetworkTimeout_ThrowsGitHubIntegrationException_ServiceUnavailable()
    {
        var httpClient = CreateHttpClient(_ =>
            throw new HttpRequestException("Connection refused"));
        var service = new GitHubService(httpClient);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.GetCommitsAsync("https://github.com/owner/repo"));

        Assert.Equal(GitHubErrorKind.ServiceUnavailable, ex.Kind);
    }

    [Fact]
    public async Task GitHubService_GetCommitsAsync_502ServerError_ThrowsGitHubIntegrationException_ServiceUnavailable()
    {
        var httpClient = CreateHttpClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        var service = new GitHubService(httpClient);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.GetCommitsAsync("https://github.com/owner/repo"));

        Assert.Equal(GitHubErrorKind.ServiceUnavailable, ex.Kind);
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
