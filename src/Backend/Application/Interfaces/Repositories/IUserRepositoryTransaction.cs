namespace Application.Interfaces.Repositories;

/// <summary>
/// Представляет транзакционную область блокировки для защиты ролевой модели пользователей.
/// </summary>
public interface IUserRepositoryTransaction : IAsyncDisposable
{
    /// <summary>
    /// Фиксирует транзакцию изменений пользователей.
    /// </summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Откатывает транзакцию изменений пользователей.
    /// </summary>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
