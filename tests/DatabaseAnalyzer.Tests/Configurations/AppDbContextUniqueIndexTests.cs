using Domain.Entities;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatabaseAnalyzer.Tests.Configurations;

public sealed class AppDbContextUniqueIndexTests
{
    [Fact]
    public void AppDbContext_Model_ContainsAllFourRequiredUniqueIndexes()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var context = new AppDbContext(options);
        var model = context.Model;

        var roleEntity = model.FindEntityType(typeof(Role));
        var userEntity = model.FindEntityType(typeof(User));
        var projectEntity = model.FindEntityType(typeof(Project));
        var commitEntity = model.FindEntityType(typeof(Commit));

        Assert.NotNull(roleEntity);
        Assert.NotNull(userEntity);
        Assert.NotNull(projectEntity);
        Assert.NotNull(commitEntity);

        // 1. Roles.Name -> IX_Roles_Name (UNIQUE)
        var roleNameIndex = roleEntity.GetIndexes()
            .SingleOrDefault(index => index.Properties.Select(p => p.Name).SequenceEqual(["Name"]));
        Assert.NotNull(roleNameIndex);
        Assert.True(roleNameIndex.IsUnique);

        // 2. Users.Username -> IX_Users_Username (UNIQUE)
        var usernameIndex = userEntity.GetIndexes()
            .SingleOrDefault(index => index.Properties.Select(p => p.Name).SequenceEqual(["Username"]));
        Assert.NotNull(usernameIndex);
        Assert.True(usernameIndex.IsUnique);

        // 3. Projects(OwnerId, Name) -> IX_Projects_OwnerId_Name (UNIQUE)
        var projectOwnerNameIndex = projectEntity.GetIndexes()
            .SingleOrDefault(index => index.Properties.Select(p => p.Name).SequenceEqual(["OwnerId", "Name"]));
        Assert.NotNull(projectOwnerNameIndex);
        Assert.True(projectOwnerNameIndex.IsUnique);

        // Также проверяем наличие одиночного индекса OwnerId для FK
        var projectOwnerIdIndex = projectEntity.GetIndexes()
            .SingleOrDefault(index => index.Properties.Select(p => p.Name).SequenceEqual(["OwnerId"]) && !index.IsUnique);
        Assert.NotNull(projectOwnerIdIndex);

        // 4. Commits(ProjectId, CommitHash) -> IX_Commits_ProjectId_CommitHash (UNIQUE)
        var commitProjectHashIndex = commitEntity.GetIndexes()
            .SingleOrDefault(index => index.Properties.Select(p => p.Name).SequenceEqual(["ProjectId", "CommitHash"]));
        Assert.NotNull(commitProjectHashIndex);
        Assert.True(commitProjectHashIndex.IsUnique);

        // Также проверяем наличие одиночного индекса ProjectId для FK
        var commitProjectIdIndex = commitEntity.GetIndexes()
            .SingleOrDefault(index => index.Properties.Select(p => p.Name).SequenceEqual(["ProjectId"]) && !index.IsUnique);
        Assert.NotNull(commitProjectIdIndex);
    }

    [Fact]
    public void Roles_Name_Duplicate_ThrowsDbUpdateException()
    {
        using var harness = new SqliteTestHarness();

        harness.Context.Roles.Add(new Role { Id = 1, Name = "Admin" });
        harness.Context.SaveChanges();

        harness.Context.Roles.Add(new Role { Id = 2, Name = "Admin" });

        Assert.Throws<DbUpdateException>(() => harness.Context.SaveChanges());
    }

    [Fact]
    public void Users_Username_Duplicate_ThrowsDbUpdateException()
    {
        using var harness = new SqliteTestHarness();

        harness.Context.Roles.Add(new Role { Id = 1, Name = "Admin" });
        harness.Context.SaveChanges();

        harness.Context.Users.Add(new User
        {
            Id = 1,
            Username = "duplicate_user",
            PasswordHash = "hash1",
            RoleId = 1
        });
        harness.Context.SaveChanges();

        harness.Context.Users.Add(new User
        {
            Id = 2,
            Username = "duplicate_user",
            PasswordHash = "hash2",
            RoleId = 1
        });

        Assert.Throws<DbUpdateException>(() => harness.Context.SaveChanges());
    }

    [Fact]
    public void Projects_OwnerId_And_Name_Duplicate_ThrowsDbUpdateException_WhileDifferentOwnersCanShareName()
    {
        using var harness = new SqliteTestHarness();

        harness.Context.Roles.Add(new Role { Id = 1, Name = "User" });
        harness.Context.Users.Add(new User { Id = 1, Username = "user1", PasswordHash = "hash1", RoleId = 1 });
        harness.Context.Users.Add(new User { Id = 2, Username = "user2", PasswordHash = "hash2", RoleId = 1 });
        harness.Context.SaveChanges();

        harness.Context.Projects.Add(new Project { Id = 1, Name = "CourseProject", RepoUrl = "https://github.com/1", OwnerId = 1 });
        harness.Context.SaveChanges();

        // Тот же проект у другого владельца - разрешен
        harness.Context.Projects.Add(new Project { Id = 2, Name = "CourseProject", RepoUrl = "https://github.com/2", OwnerId = 2 });
        harness.Context.SaveChanges();

        // Повторное имя у того же владельца - нарушение уникальности
        harness.Context.Projects.Add(new Project { Id = 3, Name = "CourseProject", RepoUrl = "https://github.com/3", OwnerId = 1 });

        Assert.Throws<DbUpdateException>(() => harness.Context.SaveChanges());
    }

    [Fact]
    public void Commits_ProjectId_And_CommitHash_Duplicate_ThrowsDbUpdateException_WhileDifferentProjectsCanShareHash()
    {
        using var harness = new SqliteTestHarness();

        harness.Context.Roles.Add(new Role { Id = 1, Name = "User" });
        harness.Context.Users.Add(new User { Id = 1, Username = "user1", PasswordHash = "hash1", RoleId = 1 });
        harness.Context.Projects.Add(new Project { Id = 1, Name = "Proj1", RepoUrl = "https://github.com/1", OwnerId = 1 });
        harness.Context.Projects.Add(new Project { Id = 2, Name = "Proj2", RepoUrl = "https://github.com/2", OwnerId = 1 });
        harness.Context.SaveChanges();

        var commitDate = DateTime.UtcNow;

        harness.Context.Commits.Add(new Commit
        {
            Id = 1,
            ProjectId = 1,
            CommitHash = "abcdef0123456789",
            CommitDate = commitDate
        });
        harness.Context.SaveChanges();

        // Тот же хэш коммита в другом проекте - разрешен
        harness.Context.Commits.Add(new Commit
        {
            Id = 2,
            ProjectId = 2,
            CommitHash = "abcdef0123456789",
            CommitDate = commitDate
        });
        harness.Context.SaveChanges();

        // Повторный хэш в том же проекте - нарушение уникальности
        harness.Context.Commits.Add(new Commit
        {
            Id = 3,
            ProjectId = 1,
            CommitHash = "abcdef0123456789",
            CommitDate = commitDate
        });

        Assert.Throws<DbUpdateException>(() => harness.Context.SaveChanges());
    }

    private sealed class SqliteTestHarness : IDisposable
    {
        private readonly SqliteConnection _connection;

        public AppDbContext Context { get; }

        public SqliteTestHarness()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_connection)
                .Options;

            Context = new AppDbContext(options);
            Context.Database.EnsureCreated();
        }

        public void Dispose()
        {
            Context.Dispose();
            _connection.Dispose();
        }
    }
}
