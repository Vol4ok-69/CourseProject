using System.ComponentModel.DataAnnotations;

namespace Application.DTOs;

/// <summary>
/// Данные для смены имени пользователя.
/// </summary>
public class ChangeUsernameDto
{
    /// <summary>
    /// Новое имя пользователя.
    /// </summary>
    [Required(ErrorMessage = "Имя пользователя обязательно для заполнения.")]
    [StringLength(50, MinimumLength = 3, ErrorMessage = "Имя пользователя должно содержать от 3 до 50 символов.")]
    public string Username { get; set; } = null!;
}
