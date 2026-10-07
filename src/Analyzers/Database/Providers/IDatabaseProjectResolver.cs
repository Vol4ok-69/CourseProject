using Application.Analyzers;

namespace DatabaseAnalyzer.Providers;

public interface IDatabaseProjectResolver
{
    Task<string?> ResolveAsync(AnalyzerContext context, CancellationToken cancellationToken = default);
}
