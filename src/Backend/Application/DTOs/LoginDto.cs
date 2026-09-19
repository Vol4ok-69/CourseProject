using System.ComponentModel.DataAnnotations;

namespace Application.DTOs;

public class LoginDto
{
    /// <summary>
    /// Имя пользователя.
    /// </summary>
    [Required]
    public string Username { get; set; } = null!;

    /// <summary>
    /// Пароль пользователя.
    /// </summary>
    [Required]
    public string Password { get; set; } = null!;
}