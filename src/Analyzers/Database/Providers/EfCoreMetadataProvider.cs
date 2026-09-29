using Application.Analyzers;
using DatabaseAnalyzer.Mapping;
using DatabaseAnalyzer.Metadata;

namespace DatabaseAnalyzer.Providers;

public sealed class EfCoreMetadataProvider(
    IEfCoreModelInspector modelInspector,
    EfCoreMetadataMapper mapper) : IDatabaseMetadataProvider
{
    public async Task<DatabaseMetadata> GetMetadataAsync(
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
            return new DatabaseMetadata();
        }

        if (projectFiles.Count > 1)
        {
            throw new InvalidOperationException(
                "Multiple .csproj files were found in the repository.");
        }

        var model = await modelInspector.InspectAsync(
            projectFiles[0],
            cancellationToken);

        return mapper.Map(model);
    }
}