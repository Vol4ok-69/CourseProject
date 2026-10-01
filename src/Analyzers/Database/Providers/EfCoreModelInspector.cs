using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Reflection;

namespace DatabaseAnalyzer.Providers;

public sealed class EfCoreModelInspector : IEfCoreModelInspector
{
    public async Task<IModel> InspectAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException(
                "The specified project file was not found.",
                projectPath);
        }

        var projectDirectory = Path.GetDirectoryName(projectPath);

        if (projectDirectory is null)
        {
            throw new InvalidOperationException(
                "The project directory could not be determined.");
        }

        await BuildProjectAsync(
            projectPath,
            cancellationToken);

        var projectName = Path.GetFileNameWithoutExtension(projectPath);

        var targetFramework = await GetTargetFrameworkAsync(
            projectPath,
            cancellationToken);

        var assemblyPath = Path.Combine(
            projectDirectory,
            "bin",
            "Debug",
            targetFramework,
            $"{projectName}.dll");

        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException(
                "The project assembly was not found after build.",
                assemblyPath);
        }

        var assembly = Assembly.LoadFrom(assemblyPath);

        var contextType = assembly
            .GetTypes()
            .FirstOrDefault(type =>
                !type.IsAbstract &&
                typeof(DbContext).IsAssignableFrom(type));

        if (contextType is null)
        {
            throw new InvalidOperationException(
                "No DbContext implementation was found in the project.");
        }

        var factoryType = assembly
            .GetTypes()
            .FirstOrDefault(type =>
                !type.IsAbstract &&
                ImplementsDesignTimeFactory(type, contextType));

        if (factoryType is not null)
        {
            var factory = Activator.CreateInstance(factoryType);

            if (factory is null)
            {
                throw new InvalidOperationException(
                    $"Could not create DbContext factory '{factoryType.FullName}'.");
            }

            var createMethod = factoryType.GetMethod(
                nameof(IDesignTimeDbContextFactory<DbContext>.CreateDbContext));

            if (createMethod is null)
            {
                throw new InvalidOperationException(
                    $"CreateDbContext method was not found on '{factoryType.FullName}'.");
            }

            var context = createMethod.Invoke(
                factory,
                [Array.Empty<string>()]) as DbContext;

            if (context is null)
            {
                throw new InvalidOperationException(
                    "The design-time factory did not return a DbContext.");
            }

            return context.Model;
        }

        throw new InvalidOperationException(
            $"No IDesignTimeDbContextFactory was found for '{contextType.FullName}'.");
    }

    private static async Task BuildProjectAsync(
    string projectPath,
    CancellationToken cancellationToken)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{projectPath}\"",
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new System.Diagnostics.Process
        {
            StartInfo = startInfo
        };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                "Failed to start dotnet build process.");
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The analyzed project could not be built.{Environment.NewLine}" +
                $"{standardOutput}{Environment.NewLine}{standardError}");
        }
    }

    private static async Task<string> GetTargetFrameworkAsync(string projectPath, CancellationToken cancellationToken)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"msbuild \"{projectPath}\" -getProperty:TargetFramework",
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new System.Diagnostics.Process
        {
            StartInfo = startInfo
        };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                "Failed to start dotnet msbuild process.");
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The target framework could not be determined.{Environment.NewLine}" +
                $"{standardOutput}{Environment.NewLine}{standardError}");
        }

        var targetFramework = standardOutput
            .Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()
            ?.Trim();

        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            throw new InvalidOperationException(
                "The project target framework could not be determined.");
        }

        return targetFramework;
    }

    private static bool ImplementsDesignTimeFactory(
        Type type,
        Type contextType)
    {
        return type
            .GetInterfaces()
            .Any(interfaceType =>
                interfaceType.IsGenericType &&
                interfaceType.GetGenericTypeDefinition() ==
                typeof(IDesignTimeDbContextFactory<>) &&
                interfaceType.GetGenericArguments()[0] == contextType);
    }
}