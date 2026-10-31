namespace Application.Interfaces.Repositories;

/// <summary>
/// Представляет транзакционную область блокировки для постановки анализа в очередь.
/// </summary>
public interface IAnalysisRepositoryTransaction : IAsyncDisposable
{
    /// <summary>
    /// Фиксирует транзакцию добавления задачи анализа в очередь.
    /// </summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Откатывает транзакцию добавления задачи анализа в очередь.
    /// </summary>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
