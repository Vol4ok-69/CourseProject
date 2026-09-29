using Application.Analyzers;
using Database.Metadata;

namespace Database.Providers;

public interface IDatabaseMetadataProvider
{
    Task<DatabaseMetadata> GetMetadataAsync(AnalyzerContext context, CancellationToken cancellationToken = default);
}