using Application.Analyzers;
using DatabaseAnalyzer.Mapping;
using DatabaseAnalyzer.Metadata;

namespace DatabaseAnalyzer.Providers;

public sealed class EfCoreMetadataProvider(
    IDatabaseProjectResolver projectResolver,
    IEfCoreModelInspector modelInspector,
    EfCoreMetadataMapper mapper
) : IDatabaseMetadataProvider
{
    public async Task<DatabaseMetadata> GetMetadataAsync(
        AnalyzerContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var resolution = await projectResolver.ResolveAsync(
            context,
            cancellationToken);

        if (resolution is null)
        {
            return new DatabaseMetadata();
        }

        var model = await modelInspector.InspectAsync(
            resolution,
            cancellationToken);

        return mapper.Map(model);
    }
}
