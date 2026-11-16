using Application.Analyzers;
using DatabaseAnalyzer.Roslyn;

namespace DatabaseAnalyzer.Runner;

public sealed class ContainerAnalysisResult
{
    public string Status { get; init; } = "Success"; // "Success", "PartialSuccess", "Failed", "Unavailable", "TimedOut", "Cancelled"

    public IReadOnlyList<AnalyzerFinding> Findings { get; init; } = [];

    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    public string? ErrorMessage { get; init; }
}

public interface IDatabaseContainerRunner
{
    Task<ContainerAnalysisResult> RunAnalysisAsync(
        RoslynModelExtractionResult extraction,
        int analysisId,
        Guid executionToken,
        Application.Interfaces.IAnalysisLeaseGuard leaseGuard,
        CancellationToken cancellationToken = default);

    Task<ContainerAnalysisResult> RunAnalysisAsync(
        RoslynModelExtractionResult extraction,
        int analysisId,
        Guid executionToken,
        CancellationToken cancellationToken = default);
}
