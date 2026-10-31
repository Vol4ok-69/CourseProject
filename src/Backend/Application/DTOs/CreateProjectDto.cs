using System.ComponentModel.DataAnnotations;

namespace Application.DTOs;

/// <summary>
/// Данные для создания проекта.
/// </summary>
public class CreateProjectDto
{
    /// <summary>
    /// Название проекта.
    /// </summary>
    [Required(ErrorMessage = "Имя проекта обязательно для заполнения.")]
    [StringLength(50, MinimumLength = 3, ErrorMessage = "Длина имени проекта должна быть от 3 до 50 символов.")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// URL репозитория проекта на GitHub.
    /// </summary>
    [Required(ErrorMessage = "URL репозитория обязателен для заполнения.")]
    [RegularExpression(@"^https:\/\/github\.com\/[^\/\s]+\/[^\/\s]+(?:\.git)?\/?$", ErrorMessage = "URL репозитория должен иметь формат https://github.com/{{owner}}/{{repo}}.")]
    public string RepoUrl { get; set; } = null!;
}
