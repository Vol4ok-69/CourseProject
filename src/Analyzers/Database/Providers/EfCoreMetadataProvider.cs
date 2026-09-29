using Application.Analyzers;
using Database.Metadata;

namespace Database.Providers;

public sealed class EfCoreMetadataProvider : IDatabaseMetadataProvider
{
    public Task<DatabaseMetadata> GetMetadataAsync(
        AnalyzerContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(context.RepositoryPath))
        {
            throw new DirectoryNotFoundException(
                $"Repository path was not found: {context.RepositoryPath}");
        }

        var projectFiles = Directory
            .EnumerateFiles(
                context.RepositoryPath,
                "*.csproj",
                SearchOption.AllDirectories)
            .ToList();

        if (projectFiles.Count == 0)
        {
            return Task.FromResult(new DatabaseMetadata());
        }

        return Task.FromResult(new DatabaseMetadata());
    }
}