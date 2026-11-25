using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Application.DTOs;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Application.Services;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Backend.Tests.Integration;

public sealed class CommitValidationAndNormalizationIntegrationTests(ApiWebApplicationFactory factory)
    : IClassFixture<ApiWebApplicationFactory>, IAsyncLifetime
{
    private readonly ApiWebApplicationFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        _factory.ResetMocks();
        await _factory.ResetDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("{\"commitHash\":\"abcdef1234567\"}")]
    [InlineData("{\"commitHash\":\"abcdef1234567\",\"commitDate\":\"2026-01-01T12:00:00\"}")]
    [InlineData("{\"commitHash\":\"abcdef1234567\",\"commitDate\":\"not-a-date\"}")]
    public async Task CreateCommit_MissingAmbiguousOrInvalidDate_Returns400BadRequest(string jsonBody)
    {
        var projectId = await CreateAuthenticatedProjectAsync();

        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync($"/api/projects/{projectId}/commits", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("2026-01-01T12:00:00Z")]
    [InlineData("2026-01-01T15:00:00+03:00")]
    [InlineData("2026-01-01T07:00:00-05:00")]
    public async Task CreateCommit_ValidDateWithUtcOrOffset_PersistsExactUtcMomentInPostgreSql(string rawDate)
    {
        var projectId = await CreateAuthenticatedProjectAsync();
        var jsonBody = $"{{\"commitHash\":\"abcdef1234567\",\"commitDate\":\"{rawDate}\"}}";

        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync($"/api/projects/{projectId}/commits", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<CommitDto>();
        Assert.NotNull(created);

        var expectedUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expectedUtc, created.CommitDate.ToUniversalTime());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Commits.AsNoTracking().SingleAsync(c => c.Id == created.Id);
        Assert.Equal(DateTimeKind.Utc, stored.CommitDate.Kind);
        Assert.Equal(expectedUtc, stored.CommitDate);
    }

    [Theory]
    [InlineData("ABCDEF1234567", "abcdef1234567")]
    [InlineData("AbCdEf1234567", "abcdef1234567")]
    public async Task CreateCommit_UppercaseOrMixedHash_PersistsLowercaseAndDetectsCaseInsensitiveDuplicate(
        string inputHash,
        string expectedHash)
    {
        var projectId = await CreateAuthenticatedProjectAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/projects/{projectId}/commits", new CreateCommitDto
        {
            CommitHash = inputHash,
            CommitDate = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
        });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<CommitDto>();
        Assert.NotNull(created);
        Assert.Equal(expectedHash, created.CommitHash);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Commits.AsNoTracking().SingleAsync(c => c.Id == created.Id);
        Assert.Equal(expectedHash, stored.CommitHash);

        var duplicateResponse = await _client.PostAsJsonAsync($"/api/projects/{projectId}/commits", new CreateCommitDto
        {
            CommitHash = expectedHash,
            CommitDate = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
        });

        Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);
    }

    [Fact]
    public async Task CommitRepository_AddRangeAsync_NormalizesHashesAndSkipsCaseInsensitiveDuplicates()
    {
        var projectId = await CreateAuthenticatedProjectAsync();

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICommitRepository>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await repo.AddAsync(new Commit
        {
            ProjectId = projectId,
            CommitHash = "AAAAAAA1111111",
            CommitDate = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
        });

        var inserted = await repo.AddRangeAsync([
            new Commit
            {
                ProjectId = projectId,
                CommitHash = "aaaaaaa1111111",
                CommitDate = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
            },
            new Commit
            {
                ProjectId = projectId,
                CommitHash = "BBBBBBB2222222",
                CommitDate = new DateTime(2026, 1, 1, 12, 5, 0, DateTimeKind.Utc)
            }
        ]);

        Assert.Equal(1, inserted);

        var hashes = await db.Commits
            .AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .OrderBy(c => c.CommitHash)
            .Select(c => c.CommitHash)
            .ToListAsync();

        Assert.Equal(["aaaaaaa1111111", "bbbbbbb2222222"], hashes);
    }

    [Fact]
    public async Task CommitRepository_SaveSyncBatchAsync_NormalizesNewCommitsAndCommitUpdateDto()
    {
        var projectId = await CreateAuthenticatedProjectAsync();

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICommitRepository>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var result = await repo.SaveSyncBatchAsync(
            projectId,
            newCommits: [
                new Commit
                {
                    ProjectId = projectId,
                    CommitHash = "CCCCCCC3333333",
                    CommitDate = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                    ParentHashes = [],
                    IsVerified = true
                }
            ],
            existingToUpdate: [
                new CommitUpdateDto("DdDdDdD4444444", [])
            ],
            checkpoint: new ProjectCheckpointDto("ccccccc3333333", "main", "1", "github.com", DateTime.UtcNow),
            expectedCheckpoint: new ProjectCheckpointSnapshotDto(null, null, null, null));

        Assert.Equal(2, result.InsertedHashes.Count);

        var hashes = await db.Commits
            .AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .OrderBy(c => c.CommitHash)
            .Select(c => c.CommitHash)
            .ToListAsync();

        Assert.Equal(["ccccccc3333333", "ddddddd4444444"], hashes);
    }

    [Fact]
    public async Task CommitRepository_SaveSyncBatchAsync_ExistingLowercaseCommit_FoundByUppercaseCommitUpdateDtoAndUpdated()
    {
        var projectId = await CreateAuthenticatedProjectAsync();

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICommitRepository>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await repo.AddAsync(new Commit
        {
            ProjectId = projectId,
            CommitHash = "abcdef1234567",
            CommitDate = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            ParentHashes = [],
            IsVerified = false
        });

        var result = await repo.SaveSyncBatchAsync(
            projectId,
            newCommits: [],
            existingToUpdate: [
                new CommitUpdateDto("ABCDEF1234567", ["1111111abcdef"])
            ],
            checkpoint: new ProjectCheckpointDto("abcdef1234567", "main", "1", "github.com", DateTime.UtcNow),
            expectedCheckpoint: new ProjectCheckpointSnapshotDto(null, null, null, null));

        Assert.Empty(result.InsertedHashes);
        Assert.Single(result.UpdatedHashes);
        Assert.Contains("abcdef1234567", result.UpdatedHashes);

        var commitsInDb = await db.Commits
            .AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .ToListAsync();

        Assert.Single(commitsInDb);
        Assert.Equal("abcdef1234567", commitsInDb[0].CommitHash);
        Assert.True(commitsInDb[0].IsVerified);
        Assert.Equal(["1111111abcdef"], commitsInDb[0].ParentHashes);
    }

    [Fact]
    public async Task GitHubSyncService_SyncAsync_PersistsLowercaseCommitHashInPostgreSql()
    {
        var projectId = await CreateAuthenticatedProjectAsync();

        var gitHubServiceMock = new Mock<IGitHubService>();
        gitHubServiceMock
            .Setup(x => x.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubRepositoryInfoDto(
                ApiHost: "github.com",
                RepositoryId: "12345",
                DefaultBranch: "main",
                DefaultBranchHeadSha: "EEEEEEE5555555"));

        gitHubServiceMock
            .Setup(x => x.GetCommitPagesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(SinglePageAsync(
            [
                new GitHubCommitInfo("EEEEEEE5555555", new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), [])
            ]));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var project = await db.Projects.SingleAsync(p => p.Id == projectId);

        var credMock = new Mock<IGitHubCredentialService>();
        credMock.Setup(x => x.GetUserTokenAsync(project.OwnerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token");

        var syncService = new GitHubSyncService(
            gitHubServiceMock.Object,
            scope.ServiceProvider.GetRequiredService<IProjectRepository>(),
            scope.ServiceProvider.GetRequiredService<ICommitRepository>(),
            credMock.Object);

        var syncResult = await syncService.SyncAsync(projectId);

        Assert.NotNull(syncResult);
        Assert.Equal(1, syncResult.Added);

        var stored = await db.Commits.AsNoTracking().SingleAsync(c => c.ProjectId == projectId);
        Assert.Equal("eeeeeee5555555", stored.CommitHash);
    }

    private static async IAsyncEnumerable<IReadOnlyList<GitHubCommitInfo>> SinglePageAsync(IReadOnlyList<GitHubCommitInfo> page)
    {
        yield return page;
        await Task.CompletedTask;
    }

    private async Task<int> CreateAuthenticatedProjectAsync()
    {
        var username = $"commit_user_{Guid.NewGuid():N}"[..20];
        var registerResponse = await _client.PostAsJsonAsync("/api/auth/register", new RegisterDto
        {
            Username = username,
            Password = "StrongPassword123!"
        });
        registerResponse.EnsureSuccessStatusCode();

        var auth = await registerResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        Assert.NotNull(auth);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var projectResponse = await _client.PostAsJsonAsync("/api/projects", new CreateProjectDto
        {
            Name = $"Proj_{Guid.NewGuid():N}"[..16],
            RepoUrl = "https://github.com/octocat/Hello-World"
        });
        projectResponse.EnsureSuccessStatusCode();

        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(project);

        return project.Id;
    }
}
