using Application.DTOs;

namespace Application.Interfaces;

/// <summary>
/// Предоставляет операции для управления персональными учетными данными GitHub пользователей.
/// </summary>
public interface IGitHubCredentialService
{
    /// <summary>
    /// Проверяет персональный токен доступа GitHub и возвращает подтвержденное имя пользователя.
    /// </summary>
    Task<string> VerifyTokenAndGetUsernameAsync(string personalAccessToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Проверяет и сохраняет персональный токен доступа GitHub для указанного пользователя в зашифрованном виде.
    /// </summary>
    Task<GitHubIntegrationStatusDto?> SetUserTokenAsync(int userId, string personalAccessToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Удаляет сохраненный токен GitHub и связанные метаданные указанного пользователя.
    /// </summary>
    Task<GitHubIntegrationStatusDto?> RemoveUserTokenAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Возвращает статус интеграции с GitHub для указанного пользователя без обращения к внешнему API.
    /// </summary>
    Task<GitHubIntegrationStatusDto?> GetIntegrationStatusAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Возвращает расшифрованный персональный токен доступа GitHub пользователя для внутренних операций.
    /// </summary>
    Task<string?> GetUserTokenAsync(int userId, CancellationToken cancellationToken = default);
}
