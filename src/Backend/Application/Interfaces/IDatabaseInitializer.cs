namespace Application.Interfaces;

/// <summary>
/// Интерфейс безопасной начальной инициализации базы данных при старте приложения.
/// </summary>
public interface IDatabaseInitializer
{
    /// <summary>
    /// Выполняет применение миграций EF Core и гарантирует наличие системных ролей с синхронизацией sequence.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
