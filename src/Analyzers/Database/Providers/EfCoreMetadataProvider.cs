using Application.Analyzers;
using DatabaseAnalyzer.Mapping;
using DatabaseAnalyzer.Metadata;

namespace DatabaseAnalyzer.Providers;

public sealed class EfCoreMetadataProvider
(
    IDatabaseProjectResolver projectResolver,
    IEfCoreModelInspector modelInspector, EfCoreMetadataMapper mapper
) : IDatabaseMetadataProvider
{
    public async Task<DatabaseMetadata> GetMetadataAsync(AnalyzerContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var projectPath = await projectResolver.ResolveAsync(
            context,
            cancellationToken);

        if (projectPath is null)
        {
            return new DatabaseMetadata();
        }

        var model = await modelInspector.InspectAsync(
            projectPath,
            cancellationToken);

        return mapper.Map(model);
    }
}
