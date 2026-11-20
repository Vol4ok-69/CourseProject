using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Data;

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Development";

        var apiDirectory = FindApiProjectDirectory();

        var builder = new ConfigurationBuilder();

        if (apiDirectory != null)
        {
            builder.SetBasePath(apiDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .AddJsonFile($"appsettings.{environment}.json", optional: true);
        }

        builder.AddEnvironmentVariables();

        var configuration = builder.Build();

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? configuration["ConnectionStrings:DefaultConnection"]
            ?? configuration["ConnectionStrings__DefaultConnection"];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Строка подключения 'DefaultConnection' не найдена. " +
                "Убедитесь, что строка подключения настроена через переменную окружения " +
                "'ConnectionStrings__DefaultConnection' (или в файле appsettings.json).");
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AppDbContext(options);
    }

    private static string? FindApiProjectDirectory()
    {
        var current = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(current, "appsettings.json")))
        {
            return current;
        }

        var candidate = Path.Combine(current, "src", "Backend", "Api");
        if (File.Exists(Path.Combine(candidate, "appsettings.json")))
        {
            return candidate;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var checkPath = Path.Combine(dir.FullName, "src", "Backend", "Api");
            if (File.Exists(Path.Combine(checkPath, "appsettings.json")))
            {
                return checkPath;
            }

            if (File.Exists(Path.Combine(dir.FullName, "Api.csproj")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
