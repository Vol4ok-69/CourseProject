using System.Data.Common;
using Application.DTOs;
using Application.Enums;
using Application.Exceptions;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Application.Services;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Repository;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Xunit;

namespace Backend.Tests;

public sealed class GitHubSyncDagTraversalTests
{
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

        var user = new User { Id = 1, Username = "testuser", PasswordHash = "hash", RoleId = 2 };
        context.Users.Add(user);

        var project = new Project
        {
            Id = 1,
            Name = "TestRepo",
            RepoUrl = "https://github.com/owner/repo",
            OwnerId = 1
        };
        context.Projects.Add(project);

        context.SaveChanges();

        return (connection, options);
    }

    private static Mock<IGitHubCredentialService> CreateCredentialServiceMock(int userId = 1, string token = "ghp_test_token")
    {
        var mock = new Mock<IGitHubCredentialService>();
        mock.Setup(c => c.GetUserTokenAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(token);
        return mock;
    }

    private static async IAsyncEnumerable<IReadOnlyList<GitHubCommitInfo>> ToAsyncEnumerable(params IReadOnlyList<GitHubCommitInfo>[] pages)
    {
        foreach (var page in pages)
        {
            yield return page;
        }
    }

    [Fact]
    public async Task TR01_HeadAndCheckpointMatch_ReturnsAlreadyCurrent_WithoutRequestingPages()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using (var seedContext = new AppDbContext(options))
            {
                var proj = seedContext.Projects.First();
                proj.LastSyncedCommitHash = "head1";
                proj.LastSyncedBranch = "main";
                proj.LastSyncedRepositoryId = "12345";
                proj.LastSyncedRepositoryHost = "api.github.com";

                var commit = new Commit
                {
                    ProjectId = 1,
                    CommitHash = "head1",
                    CommitDate = DateTime.UtcNow,
                    ParentHashes = [],
                    IsVerified = true
                };
                seedContext.Commits.Add(commit);
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();

            var gitHubMock = new Mock<IGitHubService>();
            gitHubMock.Setup(g => g.GetRepositoryInfoAsync("https://github.com/owner/repo", "ghp_test_token", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "12345", "main", "head1"));

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object);

            var result = await service.SyncAsync(1);

            Assert.NotNull(result);
            Assert.Equal(0, result.Added);
            Assert.Equal(0, result.Skipped);

            // Страницы коммитов не должны запрашиваться
            gitHubMock.Verify(g => g.GetCommitPagesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task TR02_ManualUnverifiedCommit_IsNotTrustedBoundary_ParentsFetchedAndRecordUpdated()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using (var seedContext = new AppDbContext(options))
            {
                // Ручная запись коммита: IsVerified = false, ParentHashes пуст
                seedContext.Commits.Add(new Commit
                {
                    ProjectId = 1,
                    CommitHash = "manual1",
                    CommitDate = DateTime.UtcNow,
                    ParentHashes = [],
                    IsVerified = false
                });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();

            var gitHubMock = new Mock<IGitHubService>();
            gitHubMock.Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "12345", "main", "manual1"));

            var page = new List<GitHubCommitInfo>
            {
                new("manual1", DateTime.UtcNow, ["parent1"]),
                new("parent1", DateTime.UtcNow.AddMinutes(-5), []) // root
            };

            gitHubMock.Setup(g => g.GetCommitPagesAsync(It.IsAny<string>(), "manual1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(ToAsyncEnumerable(page));

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object);

            var result = await service.SyncAsync(1);

            Assert.NotNull(result);
            Assert.Equal(1, result.Added);   // parent1 добавлен
            Assert.Equal(1, result.Skipped); // manual1 обновлен (существовал)

            var manualCommit = await commitRepo.GetByHashAsync(1, "manual1");
            Assert.NotNull(manualCommit);
            Assert.True(manualCommit.IsVerified);
            Assert.Equal(["parent1"], manualCommit.ParentHashes);

            var parentCommit = await commitRepo.GetByHashAsync(1, "parent1");
            Assert.NotNull(parentCommit);
            Assert.True(parentCommit.IsVerified);
        }
    }

    [Fact]
    public async Task TR03_LinearChainOfNewCommits_StopsAtVerifiedBoundary()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using (var seedContext = new AppDbContext(options))
            {
                // Проверенный коммит c0 уже в БД
                seedContext.Commits.Add(new Commit
                {
                    ProjectId = 1,
                    CommitHash = "c0",
                    CommitDate = DateTime.UtcNow.AddHours(-3),
                    ParentHashes = [],
                    IsVerified = true
                });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();

            var gitHubMock = new Mock<IGitHubService>();
            gitHubMock.Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "12345", "main", "c2"));

            var page = new List<GitHubCommitInfo>
            {
                new("c2", DateTime.UtcNow, ["c1"]),
                new("c1", DateTime.UtcNow.AddHours(-1), ["c0"])
            };

            gitHubMock.Setup(g => g.GetCommitPagesAsync(It.IsAny<string>(), "c2", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(ToAsyncEnumerable(page));

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object);

            var result = await service.SyncAsync(1);

            Assert.NotNull(result);
            Assert.Equal(2, result.Added);
            Assert.Equal(0, result.Skipped);

            var c2 = await commitRepo.GetByHashAsync(1, "c2");
            Assert.NotNull(c2);
            Assert.True(c2.IsVerified);
            Assert.Equal(["c1"], c2.ParentHashes);

            var c1 = await commitRepo.GetByHashAsync(1, "c1");
            Assert.NotNull(c1);
            Assert.True(c1.IsVerified);
            Assert.Equal(["c0"], c1.ParentHashes);
        }
    }

    [Fact]
    public async Task TR04_MergeWithTwoParents_OneVerifiedOneUnknown_ExploresSecondBranchCompletely()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using (var seedContext = new AppDbContext(options))
            {
                // b1 уже проверен в БД
                seedContext.Commits.Add(new Commit
                {
                    ProjectId = 1,
                    CommitHash = "b1",
                    CommitDate = DateTime.UtcNow.AddHours(-2),
                    ParentHashes = [],
                    IsVerified = true
                });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();

            var gitHubMock = new Mock<IGitHubService>();
            gitHubMock.Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "12345", "main", "m1"));

            var page1 = new List<GitHubCommitInfo>
            {
                new("m1", DateTime.UtcNow, ["b1", "b2"])
            };
            var page2 = new List<GitHubCommitInfo>
            {
                new("b2", DateTime.UtcNow.AddMinutes(-30), ["b0"]),
                new("b0", DateTime.UtcNow.AddHours(-1), []) // root
            };

            gitHubMock.Setup(g => g.GetCommitPagesAsync(It.IsAny<string>(), "m1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(ToAsyncEnumerable(page1, page2));

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object);

            var result = await service.SyncAsync(1);

            Assert.NotNull(result);
            Assert.Equal(3, result.Added); // m1, b2, b0
            Assert.Equal(0, result.Skipped);

            var m1 = await commitRepo.GetByHashAsync(1, "m1");
            Assert.NotNull(m1);
            Assert.True(m1.IsVerified);
            Assert.Equal(["b1", "b2"], m1.ParentHashes);

            var b2 = await commitRepo.GetByHashAsync(1, "b2");
            Assert.NotNull(b2);
            Assert.True(b2.IsVerified);
        }
    }

    [Fact]
    public async Task TR05_ParentOnPreviousPage_CorrectlyResolvedAcrossPages()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();

            var gitHubMock = new Mock<IGitHubService>();
            gitHubMock.Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "12345", "main", "child1"));

            var page1 = new List<GitHubCommitInfo>
            {
                new("child1", DateTime.UtcNow, ["parent1"])
            };
            var page2 = new List<GitHubCommitInfo>
            {
                new("parent1", DateTime.UtcNow.AddMinutes(-10), []) // root
            };

            gitHubMock.Setup(g => g.GetCommitPagesAsync(It.IsAny<string>(), "child1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(ToAsyncEnumerable(page1, page2));

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object);

            var result = await service.SyncAsync(1);

            Assert.NotNull(result);
            Assert.Equal(2, result.Added);
            Assert.Equal(0, result.Skipped);
        }
    }

    [Fact]
    public async Task TR06_ParentBeforeChildOnSamePage_CorrectlyResolved()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();

            var gitHubMock = new Mock<IGitHubService>();
            gitHubMock.Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "12345", "main", "c1"));

            // Parent идет до Child в списке страницы
            var page = new List<GitHubCommitInfo>
            {
                new("p1", DateTime.UtcNow.AddMinutes(-5), []),
                new("c1", DateTime.UtcNow, ["p1"])
            };

            gitHubMock.Setup(g => g.GetCommitPagesAsync(It.IsAny<string>(), "c1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(ToAsyncEnumerable(page));

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object);

            var result = await service.SyncAsync(1);

            Assert.NotNull(result);
            Assert.Equal(2, result.Added);
        }
    }

    [Fact]
    public async Task TR07_ParentAfterChildOnSamePage_CorrectlyResolved()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();

            var gitHubMock = new Mock<IGitHubService>();
            gitHubMock.Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "12345", "main", "c1"));

            // Child идет до Parent в списке страницы
            var page = new List<GitHubCommitInfo>
            {
                new("c1", DateTime.UtcNow, ["p1"]),
                new("p1", DateTime.UtcNow.AddMinutes(-5), [])
            };

            gitHubMock.Setup(g => g.GetCommitPagesAsync(It.IsAny<string>(), "c1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(ToAsyncEnumerable(page));

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object);

            var result = await service.SyncAsync(1);

            Assert.NotNull(result);
            Assert.Equal(2, result.Added);
        }
    }

    [Fact]
    public async Task TR08_ApiExhaustedPagesWithUnresolvedParents_ThrowsIncompleteHistory_NoDbChanges()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();

            var gitHubMock = new Mock<IGitHubService>();
            gitHubMock.Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "12345", "main", "c1"));

            // c1 указывает на missing_parent, который не приходит из API и отсутствует в БД
            var page = new List<GitHubCommitInfo>
            {
                new("c1", DateTime.UtcNow, ["missing_parent"])
            };

            gitHubMock.Setup(g => g.GetCommitPagesAsync(It.IsAny<string>(), "c1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(ToAsyncEnumerable(page));

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object);

            var ex = await Assert.ThrowsAsync<IncompleteHistoryException>(() => service.SyncAsync(1));
            Assert.Equal(IncompleteHistoryKind.UnresolvedParents, ex.Kind);
            Assert.Contains("missing_parent", ex.UnresolvedParents);

            // В БД ничего не должно быть сохранено
            Assert.Null(await commitRepo.GetByHashAsync(1, "c1"));
            var proj = await projectRepo.GetByIdAsync(1);
            Assert.Null(proj!.LastSyncedCommitHash);
        }
    }

    [Fact]
    public async Task TR09_PagesEndedWithoutHeadFetched_ThrowsIncompleteHistory_NoCheckpointUpdate()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();

            var gitHubMock = new Mock<IGitHubService>();
            gitHubMock.Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "12345", "main", "expected_head"));

            // API вернул коммиты, но среди них нет expected_head
            var page = new List<GitHubCommitInfo>
            {
                new("other_commit", DateTime.UtcNow, [])
            };

            gitHubMock.Setup(g => g.GetCommitPagesAsync(It.IsAny<string>(), "expected_head", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(ToAsyncEnumerable(page));

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object);

            var ex = await Assert.ThrowsAsync<IncompleteHistoryException>(() => service.SyncAsync(1));
            Assert.Equal(IncompleteHistoryKind.HeadNotFetched, ex.Kind);

            var proj = await projectRepo.GetByIdAsync(1);
            Assert.Null(proj!.LastSyncedCommitHash);
        }
    }

    [Fact]
    public async Task TR10_DuplicateShaInPages_ProcessedOnceWithoutInflatingCounters()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();

            var gitHubMock = new Mock<IGitHubService>();
            gitHubMock.Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "12345", "main", "h1"));

            var page1 = new List<GitHubCommitInfo>
            {
                new("h1", DateTime.UtcNow, ["root1"]),
                new("h1", DateTime.UtcNow, ["root1"]) // дубликат на той же странице
            };
            var page2 = new List<GitHubCommitInfo>
            {
                new("h1", DateTime.UtcNow, ["root1"]), // дубликат на следующей странице
                new("root1", DateTime.UtcNow.AddMinutes(-5), [])
            };

            gitHubMock.Setup(g => g.GetCommitPagesAsync(It.IsAny<string>(), "h1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(ToAsyncEnumerable(page1, page2));

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object);

            var result = await service.SyncAsync(1);

            Assert.NotNull(result);
            Assert.Equal(2, result.Added); // h1, root1
            Assert.Equal(0, result.Skipped);
        }
    }

    [Fact]
    public async Task TR11_RepeatedSync_StopsAtVerifiedBoundary_DoesNotReexpandVerifiedParents()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using (var seedContext = new AppDbContext(options))
            {
                // В БД уже есть проверенные узлы v1 и v0
                seedContext.Commits.Add(new Commit
                {
                    ProjectId = 1,
                    CommitHash = "v0",
                    CommitDate = DateTime.UtcNow.AddHours(-2),
                    ParentHashes = [],
                    IsVerified = true
                });
                seedContext.Commits.Add(new Commit
                {
                    ProjectId = 1,
                    CommitHash = "v1",
                    CommitDate = DateTime.UtcNow.AddHours(-1),
                    ParentHashes = ["v0"],
                    IsVerified = true
                });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();

            var gitHubMock = new Mock<IGitHubService>();
            gitHubMock.Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "12345", "main", "new_head"));

            var page = new List<GitHubCommitInfo>
            {
                new("new_head", DateTime.UtcNow, ["v1"]),
                new("v1", DateTime.UtcNow.AddHours(-1), ["v0"]) // v1 проверен в БД
            };

            gitHubMock.Setup(g => g.GetCommitPagesAsync(It.IsAny<string>(), "new_head", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(ToAsyncEnumerable(page));

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object);

            var result = await service.SyncAsync(1);

            Assert.NotNull(result);
            Assert.Equal(1, result.Added);   // new_head
            Assert.Equal(1, result.Skipped); // v1 пропущен, так как уже был в БД
        }
    }

    [Fact]
    public async Task TR12_HeadPreservedViaFixedSha_RequestsPagesWithFixedHead()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();

            var gitHubMock = new Mock<IGitHubService>();
            gitHubMock.Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "12345", "main", "fixed_sha_42"));

            var page = new List<GitHubCommitInfo>
            {
                new("fixed_sha_42", DateTime.UtcNow, [])
            };

            gitHubMock.Setup(g => g.GetCommitPagesAsync("https://github.com/owner/repo", "fixed_sha_42", "ghp_test_token", It.IsAny<CancellationToken>()))
                .Returns(ToAsyncEnumerable(page));

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object);

            await service.SyncAsync(1);

            gitHubMock.Verify(g => g.GetCommitPagesAsync("https://github.com/owner/repo", "fixed_sha_42", "ghp_test_token", It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Fact]
    public async Task TR13_FivePlusPages_TraversesAllUntilProvenClosure()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();

            var gitHubMock = new Mock<IGitHubService>();
            gitHubMock.Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "12345", "main", "c5"));

            var p1 = new List<GitHubCommitInfo> { new("c5", DateTime.UtcNow, ["c4"]) };
            var p2 = new List<GitHubCommitInfo> { new("c4", DateTime.UtcNow.AddMinutes(-1), ["c3"]) };
            var p3 = new List<GitHubCommitInfo> { new("c3", DateTime.UtcNow.AddMinutes(-2), ["c2"]) };
            var p4 = new List<GitHubCommitInfo> { new("c2", DateTime.UtcNow.AddMinutes(-3), ["c1"]) };
            var p5 = new List<GitHubCommitInfo> { new("c1", DateTime.UtcNow.AddMinutes(-4), ["c0"]) };
            var p6 = new List<GitHubCommitInfo> { new("c0", DateTime.UtcNow.AddMinutes(-5), []) }; // root

            gitHubMock.Setup(g => g.GetCommitPagesAsync(It.IsAny<string>(), "c5", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(ToAsyncEnumerable(p1, p2, p3, p4, p5, p6));

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object);

            var result = await service.SyncAsync(1);

            Assert.NotNull(result);
            Assert.Equal(6, result.Added);
            Assert.Equal(0, result.Skipped);
        }
    }

    [Fact]
    public async Task TR14_ApiErrorOnIntermediatePage_ThrowsException_NoCheckpointSaved()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();

            var gitHubMock = new Mock<IGitHubService>();
            gitHubMock.Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "12345", "main", "head_err"));

            async IAsyncEnumerable<IReadOnlyList<GitHubCommitInfo>> ErrorGenerator()
            {
                yield return new List<GitHubCommitInfo> { new("head_err", DateTime.UtcNow, ["parent_err"]) };
                await Task.Yield();
                throw new GitHubIntegrationException(GitHubErrorKind.RateLimited, "Rate limit exceeded");
            }

            gitHubMock.Setup(g => g.GetCommitPagesAsync(It.IsAny<string>(), "head_err", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(ErrorGenerator());

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object);

            await Assert.ThrowsAsync<GitHubIntegrationException>(() => service.SyncAsync(1));

            var proj = await projectRepo.GetByIdAsync(1);
            Assert.Null(proj!.LastSyncedCommitHash);
        }
    }

    [Fact]
    public async Task TR15_CancellationDuringSync_ReleasesCoordinatorAndDoesNotUpdateCheckpoint()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();
            var coordinator = new GitHubSyncCoordinator();

            var gitHubMock = new Mock<IGitHubService>();
            var cts = new CancellationTokenSource();

            gitHubMock.Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, CancellationToken>((_, _, ct) =>
                {
                    cts.Cancel();
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(new GitHubRepositoryInfoDto("api.github.com", "12345", "main", "head"));
                });

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object, coordinator);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SyncAsync(1, cts.Token));

            // Координатор должен быть освобожден: повторный захват успешен
            using var newLock = coordinator.TryAcquire(1);
            Assert.NotNull(newLock);

            var proj = await projectRepo.GetByIdAsync(1);
            Assert.Null(proj!.LastSyncedCommitHash);
        }
    }

    [Fact]
    public async Task TR16_TwoConcurrentSyncCallsForSameProject_SecondGets409WithoutCallingGitHubApi()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();
            var coordinator = new GitHubSyncCoordinator();

            var gitHubMock = new Mock<IGitHubService>();

            // Захватываем блокировку проекта вручную, имитируя выполняющуюся синхронизацию
            using var heldLock = coordinator.TryAcquire(1);
            Assert.NotNull(heldLock);

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object, coordinator);

            // Второй вызов обязан выбросить SyncAlreadyInProgressException до вызова GitHub API
            await Assert.ThrowsAsync<SyncAlreadyInProgressException>(() => service.SyncAsync(1));

            gitHubMock.Verify(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            gitHubMock.Verify(g => g.GetCommitPagesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task TR17_StaleCheckpointSnapshot_ThrowsSyncCheckpointChangedException()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using var context = new AppDbContext(options);
            var commitRepo = new CommitRepository(context);

            var checkpoint = new ProjectCheckpointDto("h1", "main", "123", "api.github.com", DateTime.UtcNow);
            var snapshot = new ProjectCheckpointSnapshotDto(null, null, null, null);

            var newCommits = new List<Commit>
            {
                new() { CommitHash = "h1", ParentHashes = [], CommitDate = DateTime.UtcNow }
            };

            // Первая вставка
            var result1 = await commitRepo.SaveSyncBatchAsync(1, newCommits, [], checkpoint, snapshot);
            Assert.Contains("h1", result1.InsertedHashes);

            // Вторая попытка с устаревшим checkpoint snapshot должна упасть
            await Assert.ThrowsAsync<SyncCheckpointChangedException>(() =>
                commitRepo.SaveSyncBatchAsync(1, newCommits, [], checkpoint, snapshot));
        }
    }

    [Fact]
    public async Task SaveSyncBatchAsync_UniqueConstraintConflict_RecoversViaSavepointAndReconciles()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        using (connection)
        {
            var interceptor = new ConcurrentCommitInserterInterceptor(connection);
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(interceptor)
                .Options;

            using (var initContext = new AppDbContext(options))
            {
                initContext.Database.EnsureCreated();
                initContext.Roles.AddRange(new Role { Id = 1, Name = "Admin" }, new Role { Id = 2, Name = "User" });
                initContext.Users.Add(new User { Id = 1, Username = "testuser", PasswordHash = "hash", RoleId = 2 });
                initContext.Projects.Add(new Project { Id = 1, Name = "TestRepo", RepoUrl = "https://github.com/owner/repo", OwnerId = 1 });
                initContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var commitRepo = new CommitRepository(context);

            var checkpoint = new ProjectCheckpointDto("c1", "main", "123", "api.github.com", DateTime.UtcNow);
            var snapshot = new ProjectCheckpointSnapshotDto(null, null, null, null);

            var newCommits = new List<Commit>
            {
                new() { CommitHash = "c1", ParentHashes = ["parent_p1"], CommitDate = DateTime.UtcNow }
            };

            // SaveSyncBatchAsync инициирует вставку, но интерцептор параллельно вставляет 'c1' как unverified,
            // провоцируя нарушение UNIQUE constraint при SaveChangesAsync.
            // Репозиторий обязан откатить до savepoint, перечитать состояние, обнаружить строку и обновить её до verified.
            var result = await commitRepo.SaveSyncBatchAsync(1, newCommits, [], checkpoint, snapshot);

            Assert.Empty(result.InsertedHashes);
            Assert.Contains("c1", result.UpdatedHashes);

            var commit = await commitRepo.GetByHashAsync(1, "c1");
            Assert.NotNull(commit);
            Assert.True(commit.IsVerified);
            Assert.Equal(["parent_p1"], commit.ParentHashes);
        }
    }

    private sealed class ConcurrentCommitInserterInterceptor(SqliteConnection connection) : DbCommandInterceptor
    {
        private bool _triggered;

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!_triggered && command.CommandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase) && command.CommandText.Contains("Commits", StringComparison.OrdinalIgnoreCase))
            {
                _triggered = true;
                using var insertCmd = connection.CreateCommand();
                insertCmd.Transaction = (SqliteTransaction?)command.Transaction;
                insertCmd.CommandText = "INSERT INTO \"Commits\" (\"ProjectId\", \"CommitHash\", \"CommitDate\", \"ParentHashes\", \"IsVerified\") VALUES (1, 'c1', '2026-11-24 00:00:00', '[]', 0);";
                insertCmd.ExecuteNonQuery();
            }
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task SyncAsync_WhenAlreadyCurrent_ReturnsAlreadyCurrentTrue_AndNullClassification()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using (var seedContext = new AppDbContext(options))
            {
                var p = seedContext.Projects.First();
                p.LastSyncedCommitHash = "head_current";
                p.LastSyncedBranch = "main";
                p.LastSyncedRepositoryId = "repo_100";
                p.LastSyncedRepositoryHost = "api.github.com";

                seedContext.Commits.Add(new Commit
                {
                    ProjectId = p.Id,
                    CommitHash = "head_current",
                    CommitDate = DateTime.UtcNow,
                    ParentHashes = [],
                    IsVerified = true
                });

                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);

            var credMock = new Mock<IGitHubCredentialService>();
            credMock.Setup(c => c.GetUserTokenAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync("token");

            var gitHubMock = new Mock<IGitHubService>();
            gitHubMock.Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "repo_100", "main", "head_current"));

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credMock.Object);

            var result = await service.SyncAsync(1);

            Assert.NotNull(result);
            Assert.Equal(0, result.Added);
            Assert.Equal(0, result.Skipped);
            Assert.True(result.AlreadyCurrent);
            Assert.Null(result.HistoryClassification);

            gitHubMock.Verify(g => g.GetCommitPagesAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task TR18_ExistingUnverifiedRecord_UpdatedWithParentsAndVerifiedTrue_WithoutDuplicateInsert()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using (var seedContext = new AppDbContext(options))
            {
                seedContext.Commits.Add(new Commit
                {
                    ProjectId = 1,
                    CommitHash = "old_unverified",
                    CommitDate = DateTime.UtcNow,
                    ParentHashes = [],
                    IsVerified = false
                });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var commitRepo = new CommitRepository(context);

            var checkpoint = new ProjectCheckpointDto("old_unverified", "main", "123", "api.github.com", DateTime.UtcNow);
            var snapshot = new ProjectCheckpointSnapshotDto(null, null, null, null);

            var updates = new List<CommitUpdateDto>
            {
                new("old_unverified", ["parent_p1"])
            };

            var result = await commitRepo.SaveSyncBatchAsync(1, [], updates, checkpoint, snapshot);

            Assert.Empty(result.InsertedHashes);
            Assert.Contains("old_unverified", result.UpdatedHashes);

            var commit = await commitRepo.GetByHashAsync(1, "old_unverified");
            Assert.NotNull(commit);
            Assert.True(commit.IsVerified);
            Assert.Equal(["parent_p1"], commit.ParentHashes);
        }
    }

    [Fact]
    public async Task TR19_BranchSwitchedOnSameRepo_ClassifiedAsBranchSwitch()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using (var seedContext = new AppDbContext(options))
            {
                var p = seedContext.Projects.First();
                p.LastSyncedCommitHash = "main_head";
                p.LastSyncedBranch = "main";
                p.LastSyncedRepositoryId = "repo1";
                p.LastSyncedRepositoryHost = "api.github.com";

                seedContext.Commits.Add(new Commit
                {
                    ProjectId = 1,
                    CommitHash = "main_head",
                    CommitDate = DateTime.UtcNow,
                    ParentHashes = [],
                    IsVerified = true
                });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();

            var gitHubMock = new Mock<IGitHubService>();
            // Ветка изменилась на develop
            gitHubMock.Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "repo1", "develop", "develop_head"));

            var page = new List<GitHubCommitInfo>
            {
                new("develop_head", DateTime.UtcNow, [])
            };

            gitHubMock.Setup(g => g.GetCommitPagesAsync(It.IsAny<string>(), "develop_head", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(ToAsyncEnumerable(page));

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object);

            var result = await service.SyncAsync(1);

            Assert.NotNull(result);
            Assert.Equal(HistoryClassification.BranchSwitch, result.HistoryClassification);
        }
    }

    [Fact]
    public async Task TR20_RepositoryIdOrHostChanged_ClassifiedAsRepositoryChanged()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using (var seedContext = new AppDbContext(options))
            {
                var p = seedContext.Projects.First();
                p.LastSyncedCommitHash = "old_repo_head";
                p.LastSyncedBranch = "main";
                p.LastSyncedRepositoryId = "old_repo_id";
                p.LastSyncedRepositoryHost = "api.github.com";

                seedContext.Commits.Add(new Commit
                {
                    ProjectId = 1,
                    CommitHash = "old_repo_head",
                    CommitDate = DateTime.UtcNow,
                    ParentHashes = [],
                    IsVerified = true
                });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();

            var gitHubMock = new Mock<IGitHubService>();
            // Репозиторий изменился (новый ID)
            gitHubMock.Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "new_repo_id", "main", "new_head"));

            var page = new List<GitHubCommitInfo>
            {
                new("new_head", DateTime.UtcNow, [])
            };

            gitHubMock.Setup(g => g.GetCommitPagesAsync(It.IsAny<string>(), "new_head", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(ToAsyncEnumerable(page));

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object);

            var result = await service.SyncAsync(1);

            Assert.NotNull(result);
            Assert.Equal(HistoryClassification.RepositoryChanged, result.HistoryClassification);
        }
    }

    [Fact]
    public async Task TR21_OldHeadIsAncestorOfCurrentVerifiedBoundary_ClassifiedAsFastForward()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using (var seedContext = new AppDbContext(options))
            {
                var p = seedContext.Projects.First();
                p.LastSyncedCommitHash = "h0";
                p.LastSyncedBranch = "main";
                p.LastSyncedRepositoryId = "repo1";
                p.LastSyncedRepositoryHost = "api.github.com";

                // h0 -> h1 (в БД уже есть h0 и h1)
                seedContext.Commits.Add(new Commit { ProjectId = 1, CommitHash = "h0", ParentHashes = [], IsVerified = true, CommitDate = DateTime.UtcNow.AddHours(-2) });
                seedContext.Commits.Add(new Commit { ProjectId = 1, CommitHash = "h1", ParentHashes = ["h0"], IsVerified = true, CommitDate = DateTime.UtcNow.AddHours(-1) });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();

            var gitHubMock = new Mock<IGitHubService>();
            gitHubMock.Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "repo1", "main", "h2"));

            // h2 ссылается на h1 (граница в БД), h0 не приходит отдельным элементом API
            var page = new List<GitHubCommitInfo>
            {
                new("h2", DateTime.UtcNow, ["h1"])
            };

            gitHubMock.Setup(g => g.GetCommitPagesAsync(It.IsAny<string>(), "h2", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(ToAsyncEnumerable(page));

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object);

            var result = await service.SyncAsync(1);

            Assert.NotNull(result);
            Assert.Equal(HistoryClassification.FastForward, result.HistoryClassification);
        }
    }

    [Fact]
    public async Task TR22_HistoryDivergedWithCommonAncestor_ClassifiedAsNonFastForward()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using (var seedContext = new AppDbContext(options))
            {
                var p = seedContext.Projects.First();
                p.LastSyncedCommitHash = "old_head_branch";
                p.LastSyncedBranch = "main";
                p.LastSyncedRepositoryId = "repo1";
                p.LastSyncedRepositoryHost = "api.github.com";

                // Общий предок common_root
                seedContext.Commits.Add(new Commit { ProjectId = 1, CommitHash = "common_root", ParentHashes = [], IsVerified = true, CommitDate = DateTime.UtcNow.AddHours(-3) });
                // Старая ветка: old_head_branch -> common_root
                seedContext.Commits.Add(new Commit { ProjectId = 1, CommitHash = "old_head_branch", ParentHashes = ["common_root"], IsVerified = true, CommitDate = DateTime.UtcNow.AddHours(-2) });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();

            var gitHubMock = new Mock<IGitHubService>();
            gitHubMock.Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "repo1", "main", "new_diverged_head"));

            // Новая ветка: new_diverged_head -> common_root
            var page = new List<GitHubCommitInfo>
            {
                new("new_diverged_head", DateTime.UtcNow, ["common_root"])
            };

            gitHubMock.Setup(g => g.GetCommitPagesAsync(It.IsAny<string>(), "new_diverged_head", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(ToAsyncEnumerable(page));

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object);

            var result = await service.SyncAsync(1);

            Assert.NotNull(result);
            Assert.Equal(HistoryClassification.NonFastForwardHistory, result.HistoryClassification);
        }
    }

    [Fact]
    public async Task TR23_DisjointVerifiedGraphs_ClassifiedAsIndependentHistory()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using (var seedContext = new AppDbContext(options))
            {
                var p = seedContext.Projects.First();
                p.LastSyncedCommitHash = "old_root";
                p.LastSyncedBranch = "main";
                p.LastSyncedRepositoryId = "repo1";
                p.LastSyncedRepositoryHost = "api.github.com";

                // Старая история: root_a
                seedContext.Commits.Add(new Commit { ProjectId = 1, CommitHash = "old_root", ParentHashes = [], IsVerified = true, CommitDate = DateTime.UtcNow.AddHours(-5) });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();

            var gitHubMock = new Mock<IGitHubService>();
            gitHubMock.Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "repo1", "main", "new_independent_root"));

            // Новая история: root_b (без пересечения с root_a)
            var page = new List<GitHubCommitInfo>
            {
                new("new_independent_root", DateTime.UtcNow, [])
            };

            gitHubMock.Setup(g => g.GetCommitPagesAsync(It.IsAny<string>(), "new_independent_root", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(ToAsyncEnumerable(page));

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object);

            var result = await service.SyncAsync(1);

            Assert.NotNull(result);
            Assert.Equal(HistoryClassification.IndependentHistory, result.HistoryClassification);
        }
    }

    [Fact]
    public async Task TR24_OldCheckpointNotVerified_ClassifiedAsInsufficientEvidence()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using (var seedContext = new AppDbContext(options))
            {
                var p = seedContext.Projects.First();
                p.LastSyncedCommitHash = "unverified_prev_head";
                p.LastSyncedBranch = "main";
                p.LastSyncedRepositoryId = "repo1";
                p.LastSyncedRepositoryHost = "api.github.com";

                // Коммит в БД не подтвержден (IsVerified = false)
                seedContext.Commits.Add(new Commit { ProjectId = 1, CommitHash = "unverified_prev_head", ParentHashes = [], IsVerified = false, CommitDate = DateTime.UtcNow.AddHours(-2) });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();

            var gitHubMock = new Mock<IGitHubService>();
            gitHubMock.Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "repo1", "main", "new_head"));

            var page = new List<GitHubCommitInfo>
            {
                new("new_head", DateTime.UtcNow, [])
            };

            gitHubMock.Setup(g => g.GetCommitPagesAsync(It.IsAny<string>(), "new_head", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(ToAsyncEnumerable(page));

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object);

            var result = await service.SyncAsync(1);

            Assert.NotNull(result);
            Assert.Equal(HistoryClassification.InsufficientEvidence, result.HistoryClassification);
        }
    }

    [Fact]
    public async Task TR25_VerifiedNodeParentsNotReexpandedOrRefetched()
    {
        var (conn, options) = CreateInMemoryDatabase();
        using (conn)
        {
            using (var seedContext = new AppDbContext(options))
            {
                // v_boundary проверен в БД
                seedContext.Commits.Add(new Commit
                {
                    ProjectId = 1,
                    CommitHash = "v_boundary",
                    CommitDate = DateTime.UtcNow.AddHours(-2),
                    ParentHashes = ["v_ancient_parent"],
                    IsVerified = true
                });
                seedContext.SaveChanges();
            }

            using var context = new AppDbContext(options);
            var projectRepo = new ProjectRepository(context);
            var commitRepo = new CommitRepository(context);
            var credentialMock = CreateCredentialServiceMock();

            var gitHubMock = new Mock<IGitHubService>();
            gitHubMock.Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRepositoryInfoDto("api.github.com", "12345", "main", "child_commit"));

            // В API приходит child_commit -> v_boundary. v_ancient_parent не запрашивается и не требуется
            var page = new List<GitHubCommitInfo>
            {
                new("child_commit", DateTime.UtcNow, ["v_boundary"])
            };

            gitHubMock.Setup(g => g.GetCommitPagesAsync(It.IsAny<string>(), "child_commit", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(ToAsyncEnumerable(page));

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo, credentialMock.Object);

            var result = await service.SyncAsync(1);

            Assert.NotNull(result);
            Assert.Equal(1, result.Added); // child_commit
            Assert.Equal(0, result.Skipped);

            // Родители v_boundary не должны запрашиваться как непроверенные
            var child = await commitRepo.GetByHashAsync(1, "child_commit");
            Assert.NotNull(child);
            Assert.True(child.IsVerified);
        }
    }
}
