using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Common;
using Application.DTOs;
using Application.Exceptions;
using Application.Interfaces;
using Application.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;

namespace Backend.Tests.Integration;

public sealed class GitHubSyncExceptionMappingIntegrationTests(ApiWebApplicationFactory factory)
    : IClassFixture<ApiWebApplicationFactory>, IAsyncLifetime
{
    private readonly ApiWebApplicationFactory _factory = factory;

    public async Task InitializeAsync()
    {
        _factory.ResetMocks();
        await _factory.ResetDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SyncGitHub_SyncCheckpointChangedException_Returns409ProblemDetailsWithoutLeakingInternalDetails()
    {
        var coordinatorMock = new Mock<IGitHubSyncCoordinator>();
        coordinatorMock
            .Setup(x => x.TryAcquire(It.IsAny<int>()))
            .Throws(new SyncCheckpointChangedException("INTERNAL_SQL_SECRET_TOKEN_STACK"));

        using var client = CreateClientWithSyncCoordinator(coordinatorMock.Object);
        var projectId = await CreateAuthenticatedProjectAsync(client);

        var response = await client.PostAsync($"/api/projects/{projectId}/github/sync", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("INTERNAL_SQL_SECRET_TOKEN_STACK", body);

        var problem = JsonSerializer.Deserialize<ProblemDetails>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(problem);
        Assert.Equal(409, problem.Status);
        Assert.Equal(ApiErrorCodes.SyncCheckpointChanged, GetErrorCode(problem));
    }

    [Fact]
    public async Task SyncGitHub_IncompleteHistoryException_Returns502ProblemDetailsWithoutLeakingInternalDetails()
    {
        var coordinatorMock = new Mock<IGitHubSyncCoordinator>();
        coordinatorMock
            .Setup(x => x.TryAcquire(It.IsAny<int>()))
            .Throws(new IncompleteHistoryException(
                IncompleteHistoryKind.UnresolvedParents,
                "SECRET_INTERNAL_HASH_OR_STACK",
                new HashSet<string> { "deadbeef1234" }));

        using var client = CreateClientWithSyncCoordinator(coordinatorMock.Object);
        var projectId = await CreateAuthenticatedProjectAsync(client);

        var response = await client.PostAsync($"/api/projects/{projectId}/github/sync", content: null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET_INTERNAL_HASH_OR_STACK", body);
        Assert.DoesNotContain("deadbeef1234", body);

        var problem = JsonSerializer.Deserialize<ProblemDetails>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(problem);
        Assert.Equal(502, problem.Status);
        Assert.Equal(ApiErrorCodes.GitHubIncompleteHistory, GetErrorCode(problem));
    }

    [Theory]
    [InlineData("SyncAlreadyInProgress", HttpStatusCode.Conflict, ApiErrorCodes.SyncAlreadyInProgress)]
    [InlineData("GitHubRateLimited", HttpStatusCode.BadGateway, ApiErrorCodes.GitHubRateLimited)]
    [InlineData("GitHubRepoNotFound", HttpStatusCode.NotFound, ApiErrorCodes.GitHubRepoNotFound)]
    [InlineData("GitHubTokenRequired", HttpStatusCode.BadRequest, ApiErrorCodes.GitHubTokenRequired)]
    [InlineData("ArgumentException", HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRepoUrl)]
    public async Task SyncGitHub_ExistingExceptions_PreserveContractAndProblemDetails(
        string exceptionCase,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        Exception exceptionToThrow = exceptionCase switch
        {
            "SyncAlreadyInProgress" => new SyncAlreadyInProgressException("Синхронизация уже выполняется."),
            "GitHubRateLimited" => new GitHubIntegrationException(GitHubErrorKind.RateLimited, "Rate limit exceeded."),
            "GitHubRepoNotFound" => new GitHubIntegrationException(GitHubErrorKind.RepoNotFound, "Repository not found."),
            "GitHubTokenRequired" => new GitHubIntegrationException(GitHubErrorKind.TokenRequired, "Token required."),
            "ArgumentException" => new ArgumentException("Invalid repository url."),
            _ => throw new InvalidOperationException($"Unknown case: {exceptionCase}")
        };

        var coordinatorMock = new Mock<IGitHubSyncCoordinator>();
        coordinatorMock
            .Setup(x => x.TryAcquire(It.IsAny<int>()))
            .Throws(exceptionToThrow);

        using var client = CreateClientWithSyncCoordinator(coordinatorMock.Object);
        var projectId = await CreateAuthenticatedProjectAsync(client);

        var response = await client.PostAsync($"/api/projects/{projectId}/github/sync", content: null);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal((int)expectedStatus, problem.Status);
        Assert.Equal(expectedCode, GetErrorCode(problem));
    }

    private HttpClient CreateClientWithSyncCoordinator(IGitHubSyncCoordinator coordinator)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IGitHubSyncCoordinator>();
                services.AddScoped(_ => coordinator);
            });
        }).CreateClient();
    }

    private static async Task<int> CreateAuthenticatedProjectAsync(HttpClient client)
    {
        var username = $"sync_user_{Guid.NewGuid():N}"[..20];
        var registerResponse = await client.PostAsJsonAsync("/api/auth/register", new RegisterDto
        {
            Username = username,
            Password = "StrongPassword123!"
        });
        registerResponse.EnsureSuccessStatusCode();

        var auth = await registerResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        Assert.NotNull(auth);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var projectResponse = await client.PostAsJsonAsync("/api/projects", new CreateProjectDto
        {
            Name = $"Proj_{Guid.NewGuid():N}"[..16],
            RepoUrl = "https://github.com/octocat/Hello-World"
        });
        projectResponse.EnsureSuccessStatusCode();

        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(project);

        return project.Id;
    }

    private static string? GetErrorCode(ProblemDetails problem)
    {
        if (!problem.Extensions.TryGetValue("code", out var value) || value is null)
        {
            return null;
        }

        return value is JsonElement jsonElement ? jsonElement.GetString() : value.ToString();
    }
}
