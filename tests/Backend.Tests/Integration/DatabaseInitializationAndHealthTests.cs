using Api.Health;
using Application.DTOs;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Backend.Tests.Integration;

/// <summary>
/// Интеграционные тесты компонента инициализации базы данных DatabaseInitializer и эндпоинта /health.
/// </summary>
public sealed class DatabaseInitializationAndHealthTests(ApiWebApplicationFactory factory)
    : IClassFixture<ApiWebApplicationFactory>, IAsyncLifetime
{
    private readonly ApiWebApplicationFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        _factory.ResetMocks();
        await _factory.ResetDatabaseAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_factory.ConnectionString)
            .Options;

        await using var context = new AppDbContext(options);
        await RestoreDefaultRolesAsync(context);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task HealthCheck_WhenDatabaseIsHealthy_ReturnsOkAndHealthy()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal("Healthy", content);
    }

    [Fact]
    public async Task DatabaseInitializer_EnsuresDefaultRolesExistWithExpectedIds()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_factory.ConnectionString)
            .Options;

        await using var context = new AppDbContext(options);

        var adminRole = await context.Roles.FirstOrDefaultAsync(r => r.Id == 1);
        var userRole = await context.Roles.FirstOrDefaultAsync(r => r.Id == 2);

        Assert.NotNull(adminRole);
        Assert.Equal("Admin", adminRole.Name);

        Assert.NotNull(userRole);
        Assert.Equal("User", userRole.Name);
    }

    [Fact]
    public async Task DatabaseInitializer_IsIdempotent_SecondExecutionDoesNotDuplicateRoles()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_factory.ConnectionString)
            .Options;

        await using var context = new AppDbContext(options);
        var initializer = new DatabaseInitializer(context, NullLogger<DatabaseInitializer>.Instance);

        // Повторный запуск инициализатора
        await initializer.InitializeAsync();

        var adminRoles = await context.Roles.Where(r => r.Id == 1 || r.Name == "Admin").ToListAsync();
        var userRoles = await context.Roles.Where(r => r.Id == 2 || r.Name == "User").ToListAsync();

        Assert.Single(adminRoles);
        Assert.Single(userRoles);
    }

    [Fact]
    public async Task DatabaseInitializer_WhenUserRoleExistsWithDifferentId_ThrowsDiagnosticException()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_factory.ConnectionString)
            .Options;

        await using var context = new AppDbContext(options);

        try
        {
            if (await context.Users.AnyAsync())
            {
                context.Users.RemoveRange(await context.Users.ToListAsync());
                await context.SaveChangesAsync();
            }

            var userRole = await context.Roles.FirstOrDefaultAsync(r => r.Id == 2);
            if (userRole != null)
            {
                context.Roles.Remove(userRole);
            }
            context.Roles.Add(new Role { Id = 5, Name = "User" });
            await context.SaveChangesAsync();

            var initializer = new DatabaseInitializer(context, NullLogger<DatabaseInitializer>.Instance);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.InitializeAsync());

            Assert.Contains("Конфликт системной роли", ex.Message);
            Assert.Contains("User", ex.Message);
            Assert.Contains("Id=5", ex.Message);
            Assert.Contains("Id=2", ex.Message);
        }
        finally
        {
            await RestoreDefaultRolesAsync(context);
        }
    }

    [Fact]
    public async Task DatabaseInitializer_WhenAdminRoleExistsWithDifferentId_ThrowsDiagnosticException()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_factory.ConnectionString)
            .Options;

        await using var context = new AppDbContext(options);

        try
        {
            if (await context.Users.AnyAsync())
            {
                context.Users.RemoveRange(await context.Users.ToListAsync());
                await context.SaveChangesAsync();
            }

            var adminRole = await context.Roles.FirstOrDefaultAsync(r => r.Id == 1);
            if (adminRole != null)
            {
                context.Roles.Remove(adminRole);
            }
            context.Roles.Add(new Role { Id = 5, Name = "Admin" });
            await context.SaveChangesAsync();

            var initializer = new DatabaseInitializer(context, NullLogger<DatabaseInitializer>.Instance);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.InitializeAsync());

            Assert.Contains("Конфликт системной роли", ex.Message);
            Assert.Contains("Admin", ex.Message);
            Assert.Contains("Id=5", ex.Message);
            Assert.Contains("Id=1", ex.Message);
        }
        finally
        {
            await RestoreDefaultRolesAsync(context);
        }
    }

    [Fact]
    public async Task DatabaseInitializer_WhenId1OccupiedByDifferentRoleName_ThrowsDiagnosticException()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_factory.ConnectionString)
            .Options;

        await using var context = new AppDbContext(options);

        try
        {
            if (await context.Users.AnyAsync())
            {
                context.Users.RemoveRange(await context.Users.ToListAsync());
                await context.SaveChangesAsync();
            }

            var adminRole = await context.Roles.FirstOrDefaultAsync(r => r.Id == 1);
            if (adminRole != null)
            {
                context.Roles.Remove(adminRole);
            }
            context.Roles.Add(new Role { Id = 1, Name = "SuperAdmin" });
            await context.SaveChangesAsync();

            var initializer = new DatabaseInitializer(context, NullLogger<DatabaseInitializer>.Instance);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.InitializeAsync());

            Assert.Contains("Конфликт системной роли", ex.Message);
            Assert.Contains("Id=1", ex.Message);
            Assert.Contains("SuperAdmin", ex.Message);
            Assert.Contains("Admin", ex.Message);
        }
        finally
        {
            await RestoreDefaultRolesAsync(context);
        }
    }

    [Fact]
    public async Task DatabaseInitializer_WhenId2OccupiedByDifferentRoleName_ThrowsDiagnosticException()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_factory.ConnectionString)
            .Options;

        await using var context = new AppDbContext(options);

        try
        {
            if (await context.Users.AnyAsync())
            {
                context.Users.RemoveRange(await context.Users.ToListAsync());
                await context.SaveChangesAsync();
            }

            var userRole = await context.Roles.FirstOrDefaultAsync(r => r.Id == 2);
            if (userRole != null)
            {
                context.Roles.Remove(userRole);
            }
            context.Roles.Add(new Role { Id = 2, Name = "Guest" });
            await context.SaveChangesAsync();

            var initializer = new DatabaseInitializer(context, NullLogger<DatabaseInitializer>.Instance);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.InitializeAsync());

            Assert.Contains("Конфликт системной роли", ex.Message);
            Assert.Contains("Id=2", ex.Message);
            Assert.Contains("Guest", ex.Message);
            Assert.Contains("User", ex.Message);
        }
        finally
        {
            await RestoreDefaultRolesAsync(context);
        }
    }

    [Fact]
    public async Task DatabaseInitializer_SequenceSynchronization_AllowsSubsequentRoleInsertsWithoutConflict()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_factory.ConnectionString)
            .Options;

        await using var context = new AppDbContext(options);

        // Вставка новой роли без явного указания Id (полагаясь на PostgreSQL identity sequence)
        var customRole = new Role { Name = "QualityAuditor" };
        context.Roles.Add(customRole);
        await context.SaveChangesAsync();

        Assert.True(customRole.Id >= 3, $"Ожидался идентификатор >= 3, получен: {customRole.Id}");
    }

    [Fact]
    public async Task InitializedDatabase_AllowsUserRegistrationWithoutForeignKeyViolation()
    {
        var registerDto = new RegisterDto
        {
            Username = "docker_test_user",
            Password = "SecurePassword123!"
        };

        var response = await _client.PostAsJsonAsync("/api/auth/register", registerDto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_factory.ConnectionString)
            .Options;

        await using var context = new AppDbContext(options);
        var user = await context.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Username == "docker_test_user");

        Assert.NotNull(user);
        Assert.Equal(2, user.RoleId);
        Assert.NotNull(user.Role);
        Assert.Equal("User", user.Role.Name);
    }

    [Fact]
    public async Task DatabaseHealthCheck_WhenDatabaseIsUnreachable_ReturnsUnhealthyWithoutLeakingDetails()
    {
        var invalidOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=59999;Database=non_existent;Username=fake;Password=fake;Timeout=1;CommandTimeout=1")
            .Options;

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddScoped(_ => new AppDbContext(invalidOptions));
        var serviceProvider = serviceCollection.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var healthCheck = new DatabaseHealthCheck(scopeFactory, NullLogger<DatabaseHealthCheck>.Instance);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("База данных недоступна.", result.Description);
        Assert.Null(result.Exception);
    }

    private static async Task RestoreDefaultRolesAsync(AppDbContext context)
    {
        if (await context.Users.AnyAsync())
        {
            context.Users.RemoveRange(await context.Users.ToListAsync());
            await context.SaveChangesAsync();
        }

        var allRoles = await context.Roles.ToListAsync();
        context.Roles.RemoveRange(allRoles);
        await context.SaveChangesAsync();

        context.Roles.Add(new Role { Id = 1, Name = "Admin" });
        context.Roles.Add(new Role { Id = 2, Name = "User" });
        await context.SaveChangesAsync();

        if (context.Database.IsNpgsql())
        {
            await context.Database.ExecuteSqlRawAsync(
                "SELECT setval(pg_get_serial_sequence('\"Roles\"', 'Id'), 2);");
        }
    }
}
