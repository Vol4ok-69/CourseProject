using Application.Interfaces;

namespace Application.Analyzers;

public class AnalyzerContext
{
    public string RepositoryPath { get; init; } = null!;
    public string CommitHash { get; init; } = null!;
    public string? ProjectPath { get; init; }
    public string? DbContextClassName { get; init; }
    public int? AnalysisId { get; init; }
    public Guid? ExecutionToken { get; init; }

    /// <summary>
    /// Активный guard владения рабочей директорией, удерживаемый AnalysisService (TD-003).
    /// </summary>
    public IAnalysisLeaseGuard? LeaseGuard { get; init; }
}
