using System.ComponentModel.DataAnnotations;

namespace Application.DTOs;

/// <summary>
/// Данные для регистрации коммита.
/// </summary>
public class CreateCommitDto
{
    /// <summary>
    /// Хэш коммита в Git (SHA-1).
    /// </summary>
    [Required(ErrorMessage = "Хэш коммита обязателен для заполнения.")]
    [StringLength(40, MinimumLength = 7, ErrorMessage = "Хэш коммита должен содержать от 7 до 40 символов.")]
    public string CommitHash { get; set; } = null!;

    /// <summary>
    /// Дата и время создания коммита.
    /// </summary>
    [Required(ErrorMessage = "Дата коммита обязательна для заполнения.")]
    public DateTime CommitDate { get; set; }
}
