using System.ComponentModel.DataAnnotations;

namespace Application.DTOs;

/// <summary>
/// Данные для входа пользователя в систему.
/// </summary>
public class LoginDto
{
    /// <summary>
    /// Имя пользователя.
    /// </summary>
    [Required(ErrorMessage = "Имя пользователя обязательно для заполнения.")]
    public string Username { get; set; } = null!;

    /// <summary>
    /// Пароль пользователя.
    /// </summary>
    [Required(ErrorMessage = "Пароль обязателен для заполнения.")]
    public string Password { get; set; } = null!;
}
