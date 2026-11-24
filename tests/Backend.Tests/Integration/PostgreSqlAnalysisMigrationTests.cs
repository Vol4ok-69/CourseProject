using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Backend.Tests.Integration;

public sealed class PostgreSqlAnalysisMigrationTests : IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlFixture _fixture;

    public PostgreSqlAnalysisMigrationTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Analyses_Table_HasExpectedDefaultValues_AfterMigration()
    {
        await _fixture.ResetDatabaseAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        await using var context = new AppDbContext(options);

        var user = new User
        {
            Username = "migration_tester",
            PasswordHash = "hash",
            RoleId = 1
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var project = new Project
        {
            Name = "Migration Test Project",
            RepoUrl = "https://github.com/test/repo",
            OwnerId = user.Id
        };
        context.Projects.Add(project);
        await context.SaveChangesAsync();

        var commit = new Commit
        {
            ProjectId = project.Id,
            CommitHash = "0123456789abcdef",
            CommitDate = DateTime.UtcNow
        };
        context.Commits.Add(commit);
        await context.SaveChangesAsync();

        var analysis = new Analysis
        {
            CommitId = commit.Id,
            Status = AnalysisStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        context.Analyses.Add(analysis);
        await context.SaveChangesAsync();

        var reloaded = await context.Analyses
            .AsNoTracking()
            .FirstAsync(a => a.Id == analysis.Id);

        Assert.Equal(0, reloaded.AttemptCount);
        Assert.Equal(3, reloaded.MaxAttempts);
        Assert.Null(reloaded.NextAttemptAt);
    }

    [Fact]
    public async Task Analyses_CheckConstraint_AttemptCount_RejectsNegativeValue()
    {
        await _fixture.ResetDatabaseAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        await using var context = new AppDbContext(options);

        var user = new User
        {
            Username = "attempt_ck",
            PasswordHash = "hash",
            RoleId = 1
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var project = new Project
        {
            Name = "CK Test Project",
            RepoUrl = "https://github.com/test/ck_repo",
            OwnerId = user.Id
        };
        context.Projects.Add(project);
        await context.SaveChangesAsync();

        var commit = new Commit
        {
            ProjectId = project.Id,
            CommitHash = "abcdef0123456789",
            CommitDate = DateTime.UtcNow
        };
        context.Commits.Add(commit);
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            var analysis = new Analysis
            {
                CommitId = commit.Id,
                Status = AnalysisStatus.Pending,
                CreatedAt = DateTime.UtcNow,
                AttemptCount = -1
            };
            context.Analyses.Add(analysis);
            await context.SaveChangesAsync();
        });

        var postgresEx = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("CK_Analyses_AttemptCount", postgresEx.ConstraintName);
    }

    [Fact]
    public async Task Analyses_CheckConstraint_MaxAttempts_RejectsZeroOrNegative()
    {
        await _fixture.ResetDatabaseAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        await using var context = new AppDbContext(options);

        var user = new User
        {
            Username = "max_attempts_ck",
            PasswordHash = "hash",
            RoleId = 1
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var project = new Project
        {
            Name = "CK MaxAttempts Project",
            RepoUrl = "https://github.com/test/ck_max_repo",
            OwnerId = user.Id
        };
        context.Projects.Add(project);
        await context.SaveChangesAsync();

        var commit = new Commit
        {
            ProjectId = project.Id,
            CommitHash = "abcdef9876543210",
            CommitDate = DateTime.UtcNow
        };
        context.Commits.Add(commit);
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            var analysis = new Analysis
            {
                CommitId = commit.Id,
                Status = AnalysisStatus.Pending,
                CreatedAt = DateTime.UtcNow,
                MaxAttempts = -1
            };
            context.Analyses.Add(analysis);
            await context.SaveChangesAsync();
        });

        var postgresEx = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("CK_Analyses_MaxAttempts", postgresEx.ConstraintName);
    }

    [Fact]
    public async Task Migration_Rollback_And_Reapply_Succeeds()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        await using (var context = new AppDbContext(options))
        {
            // Roll back to previous migration
            await context.Database.MigrateAsync("20261103101757_AddUserGitHubIntegration");
        }

        await using (var context = new AppDbContext(options))
        {
            // Reapply all migrations including AddAnalysisRetryAndPoisonPillQueue
            await context.Database.MigrateAsync();
        }

        await using (var context = new AppDbContext(options))
        {
            var pending = await context.Database.GetPendingMigrationsAsync();
            Assert.Empty(pending);
        }
    }
}
