using System.ComponentModel.DataAnnotations;

namespace Application.DTOs;

/// <summary>
/// Данные для смены пароля пользователя.
/// </summary>
public class ChangePasswordDto
{
    /// <summary>
    /// Текущий пароль пользователя.
    /// </summary>
    [Required(ErrorMessage = "Текущий пароль обязателен для заполнения.")]
    public string CurrentPassword { get; set; } = null!;

    /// <summary>
    /// Новый пароль пользователя.
    /// </summary>
    [Required(ErrorMessage = "Новый пароль обязателен для заполнения.")]
    [MinLength(6, ErrorMessage = "Новый пароль должен содержать не менее 6 символов.")]
    public string NewPassword { get; set; } = null!;
}
