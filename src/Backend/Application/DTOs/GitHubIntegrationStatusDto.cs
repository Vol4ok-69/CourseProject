namespace Application.DTOs;

/// <summary>
/// Статус интеграции учетной записи с внешним сервисом GitHub.
/// </summary>
public class GitHubIntegrationStatusDto
{
    /// <summary>
    /// Признак наличия сохраненного персонального токена GitHub.
    /// </summary>
    public bool IsConnected { get; set; }

    /// <summary>
    /// Имя пользователя GitHub, если интеграция подключена.
    /// </summary>
    public string? GitHubUsername { get; set; }

    /// <summary>
    /// Дата и время подключения интеграции в формате UTC.
    /// </summary>
    public DateTime? ConnectedAt { get; set; }
}
