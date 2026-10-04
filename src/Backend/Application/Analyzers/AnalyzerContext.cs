namespace Application.Analyzers;

public class AnalyzerContext
{
    public string RepositoryPath { get; init; } = null!;
    public string CommitHash { get; init; } = null!;
    public string? ProjectPath { get; init; }
    public string? DbContextClassName { get; init; }
}