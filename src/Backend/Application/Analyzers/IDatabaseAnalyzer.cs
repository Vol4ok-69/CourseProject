namespace Application.Analyzers;

/// <summary>
/// Интерфейс анализатора базы данных с поддержкой типизированного статуса выполнения (ADR-001 / TD-002).
/// </summary>
public interface IDatabaseAnalyzer : IAnalyzer
{
    /// <summary>
    /// Фактический статус последнего выполнения анализатора.
    /// </summary>
    DatabaseAnalysisExecutionStatus ExecutionStatus { get; }
}
