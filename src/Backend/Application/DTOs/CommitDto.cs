namespace Application.DTOs;

/// <summary>
/// Данные коммита проекта.
/// </summary>
public class CommitDto
{
    /// <summary>
    /// Уникальный идентификатор коммита.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Идентификатор проекта, к которому относится коммит.
    /// </summary>
    public int ProjectId { get; set; }

    /// <summary>
    /// Хэш коммита в системе Git.
    /// </summary>
    public string CommitHash { get; set; } = null!;

    /// <summary>
    /// Дата и время фиксации коммита.
    /// </summary>
    public DateTime CommitDate { get; set; }
}
