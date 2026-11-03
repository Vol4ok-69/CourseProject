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

    [Fact]
    public async Task SyncAsync_WhenProjectNotFound_ReturnsNull()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();

        projectRepoMock.Setup(repo => repo.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Project?)null);

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object);

        var result = await service.SyncAsync(1);

        Assert.Null(result);
        gitHubMock.Verify(g => g.GetCommitsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        commitRepoMock.Verify(c => c.AddRangeAsync(It.IsAny<IReadOnlyList<Commit>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_WhenGitHubReturnsNoCommits_ReturnsZeroAddedAndZeroSkipped()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();

        projectRepoMock.Setup(repo => repo.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 1, RepoUrl = "https://github.com/test/repo" });

        gitHubMock.Setup(g => g.GetCommitsAsync("https://github.com/test/repo", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object);

        var result = await service.SyncAsync(1);

        Assert.NotNull(result);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Skipped);
        commitRepoMock.Verify(c => c.GetExistingHashesAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        commitRepoMock.Verify(c => c.AddRangeAsync(It.IsAny<IReadOnlyList<Commit>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_WhenAllCommitsAreNew_BatchInsertsAllAndReturnsCounts()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();

        projectRepoMock.Setup(repo => repo.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 10, RepoUrl = "https://github.com/test/repo" });

        var date = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var gitHubCommits = new List<GitHubCommitInfo>
        {
            new("hash1", date),
            new("hash2", date.AddHours(1)),
            new("hash3", date.AddHours(2))
        };

        gitHubMock.Setup(g => g.GetCommitsAsync("https://github.com/test/repo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(gitHubCommits);

        commitRepoMock.Setup(c => c.GetExistingHashesAsync(10, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());

        commitRepoMock.Setup(c => c.AddRangeAsync(It.IsAny<IReadOnlyList<Commit>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object);

        var result = await service.SyncAsync(10);

        Assert.NotNull(result);
        Assert.Equal(3, result.Added);
        Assert.Equal(0, result.Skipped);

        commitRepoMock.Verify(c => c.GetExistingHashesAsync(10, It.Is<IReadOnlyCollection<string>>(h => h.Count == 3), It.IsAny<CancellationToken>()), Times.Once);
        commitRepoMock.Verify(c => c.AddRangeAsync(It.Is<IReadOnlyList<Commit>>(list => list.Count == 3), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_WhenAllCommitsAlreadyExist_SkipsAllAndDoesNotCallAddRange()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();

        projectRepoMock.Setup(repo => repo.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 10, RepoUrl = "https://github.com/test/repo" });

        var date = DateTime.UtcNow;
        var gitHubCommits = new List<GitHubCommitInfo>
        {
            new("hash1", date),
            new("hash2", date)
        };

        gitHubMock.Setup(g => g.GetCommitsAsync("https://github.com/test/repo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(gitHubCommits);

        commitRepoMock.Setup(c => c.GetExistingHashesAsync(10, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "hash1", "hash2" });

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object);

        var result = await service.SyncAsync(10);

        Assert.NotNull(result);
        Assert.Equal(0, result.Added);
        Assert.Equal(2, result.Skipped);
        commitRepoMock.Verify(c => c.AddRangeAsync(It.IsAny<IReadOnlyList<Commit>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_WhenPartialCommitsExist_InsertsOnlyNewCommits()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();

        projectRepoMock.Setup(repo => repo.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 10, RepoUrl = "https://github.com/test/repo" });

        var date = DateTime.UtcNow;
        var gitHubCommits = new List<GitHubCommitInfo>
        {
            new("existing1", date),
            new("new1", date),
            new("existing2", date),
            new("new2", date)
        };

        gitHubMock.Setup(g => g.GetCommitsAsync("https://github.com/test/repo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(gitHubCommits);

        commitRepoMock.Setup(c => c.GetExistingHashesAsync(10, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "existing1", "existing2" });

        commitRepoMock.Setup(c => c.AddRangeAsync(It.IsAny<IReadOnlyList<Commit>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object);

        var result = await service.SyncAsync(10);

        Assert.NotNull(result);
        Assert.Equal(2, result.Added);
        Assert.Equal(2, result.Skipped);

        commitRepoMock.Verify(c => c.AddRangeAsync(
            It.Is<IReadOnlyList<Commit>>(list => list.Count == 2 && list.All(c => c.CommitHash == "new1" || c.CommitHash == "new2")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_WhenCommitDateIsNull_IgnoresCommitWithoutCounting()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();

        projectRepoMock.Setup(repo => repo.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 10, RepoUrl = "https://github.com/test/repo" });

        var gitHubCommits = new List<GitHubCommitInfo>
        {
            new("noDate1", null),
            new("valid1", DateTime.UtcNow),
            new("noDate2", null)
        };

        gitHubMock.Setup(g => g.GetCommitsAsync("https://github.com/test/repo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(gitHubCommits);

        commitRepoMock.Setup(c => c.GetExistingHashesAsync(10, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());

        commitRepoMock.Setup(c => c.AddRangeAsync(It.IsAny<IReadOnlyList<Commit>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object);

        var result = await service.SyncAsync(10);

        Assert.NotNull(result);
        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Skipped);
        commitRepoMock.Verify(c => c.GetExistingHashesAsync(10, It.Is<IReadOnlyCollection<string>>(h => h.Count == 1 && h.Contains("valid1")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_WhenDuplicateHashesInGitHubResponse_CountsSubsequentDuplicatesAsSkipped()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();

        projectRepoMock.Setup(repo => repo.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 10, RepoUrl = "https://github.com/test/repo" });

        var date = DateTime.UtcNow;
        var gitHubCommits = new List<GitHubCommitInfo>
        {
            new("hashA", date),
            new("hashA", date),
            new("hashB", date)
        };

        gitHubMock.Setup(g => g.GetCommitsAsync("https://github.com/test/repo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(gitHubCommits);

        commitRepoMock.Setup(c => c.GetExistingHashesAsync(10, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());

        commitRepoMock.Setup(c => c.AddRangeAsync(It.IsAny<IReadOnlyList<Commit>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object);

        var result = await service.SyncAsync(10);

        Assert.NotNull(result);
        Assert.Equal(2, result.Added);
        Assert.Equal(1, result.Skipped);

        commitRepoMock.Verify(c => c.GetExistingHashesAsync(10, It.Is<IReadOnlyCollection<string>>(h => h.Count == 2), It.IsAny<CancellationToken>()), Times.Once);
        commitRepoMock.Verify(c => c.AddRangeAsync(It.Is<IReadOnlyList<Commit>>(list => list.Count == 2), It.IsAny<CancellationToken>()), Times.Once);
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

            var date = new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);
            var initialCommits = new List<GitHubCommitInfo>
            {
                new("sha1", date),
                new("sha2", date.AddMinutes(1)),
                new("sha3", date.AddMinutes(2))
            };

            gitHubMock.Setup(g => g.GetCommitsAsync("https://github.com/test/sync", It.IsAny<CancellationToken>()))
                .ReturnsAsync(initialCommits);

            var service = new GitHubSyncService(gitHubMock.Object, projectRepo, commitRepo);

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
                new("sha1", date),
                new("sha2", date.AddMinutes(1)),
                new("sha3", date.AddMinutes(2)),
                new("sha4", date.AddMinutes(3))
            };

            gitHubMock.Setup(g => g.GetCommitsAsync("https://github.com/test/sync", It.IsAny<CancellationToken>()))
                .ReturnsAsync(subsequentCommits);

            var result2 = await service.SyncAsync(100);

            Assert.NotNull(result2);
            Assert.Equal(1, result2.Added);
            Assert.Equal(3, result2.Skipped);

            // Итого в базе ровно 4 коммита
            var totalCommitsInDb = await context.Commits.Where(c => c.ProjectId == 100).ToListAsync();
            Assert.Equal(4, totalCommitsInDb.Count);
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
                    CommitHash = "conflictingHash",
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
    public async Task SyncAsync_WhenAllCommitsHaveNullDate_ReturnsZeroAddedAndZeroSkipped()
    {
        var gitHubMock = new Mock<IGitHubService>();
        var projectRepoMock = new Mock<IProjectRepository>();
        var commitRepoMock = new Mock<ICommitRepository>();

        projectRepoMock.Setup(repo => repo.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 10, RepoUrl = "https://github.com/test/repo" });

        var gitHubCommits = new List<GitHubCommitInfo>
        {
            new("nullDate1", null),
            new("nullDate2", null),
            new("nullDate3", null)
        };

        gitHubMock.Setup(g => g.GetCommitsAsync("https://github.com/test/repo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(gitHubCommits);

        var service = new GitHubSyncService(gitHubMock.Object, projectRepoMock.Object, commitRepoMock.Object);

        var result = await service.SyncAsync(10);

        Assert.NotNull(result);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Skipped);

        // При отсутствии валидных коммитов запросы к репозиторию коммитов вообще не должны выполняться
        commitRepoMock.Verify(c => c.GetExistingHashesAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        commitRepoMock.Verify(c => c.AddRangeAsync(It.IsAny<IReadOnlyList<Commit>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
