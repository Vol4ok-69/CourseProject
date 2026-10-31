using System.ComponentModel.DataAnnotations;

namespace Application.DTOs;

/// <summary>
/// Данные для регистрации нового пользователя.
/// </summary>
public class RegisterDto
{
    /// <summary>
    /// Имя пользователя.
    /// </summary>
    [Required(ErrorMessage = "Имя пользователя обязательно для заполнения.")]
    [StringLength(50, MinimumLength = 3, ErrorMessage = "Имя пользователя должно содержать от 3 до 50 символов.")]
    public string Username { get; set; } = null!;

    /// <summary>
    /// Пароль пользователя.
    /// </summary>
    [Required(ErrorMessage = "Пароль обязателен для заполнения.")]
    [MinLength(6, ErrorMessage = "Пароль должен содержать не менее 6 символов.")]
    public string Password { get; set; } = null!;
}
