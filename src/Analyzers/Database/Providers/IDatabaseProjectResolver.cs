using Application.Analyzers;

namespace DatabaseAnalyzer.Providers;

public interface IDatabaseProjectResolver
{
    Task<DatabaseProjectResolution?> ResolveAsync(
        AnalyzerContext context,
        CancellationToken cancellationToken = default);
}
