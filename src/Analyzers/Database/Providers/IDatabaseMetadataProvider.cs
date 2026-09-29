using Application.Analyzers;
using DatabaseAnalyzer.Metadata;

namespace DatabaseAnalyzer.Providers;

public interface IDatabaseMetadataProvider
{
    Task<DatabaseMetadata> GetMetadataAsync(AnalyzerContext context, CancellationToken cancellationToken = default);
}