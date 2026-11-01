namespace Application.DTOs;

/// <summary>
/// Результат синхронизации коммитов проекта с GitHub.
/// </summary>
public class GitHubSyncResultDto
{
    /// <summary>
    /// Количество добавленных новых коммитов.
    /// </summary>
    public int Added { get; set; }

    /// <summary>
    /// Количество пропущенных существующих коммитов.
    /// </summary>
    public int Skipped { get; set; }
}
