using System.ComponentModel.DataAnnotations;

namespace Application.DTOs;

public class RegisterDto
{
    /// <summary>
    /// Имя пользователя.
    /// </summary>
    [Required]
    [MinLength(3)]
    public string Username { get; set; } = null!;

    /// <summary>
    /// Пароль пользователя.
    /// </summary>
    [Required]
    [MinLength(6)]
    public string Password { get; set; } = null!;
}