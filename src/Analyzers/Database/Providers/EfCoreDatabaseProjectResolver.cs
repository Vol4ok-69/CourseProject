using Application.Analyzers;

namespace DatabaseAnalyzer.Providers;

public sealed class EfCoreDatabaseProjectResolver : IDatabaseProjectResolver
{
    public Task<string?> ResolveAsync(AnalyzerContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(context.RepositoryPath))
        {
            throw new DirectoryNotFoundException($"Repository path was not found: {context.RepositoryPath}");
        }

        if (!string.IsNullOrWhiteSpace(context.ProjectPath))
        {
            var projectPath = Path.GetFullPath(Path.Combine(context.RepositoryPath, context.ProjectPath));

            if (!File.Exists(projectPath))
            {
                throw new FileNotFoundException($"Project file was not found: {projectPath}", projectPath);
            }

            if (!string.Equals(Path.GetExtension(projectPath), ".csproj", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Specified project is not a .csproj file: {projectPath}");
            }

            return Task.FromResult<string?>(projectPath);
        }

        var projectFiles = Directory.EnumerateFiles(context.RepositoryPath, "*.csproj", SearchOption.AllDirectories).ToList();

        if (projectFiles.Count == 0)
        {
            return Task.FromResult<string?>(null);
        }

        if (projectFiles.Count > 1)
        {
            throw new InvalidOperationException("Multiple .csproj files were found in the repository. Specify ProjectPath explicitly.");
        }

        return Task.FromResult<string?>(projectFiles[0]);
    }
}