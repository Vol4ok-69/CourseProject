using Application.DTOs;
using Application.Interfaces;
using Application.Interfaces.Repositories;

namespace Infrastructure.GitHub;

/// <summary>
/// Реализует операции управления персональными учетными данными GitHub пользователей.
/// </summary>
public class GitHubCredentialService(
    IUserRepository userRepository,
    ITokenEncryptionService tokenEncryptionService,
    IGitHubService gitHubService) : IGitHubCredentialService
{
    public async Task<string> VerifyTokenAndGetUsernameAsync(
        string personalAccessToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(personalAccessToken))
            throw new ArgumentException("Не указан персональный токен доступа GitHub (PAT).", nameof(personalAccessToken));

        return await gitHubService.VerifyTokenAndGetUsernameAsync(personalAccessToken, cancellationToken);
    }

    public async Task<GitHubIntegrationStatusDto?> SetUserTokenAsync(
        int userId,
        string personalAccessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);

        if (string.IsNullOrWhiteSpace(personalAccessToken))
            throw new ArgumentException("Не указан персональный токен доступа GitHub (PAT).", nameof(personalAccessToken));

        var user = await userRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        // 1. Проверяем токен через GitHub API до любых изменений сущности пользователя.
        // Если вызов завершится ошибкой, существующие учетные данные останутся нетронутыми.
        var githubUsername = await gitHubService.VerifyTokenAndGetUsernameAsync(personalAccessToken, cancellationToken);

        // 2. Только после успешной верификации шифруем токен
        var encryptedToken = tokenEncryptionService.Encrypt(personalAccessToken);

        // 3. Согласованно обновляем все три поля сущности
        user.EncryptedGitHubToken = encryptedToken;
        user.GitHubUsername = githubUsername;
        user.GitHubTokenCreatedAt = DateTime.UtcNow;

        await userRepository.UpdateAsync(user, cancellationToken);

        return new GitHubIntegrationStatusDto
        {
            IsConnected = true,
            GitHubUsername = user.GitHubUsername,
            ConnectedAt = user.GitHubTokenCreatedAt
        };
    }

    public async Task<GitHubIntegrationStatusDto?> RemoveUserTokenAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);

        var user = await userRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        // Согласованно очищаем все три поля
        user.EncryptedGitHubToken = null;
        user.GitHubUsername = null;
        user.GitHubTokenCreatedAt = null;

        await userRepository.UpdateAsync(user, cancellationToken);

        return new GitHubIntegrationStatusDto
        {
            IsConnected = false,
            GitHubUsername = null,
            ConnectedAt = null
        };
    }

    public async Task<GitHubIntegrationStatusDto?> GetIntegrationStatusAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);

        var user = await userRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var isConnected = !string.IsNullOrWhiteSpace(user.EncryptedGitHubToken);

        return new GitHubIntegrationStatusDto
        {
            IsConnected = isConnected,
            GitHubUsername = isConnected ? user.GitHubUsername : null,
            ConnectedAt = isConnected ? user.GitHubTokenCreatedAt : null
        };
    }

    public async Task<string?> GetUserTokenAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);

        var user = await userRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null || string.IsNullOrWhiteSpace(user.EncryptedGitHubToken))
        {
            return null;
        }

        try
        {
            return tokenEncryptionService.Decrypt(user.EncryptedGitHubToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Не удалось расшифровать сохраненный токен GitHub.", ex);
        }
    }
}
