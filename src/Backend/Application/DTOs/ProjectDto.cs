namespace Application.DTOs;

/// <summary>
/// Данные проекта.
/// </summary>
public class ProjectDto
{
    /// <summary>
    /// Уникальный идентификатор проекта.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Название проекта.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// URL репозитория проекта на GitHub.
    /// </summary>
    public string RepoUrl { get; set; } = null!;

    /// <summary>
    /// Идентификатор владельца проекта.
    /// </summary>
    public int OwnerId { get; set; }
}
