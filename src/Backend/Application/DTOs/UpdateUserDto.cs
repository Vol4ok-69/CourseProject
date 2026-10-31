using System.ComponentModel.DataAnnotations;

namespace Application.DTOs;

/// <summary>
/// Данные для обновления профиля пользователя администратором.
/// </summary>
public class UpdateUserDto
{
    /// <summary>
    /// Новое имя пользователя.
    /// </summary>
    [Required(ErrorMessage = "Имя пользователя обязательно для заполнения.")]
    [StringLength(50, MinimumLength = 3, ErrorMessage = "Имя пользователя должно содержать от 3 до 50 символов.")]
    public string Username { get; set; } = null!;
}
