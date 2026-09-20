using Domain.Enums;

namespace Application.Analyzers;

public interface IAnalyzer
{
    AnalyzerType Type { get; }

    Task<IReadOnlyList<AnalyzerFinding>> AnalyzeAsync(AnalyzerContext context, CancellationToken cancellationToken = default);
}