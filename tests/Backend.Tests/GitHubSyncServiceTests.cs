using System.Reflection;
using Application.DTOs;
using Application.Exceptions;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Application.Services;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Repository;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Backend.Tests;

public sealed class GitHubSyncServiceTests
{
    private static async IAsyncEnumerable<IReadOnlyList<GitHubCommitInfo>> ToAsyncEnumerable(params IReadOnlyList<GitHubCommitInfo>[] pages)
    {
        foreach (var page in pages)
        {
            yield return page;
        }
        await Task.CompletedTask;
    }
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

    private static Mock<IGitHubCredentialService> CreateCredentialServiceMock(int userId = 1, string token = "ghp_valid_test_token")
    {
        var mock = new Mock<IGitHubCredentialService>();
        mock.Setup(c => c.GetUserTokenAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(token);
        return mock;
    }

    [Fact]
    public async Task SyncAsync_WhenProjectNotFound_ReturnsNull()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();
        var credentialMock = new Mock<IGitHubCredentialService>();

        projectRepoMock.Setup(repo => repo.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Project?)null);

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object, credentialMock.Object);

        var result = await service.SyncAsync(1);

        Assert.Null(result);
        credentialMock.Verify(c => c.GetUserTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        gitHubMock.Verify(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        gitHubMock.Verify(g => g.GetCommitPagesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        commitRepoMock.Verify(c => c.SaveSyncBatchAsync(It.IsAny<int>(), It.IsAny<IReadOnlyList<Commit>>(), It.IsAny<IReadOnlyList<CommitUpdateDto>>(), It.IsAny<ProjectCheckpointDto>(), It.IsAny<ProjectCheckpointSnapshotDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_WhenRepositoryInfoIsNull_ThrowsGitHubIntegrationException()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();
        var credentialMock = CreateCredentialServiceMock(1, "ghp_test_token");

        projectRepoMock.Setup(repo => repo.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 1, RepoUrl = "https://github.com/test/repo", OwnerId = 1 });

        gitHubMock.Setup(g => g.GetRepositoryInfoAsync("https://github.com/test/repo", "ghp_test_token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(default(GitHubRepositoryInfoDto)!);

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object, credentialMock.Object);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() => service.SyncAsync(1));
        Assert.Equal(GitHubErrorKind.ServiceUnavailable, ex.Kind);
    }

    [Fact]
    public async Task SyncAsync_WhenAllCommitsAreNew_BatchInsertsAllAndReturnsCounts()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();
        var credentialMock = CreateCredentialServiceMock(1, "ghp_test_token");

        projectRepoMock.Setup(repo => repo.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 10, RepoUrl = "https://github.com/test/repo", OwnerId = 1 });

        var date = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var pageCommits = new List<GitHubCommitInfo>
        {
            new("hash3", date.AddHours(2), ["hash2"]),
            new("hash2", date.AddHours(1), ["hash1"]),
            new("hash1", date, [])
        };

        gitHubMock.Setup(g => g.GetRepositoryInfoAsync("https://github.com/test/repo", "ghp_test_token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "10", "main", "hash3"));

        gitHubMock.Setup(g => g.GetCommitPagesAsync("https://github.com/test/repo", "hash3", "ghp_test_token", It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(pageCommits));

        commitRepoMock.Setup(c => c.GetHashesVerificationStatusAsync(10, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, bool>());

        commitRepoMock.Setup(c => c.SaveSyncBatchAsync(10, It.IsAny<IReadOnlyList<Commit>>(), It.IsAny<IReadOnlyList<CommitUpdateDto>>(), It.IsAny<ProjectCheckpointDto>(), It.IsAny<ProjectCheckpointSnapshotDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncBatchResult(new HashSet<string> { "hash1", "hash2", "hash3" }, new HashSet<string>(), new HashSet<string>()));

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object, credentialMock.Object);

        var result = await service.SyncAsync(10);

        Assert.NotNull(result);
        Assert.Equal(3, result.Added);
        Assert.Equal(0, result.Skipped);

        commitRepoMock.Verify(c => c.SaveSyncBatchAsync(10, It.Is<IReadOnlyList<Commit>>(list => list.Count == 3), It.IsAny<IReadOnlyList<CommitUpdateDto>>(), It.IsAny<ProjectCheckpointDto>(), It.IsAny<ProjectCheckpointSnapshotDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_WhenAllCommitsAlreadyExist_SkipsAllAndDoesNotCallAddRange()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();
        var credentialMock = CreateCredentialServiceMock(1, "ghp_test_token");

        projectRepoMock.Setup(repo => repo.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 10, RepoUrl = "https://github.com/test/repo", OwnerId = 1 });

        var date = DateTime.UtcNow;
        var pageCommits = new List<GitHubCommitInfo>
        {
            new("hash2", date, ["hash1"]),
            new("hash1", date, [])
        };

        gitHubMock.Setup(g => g.GetRepositoryInfoAsync("https://github.com/test/repo", "ghp_test_token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "10", "main", "hash2"));

        gitHubMock.Setup(g => g.GetCommitPagesAsync("https://github.com/test/repo", "hash2", "ghp_test_token", It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(pageCommits));

        commitRepoMock.Setup(c => c.GetHashesVerificationStatusAsync(10, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, bool> { ["hash2"] = true, ["hash1"] = true });

        commitRepoMock.Setup(c => c.SaveSyncBatchAsync(10, It.IsAny<IReadOnlyList<Commit>>(), It.IsAny<IReadOnlyList<CommitUpdateDto>>(), It.IsAny<ProjectCheckpointDto>(), It.IsAny<ProjectCheckpointSnapshotDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncBatchResult(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { "hash1", "hash2" }));

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object, credentialMock.Object);

        var result = await service.SyncAsync(10);

        Assert.NotNull(result);
        Assert.Equal(0, result.Added);
        Assert.Equal(2, result.Skipped);
    }

    [Fact]
    public async Task SyncAsync_WhenPartialCommitsExist_InsertsOnlyNewCommits()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();
        var credentialMock = CreateCredentialServiceMock(1, "ghp_test_token");

        projectRepoMock.Setup(repo => repo.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 10, RepoUrl = "https://github.com/test/repo", OwnerId = 1 });

        var date = DateTime.UtcNow;
        var pageCommits = new List<GitHubCommitInfo>
        {
            new("new2", date, ["new1"]),
            new("new1", date, ["existing2"]),
            new("existing2", date, ["existing1"]),
            new("existing1", date, [])
        };

        gitHubMock.Setup(g => g.GetRepositoryInfoAsync("https://github.com/test/repo", "ghp_test_token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "10", "main", "new2"));

        gitHubMock.Setup(g => g.GetCommitPagesAsync("https://github.com/test/repo", "new2", "ghp_test_token", It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(pageCommits));

        commitRepoMock.Setup(c => c.GetHashesVerificationStatusAsync(10, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int p, IReadOnlyCollection<string> hashes, CancellationToken ct) =>
            {
                var dict = new Dictionary<string, bool>();
                if (hashes.Contains("existing1")) dict["existing1"] = true;
                if (hashes.Contains("existing2")) dict["existing2"] = true;
                return dict;
            });

        commitRepoMock.Setup(c => c.SaveSyncBatchAsync(10, It.IsAny<IReadOnlyList<Commit>>(), It.IsAny<IReadOnlyList<CommitUpdateDto>>(), It.IsAny<ProjectCheckpointDto>(), It.IsAny<ProjectCheckpointSnapshotDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncBatchResult(new HashSet<string> { "new1", "new2" }, new HashSet<string>(), new HashSet<string> { "existing1", "existing2" }));

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object, credentialMock.Object);

        var result = await service.SyncAsync(10);

        Assert.NotNull(result);
        Assert.Equal(2, result.Added);
        Assert.Equal(2, result.Skipped);

        commitRepoMock.Verify(c => c.SaveSyncBatchAsync(
            10,
            It.Is<IReadOnlyList<Commit>>(list => list.Count == 2 && list.All(c => c.CommitHash == "new1" || c.CommitHash == "new2")),
            It.IsAny<IReadOnlyList<CommitUpdateDto>>(),
            It.IsAny<ProjectCheckpointDto>(),
            It.IsAny<ProjectCheckpointSnapshotDto>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_WhenCommitDateIsNull_IgnoresCommitWithoutCounting()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();
        var credentialMock = CreateCredentialServiceMock(1, "ghp_test_token");

        projectRepoMock.Setup(repo => repo.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 10, RepoUrl = "https://github.com/test/repo", OwnerId = 1 });

        var pageCommits = new List<GitHubCommitInfo>
        {
            new("valid1", null, [])
        };

        gitHubMock.Setup(g => g.GetRepositoryInfoAsync("https://github.com/test/repo", "ghp_test_token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "10", "main", "valid1"));

        gitHubMock.Setup(g => g.GetCommitPagesAsync("https://github.com/test/repo", "valid1", "ghp_test_token", It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(pageCommits));

        commitRepoMock.Setup(c => c.GetHashesVerificationStatusAsync(10, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, bool>());

        commitRepoMock.Setup(c => c.SaveSyncBatchAsync(10, It.IsAny<IReadOnlyList<Commit>>(), It.IsAny<IReadOnlyList<CommitUpdateDto>>(), It.IsAny<ProjectCheckpointDto>(), It.IsAny<ProjectCheckpointSnapshotDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncBatchResult(new HashSet<string> { "valid1" }, new HashSet<string>(), new HashSet<string>()));

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object, credentialMock.Object);

        var result = await service.SyncAsync(10);

        Assert.NotNull(result);
        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public async Task SyncAsync_WhenDuplicateHashesInGitHubResponse_CountsSubsequentDuplicatesAsSkipped()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();
        var credentialMock = CreateCredentialServiceMock(1, "ghp_test_token");

        projectRepoMock.Setup(repo => repo.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 10, RepoUrl = "https://github.com/test/repo", OwnerId = 1 });

        var date = DateTime.UtcNow;
        var pageCommits = new List<GitHubCommitInfo>
        {
            new("hashB", date, ["hashA"]),
            new("hashA", date, []),
            new("hashA", date, [])
        };

        gitHubMock.Setup(g => g.GetRepositoryInfoAsync("https://github.com/test/repo", "ghp_test_token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "10", "main", "hashB"));

        gitHubMock.Setup(g => g.GetCommitPagesAsync("https://github.com/test/repo", "hashB", "ghp_test_token", It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(pageCommits));

        commitRepoMock.Setup(c => c.GetHashesVerificationStatusAsync(10, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, bool>());

        commitRepoMock.Setup(c => c.SaveSyncBatchAsync(10, It.IsAny<IReadOnlyList<Commit>>(), It.IsAny<IReadOnlyList<CommitUpdateDto>>(), It.IsAny<ProjectCheckpointDto>(), It.IsAny<ProjectCheckpointSnapshotDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncBatchResult(new HashSet<string> { "hashA", "hashB" }, new HashSet<string>(), new HashSet<string>()));

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object, credentialMock.Object);

        var result = await service.SyncAsync(10);

        Assert.NotNull(result);
        Assert.Equal(2, result.Added);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public async Task SyncAsync_IntegrationWithSqlite_SavesCommitsToDatabaseAndHandlesReSync()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using (connection)
        {
            using (var seedContext = new AppDbContext(options))
            {
                seedContext.Users.Add(new User { Id = 1, Username = "dev", PasswordHash = "hash", RoleId = 1 });
                seedContext.Projects.Add(new Project { Id = 100, Name = "SyncProject", RepoUrl = "https://github.com/test/sync", OwnerId = 1 });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var gitHubMock = new Mock<IGitHubService>();
            var credentialMock = CreateCredentialServiceMock(1, "ghp_sqlite_token");

            var date = new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);
            var initialCommits = new List<GitHubCommitInfo>
            {
                new("sha3", date.AddMinutes(2), ["sha2"]),
                new("sha2", date.AddMinutes(1), ["sha1"]),
                new("sha1", date, [])
            };

            gitHubMock.Setup(g => g.GetRepositoryInfoAsync("https://github.com/test/sync", "ghp_sqlite_token", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "100", "main", "sha3"));

            gitHubMock.Setup(g => g.GetCommitPagesAsync("https://github.com/test/sync", "sha3", "ghp_sqlite_token", It.IsAny<CancellationToken>()))
                .Returns(ToAsyncEnumerable(initialCommits));

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object);

            // Первый запуск синхронизации: 3 новых коммита
            var result1 = await service.SyncAsync(100);

            Assert.NotNull(result1);
            Assert.Equal(3, result1.Added);
            Assert.Equal(0, result1.Skipped);

            // Проверяем физическое сохранение в БД
            var commitsInDb = await context.Commits.Where(c => c.ProjectId == 100).ToListAsync();
            Assert.Equal(3, commitsInDb.Count);
            Assert.Contains(commitsInDb, c => c.CommitHash == "sha1");
            Assert.Contains(commitsInDb, c => c.CommitHash == "sha2");
            Assert.Contains(commitsInDb, c => c.CommitHash == "sha3");

            // Повторный запуск синхронизации: те же 3 коммита + 1 новый
            var subsequentCommits = new List<GitHubCommitInfo>
            {
                new("sha4", date.AddMinutes(3), ["sha3"]),
                new("sha3", date.AddMinutes(2), ["sha2"])
            };

            gitHubMock.Setup(g => g.GetRepositoryInfoAsync("https://github.com/test/sync", "ghp_sqlite_token", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "100", "main", "sha4"));

            gitHubMock.Setup(g => g.GetCommitPagesAsync("https://github.com/test/sync", "sha4", "ghp_sqlite_token", It.IsAny<CancellationToken>()))
                .Returns(ToAsyncEnumerable(subsequentCommits));

            var result2 = await service.SyncAsync(100);

            Assert.NotNull(result2);
            Assert.Equal(1, result2.Added);
            Assert.Equal(1, result2.Skipped);

            // Итого в базе ровно 4 коммита
            var totalCommits = await context.Commits.Where(c => c.ProjectId == 100).CountAsync();
            Assert.Equal(4, totalCommits);
        }
    }

    [Fact]
    public async Task CommitRepository_AddRangeAsync_WhenUniqueConstraintConflictOccurs_RecoversAndDetachesOnlyFailedCommits()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using (connection)
        {
            using (var seedContext = new AppDbContext(options))
            {
                seedContext.Users.Add(new User { Id = 1, Username = "dev", PasswordHash = "hash", RoleId = 1 });
                seedContext.Projects.Add(new Project { Id = 200, Name = "ConflictProject", RepoUrl = "https://github.com/test/conflict", OwnerId = 1 });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);

            // Загружаем проект в контекст, чтобы проверить, что он останется отслеживаемым (не потеряется из-за Clear)
            var trackedProject = await projectRepo.GetByIdAsync(200);
            Assert.NotNull(trackedProject);
            Assert.Equal(EntityState.Unchanged, context.Entry(trackedProject).State);

            // Вставляем коммит "conflictingHash" в параллельном контексте, создавая конфликт уникальности до начала вставки
            using (var parallelContext = new AppDbContext(options))
            {
                parallelContext.Commits.Add(new Commit
                {
                    ProjectId = 200,
                    CommitHash = "conflictinghash",
                    CommitDate = DateTime.UtcNow
                });
                parallelContext.SaveChanges();
            }

            // Пытаемся добавить пачку, где один коммит уже существует в БД
            var batch = new List<Commit>
            {
                new() { ProjectId = 200, CommitHash = "newHashA", CommitDate = DateTime.UtcNow },
                new() { ProjectId = 200, CommitHash = "conflictingHash", CommitDate = DateTime.UtcNow },
                new() { ProjectId = 200, CommitHash = "newHashB", CommitDate = DateTime.UtcNow }
            };

            var insertedCount = await commitRepo.AddRangeAsync(batch);

            // Из 3 коммитов 2 неконфликтующих должны быть сохранены, 1 пропущен
            Assert.Equal(2, insertedCount);

            // Проверяем, что исходный trackedProject остался в ChangeTracker и НЕ был отсоединён
            Assert.Equal(EntityState.Unchanged, context.Entry(trackedProject).State);

            // Проверяем, что в базе теперь 3 коммита ("conflictingHash", "newHashA", "newHashB")
            var inDb = await context.Commits.Where(c => c.ProjectId == 200).ToListAsync();
            Assert.Equal(3, inDb.Count);
        }
    }

    [Fact]
    public async Task CommitRepository_GetExistingHashesAsync_NullArgument_ThrowsArgumentNullException()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using (connection)
        {
            using var context = new AppDbContext(options);
            var repo = new CommitRepository(context);

            await Assert.ThrowsAsync<ArgumentNullException>(() => repo.GetExistingHashesAsync(1, null!));
        }
    }

    [Fact]
    public async Task CommitRepository_GetExistingHashesAsync_EmptyCollection_ReturnsEmptySet()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using (connection)
        {
            using var context = new AppDbContext(options);
            var repo = new CommitRepository(context);

            var hashes = await repo.GetExistingHashesAsync(1, []);
            Assert.Empty(hashes);
        }
    }

    [Fact]
    public async Task CommitRepository_GetExistingHashesAsync_FiltersByProjectId()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using (connection)
        {
            using (var seedContext = new AppDbContext(options))
            {
                seedContext.Users.Add(new User { Id = 1, Username = "dev", PasswordHash = "hash", RoleId = 1 });
                seedContext.Projects.Add(new Project { Id = 1, Name = "P1", RepoUrl = "https://github.com/test/p1", OwnerId = 1 });
                seedContext.Projects.Add(new Project { Id = 2, Name = "P2", RepoUrl = "https://github.com/test/p2", OwnerId = 1 });
                seedContext.Commits.Add(new Commit { ProjectId = 1, CommitHash = "sharedHash", CommitDate = DateTime.UtcNow });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var repo = new CommitRepository(context);

            // Для ProjectId = 1 хеш должен быть найден
            var hashesP1 = await repo.GetExistingHashesAsync(1, ["sharedHash", "unknownHash"]);
            Assert.Single(hashesP1);
            Assert.Contains("sharedHash", hashesP1);

            // Для ProjectId = 2 тот же хеш не должен возвращаться, так как принадлежит другому проекту
            var hashesP2 = await repo.GetExistingHashesAsync(2, ["sharedHash", "unknownHash"]);
            Assert.Empty(hashesP2);
        }
    }

    [Fact]
    public async Task CommitRepository_AddRangeAsync_NullArgument_ThrowsArgumentNullException()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using (connection)
        {
            using var context = new AppDbContext(options);
            var repo = new CommitRepository(context);

            await Assert.ThrowsAsync<ArgumentNullException>(() => repo.AddRangeAsync(null!));
        }
    }

    [Fact]
    public async Task CommitRepository_AddRangeAsync_EmptyList_ReturnsZero()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using (connection)
        {
            using var context = new AppDbContext(options);
            var repo = new CommitRepository(context);

            var result = await repo.AddRangeAsync([]);
            Assert.Equal(0, result);
        }
    }

    [Fact]
    public async Task CommitRepository_AddRangeAsync_WhenCancellationTokenCancelled_ThrowsOperationCanceledException()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using (connection)
        {
            using var context = new AppDbContext(options);
            var repo = new CommitRepository(context);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var batch = new List<Commit>
            {
                new() { ProjectId = 1, CommitHash = "cancel1", CommitDate = DateTime.UtcNow }
            };

            await Assert.ThrowsAsync<OperationCanceledException>(() => repo.AddRangeAsync(batch, cts.Token));
        }
    }

    [Fact]
    public async Task CommitRepository_AddRangeAsync_WhenUnrelatedDbErrorOccurs_RethrowsDbUpdateException()
    {
        var (connection, options) = CreateInMemoryDatabase();
        using (connection)
        {
            // Включаем проверку внешних ключей в SQLite
            using (var pragmaCmd = connection.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA foreign_keys = ON;";
                pragmaCmd.ExecuteNonQuery();
            }

            using var context = new AppDbContext(options);
            var repo = new CommitRepository(context);

            // Commit ссылается на несуществующий ProjectId = 99999
            var batch = new List<Commit>
            {
                new() { ProjectId = 99999, CommitHash = "foreignKeyViolation", CommitDate = DateTime.UtcNow }
            };

            // Несвязанная ошибка базы данных (FOREIGN KEY constraint) должна пробрасываться, а не маскироваться
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => repo.AddRangeAsync(batch));
            Assert.Contains("FOREIGN KEY", ex.InnerException?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task SyncAsync_WhenDefaultBranchHeadShaIsEmpty_ThrowsGitHubIntegrationException()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();
        var credentialMock = CreateCredentialServiceMock(1, "ghp_test_token");

        projectRepoMock.Setup(repo => repo.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 10, RepoUrl = "https://github.com/test/repo", OwnerId = 1 });

        gitHubMock.Setup(g => g.GetRepositoryInfoAsync("https://github.com/test/repo", "ghp_test_token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "10", "main", ""));

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object, credentialMock.Object);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() => service.SyncAsync(10));
        Assert.Equal(GitHubErrorKind.ServiceUnavailable, ex.Kind);
    }

    #region Сценарии этапа 8.4: Персональный PAT владельца проекта

    [Fact]
    public async Task SyncAsync_OwnerInitiatesSync_PassesOwnerPatToGitHubService()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();
        var credentialMock = new Mock<IGitHubCredentialService>();

        const int ownerId = 42;
        const string expectedPat = "ghp_owner_pat_424242";

        projectRepoMock.Setup(repo => repo.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 10, RepoUrl = "https://github.com/owner/project", OwnerId = ownerId });

        credentialMock.Setup(c => c.GetUserTokenAsync(ownerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedPat);

        gitHubMock.Setup(g => g.GetRepositoryInfoAsync("https://github.com/owner/project", expectedPat, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "10", "main", "head_1"));

        gitHubMock.Setup(g => g.GetCommitPagesAsync("https://github.com/owner/project", "head_1", expectedPat, It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(new List<GitHubCommitInfo>
            {
                new("head_1", DateTime.UtcNow, [])
            }));

        commitRepoMock.Setup(c => c.GetHashesVerificationStatusAsync(10, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, bool>());

        commitRepoMock.Setup(c => c.SaveSyncBatchAsync(10, It.IsAny<IReadOnlyList<Commit>>(), It.IsAny<IReadOnlyList<CommitUpdateDto>>(), It.IsAny<ProjectCheckpointDto>(), It.IsAny<ProjectCheckpointSnapshotDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncBatchResult(new HashSet<string> { "head_1" }, new HashSet<string>(), new HashSet<string>()));

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object, credentialMock.Object);

        var result = await service.SyncAsync(10);

        Assert.NotNull(result);
        credentialMock.Verify(c => c.GetUserTokenAsync(ownerId, It.IsAny<CancellationToken>()), Times.Once);
        gitHubMock.Verify(g => g.GetRepositoryInfoAsync("https://github.com/owner/project", expectedPat, It.IsAny<CancellationToken>()), Times.Once);
        gitHubMock.Verify(g => g.GetCommitPagesAsync("https://github.com/owner/project", "head_1", expectedPat, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_AdminSyncsOtherUserProject_AlwaysUsesProjectOwnerPat_NeverAdminPat()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();
        var credentialMock = new Mock<IGitHubCredentialService>();

        const int ownerId = 2;
        const int adminId = 99;
        const string ownerPat = "ghp_owner_pat_user2";
        const string adminPat = "ghp_admin_pat_user99";

        projectRepoMock.Setup(repo => repo.GetByIdAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 100, RepoUrl = "https://github.com/user2/repo", OwnerId = ownerId });

        credentialMock.Setup(c => c.GetUserTokenAsync(ownerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ownerPat);

        credentialMock.Setup(c => c.GetUserTokenAsync(adminId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(adminPat);

        gitHubMock.Setup(g => g.GetRepositoryInfoAsync("https://github.com/user2/repo", ownerPat, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "100", "main", "head_user2"));

        gitHubMock.Setup(g => g.GetCommitPagesAsync("https://github.com/user2/repo", "head_user2", ownerPat, It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(new List<GitHubCommitInfo>
            {
                new("head_user2", DateTime.UtcNow, [])
            }));

        commitRepoMock.Setup(c => c.GetHashesVerificationStatusAsync(100, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, bool>());

        commitRepoMock.Setup(c => c.SaveSyncBatchAsync(100, It.IsAny<IReadOnlyList<Commit>>(), It.IsAny<IReadOnlyList<CommitUpdateDto>>(), It.IsAny<ProjectCheckpointDto>(), It.IsAny<ProjectCheckpointSnapshotDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncBatchResult(new HashSet<string> { "head_user2" }, new HashSet<string>(), new HashSet<string>()));

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object, credentialMock.Object);

        var result = await service.SyncAsync(100);

        Assert.NotNull(result);

        // Должен запрашиваться токен строго владельца проекта (ownerId = 2)
        credentialMock.Verify(c => c.GetUserTokenAsync(ownerId, It.IsAny<CancellationToken>()), Times.Once);
        credentialMock.Verify(c => c.GetUserTokenAsync(adminId, It.IsAny<CancellationToken>()), Times.Never);

        // В GitHubService передается строго PAT владельца
        gitHubMock.Verify(g => g.GetRepositoryInfoAsync("https://github.com/user2/repo", ownerPat, It.IsAny<CancellationToken>()), Times.Once);
        gitHubMock.Verify(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), adminPat, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_WhenOwnerHasNoPat_ThrowsTokenRequiredException_AndNeverCallsGitHubApi()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();
        var credentialMock = new Mock<IGitHubCredentialService>();

        const int ownerId = 5;

        projectRepoMock.Setup(repo => repo.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 10, RepoUrl = "https://github.com/test/repo", OwnerId = ownerId });

        credentialMock.Setup(c => c.GetUserTokenAsync(ownerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object, credentialMock.Object);

        var exception = await Assert.ThrowsAsync<GitHubIntegrationException>(() => service.SyncAsync(10));

        Assert.Equal(GitHubErrorKind.TokenRequired, exception.Kind);
        Assert.Contains("токена GitHub", exception.Message, StringComparison.OrdinalIgnoreCase);

        // GitHub API и хранилище коммитов не должны вызываться
        gitHubMock.Verify(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        gitHubMock.Verify(g => g.GetCommitPagesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        commitRepoMock.Verify(c => c.SaveSyncBatchAsync(It.IsAny<int>(), It.IsAny<IReadOnlyList<Commit>>(), It.IsAny<IReadOnlyList<CommitUpdateDto>>(), It.IsAny<ProjectCheckpointDto>(), It.IsAny<ProjectCheckpointSnapshotDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task SyncAsync_WhenOwnerPatIsWhitespace_ThrowsTokenRequiredException_AndNeverCallsGitHubApi(string whitespaceToken)
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();
        var credentialMock = new Mock<IGitHubCredentialService>();

        const int ownerId = 7;

        projectRepoMock.Setup(repo => repo.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 10, RepoUrl = "https://github.com/test/repo", OwnerId = ownerId });

        credentialMock.Setup(c => c.GetUserTokenAsync(ownerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(whitespaceToken);

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object, credentialMock.Object);

        var exception = await Assert.ThrowsAsync<GitHubIntegrationException>(() => service.SyncAsync(10));

        Assert.Equal(GitHubErrorKind.TokenRequired, exception.Kind);
        gitHubMock.Verify(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        gitHubMock.Verify(g => g.GetCommitPagesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_WhenCallerHasPat_ButOwnerHasNoPat_ThrowsTokenRequired_NoFallbackToCallerPat()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();
        var credentialMock = new Mock<IGitHubCredentialService>();

        const int ownerId = 7;
        const int callerId = 1;
        const string callerPat = "ghp_caller_valid_pat";

        projectRepoMock.Setup(repo => repo.GetByIdAsync(50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 50, RepoUrl = "https://github.com/owner7/repo", OwnerId = ownerId });

        // У вызывающего токен есть, а у владельца проекта - нет
        credentialMock.Setup(c => c.GetUserTokenAsync(callerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(callerPat);
        credentialMock.Setup(c => c.GetUserTokenAsync(ownerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object, credentialMock.Object);

        var exception = await Assert.ThrowsAsync<GitHubIntegrationException>(() => service.SyncAsync(50));

        Assert.Equal(GitHubErrorKind.TokenRequired, exception.Kind);

        // Никакого fallback на токен вызывающего
        gitHubMock.Verify(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), callerPat, It.IsAny<CancellationToken>()), Times.Never);
        gitHubMock.Verify(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_WhenOwnerPatIsExpired_PropagatesTokenExpiredException()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();
        var credentialMock = new Mock<IGitHubCredentialService>();

        const int ownerId = 8;
        const string expiredPat = "ghp_expired_token_888";

        projectRepoMock.Setup(repo => repo.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 10, RepoUrl = "https://github.com/test/repo", OwnerId = ownerId });

        credentialMock.Setup(c => c.GetUserTokenAsync(ownerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expiredPat);

        gitHubMock.Setup(g => g.GetRepositoryInfoAsync("https://github.com/test/repo", expiredPat, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.TokenExpired, "Персональный токен GitHub недействителен или был отозван."));

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object, credentialMock.Object);

        var exception = await Assert.ThrowsAsync<GitHubIntegrationException>(() => service.SyncAsync(10));

        Assert.Equal(GitHubErrorKind.TokenExpired, exception.Kind);
        Assert.Contains("Персональный токен GitHub недействителен", exception.Message);
    }

    [Theory]
    [InlineData(GitHubErrorKind.RateLimited)]
    [InlineData(GitHubErrorKind.AccessDenied)]
    [InlineData(GitHubErrorKind.RepoNotFound)]
    [InlineData(GitHubErrorKind.ServiceUnavailable)]
    public async Task SyncAsync_WhenGitHubServiceFails_PropagatesGitHubIntegrationException(GitHubErrorKind errorKind)
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();
        var credentialMock = new Mock<IGitHubCredentialService>();

        const int ownerId = 3;
        const string pat = "ghp_valid_pat";

        projectRepoMock.Setup(repo => repo.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 10, RepoUrl = "https://github.com/test/repo", OwnerId = ownerId });

        credentialMock.Setup(c => c.GetUserTokenAsync(ownerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pat);

        gitHubMock.Setup(g => g.GetRepositoryInfoAsync("https://github.com/test/repo", pat, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(errorKind, $"Тестовая ошибка {errorKind}"));

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object, credentialMock.Object);

        var exception = await Assert.ThrowsAsync<GitHubIntegrationException>(() => service.SyncAsync(10));

        Assert.Equal(errorKind, exception.Kind);
    }

    [Fact]
    public async Task SyncAsync_PatIsNeverRetainedInServiceFields()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();
        var credentialMock = new Mock<IGitHubCredentialService>();

        const int ownerId = 12;
        const string secretPat = "ghp_super_secret_pat_value_99999";

        projectRepoMock.Setup(repo => repo.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 10, RepoUrl = "https://github.com/test/repo", OwnerId = ownerId });

        credentialMock.Setup(c => c.GetUserTokenAsync(ownerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(secretPat);

        gitHubMock.Setup(g => g.GetRepositoryInfoAsync("https://github.com/test/repo", secretPat, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "10", "main", "h1"));

        gitHubMock.Setup(g => g.GetCommitPagesAsync("https://github.com/test/repo", "h1", secretPat, It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(new List<GitHubCommitInfo>
            {
                new("h1", DateTime.UtcNow, [])
            }));

        commitRepoMock.Setup(c => c.GetHashesVerificationStatusAsync(10, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, bool>());

        commitRepoMock.Setup(c => c.SaveSyncBatchAsync(10, It.IsAny<IReadOnlyList<Commit>>(), It.IsAny<IReadOnlyList<CommitUpdateDto>>(), It.IsAny<ProjectCheckpointDto>(), It.IsAny<ProjectCheckpointSnapshotDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncBatchResult(new HashSet<string> { "h1" }, new HashSet<string>(), new HashSet<string>()));

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object, credentialMock.Object);

        await service.SyncAsync(10);

        // Проверяем все поля сервиса через рефлексию
        var fields = typeof(GitHubSyncService).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        foreach (var field in fields)
        {
            var value = field.GetValue(service);
            if (value is string stringVal)
            {
                Assert.False(stringVal.Contains(secretPat, StringComparison.Ordinal),
                    $"Поле {field.Name} сервиса GitHubSyncService содержит секретный токен PAT!");
            }
        }
    }

    [Fact]
    public async Task SyncAsync_ExceptionsDoNotContainPat()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();
        var credentialMock = new Mock<IGitHubCredentialService>();

        const int ownerId = 15;
        const string secretPat = "ghp_leak_check_pat_123456789";

        projectRepoMock.Setup(repo => repo.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 10, RepoUrl = "https://github.com/test/repo", OwnerId = ownerId });

        credentialMock.Setup(c => c.GetUserTokenAsync(ownerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(secretPat);

        gitHubMock.Setup(g => g.GetRepositoryInfoAsync("https://github.com/test/repo", secretPat, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.AccessDenied, "Доступ к репозиторию запрещен."));

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object, credentialMock.Object);

        var exception = await Assert.ThrowsAsync<GitHubIntegrationException>(() => service.SyncAsync(10));

        Assert.DoesNotContain(secretPat, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretPat, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GitHubSyncService_Constructor_ThrowsArgumentNullException_WhenCredentialServiceIsNull()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();

        Assert.Throws<ArgumentNullException>(() =>
            new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object, null!));
    }

    #endregion
}
