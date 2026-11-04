using System.ComponentModel.DataAnnotations;

namespace Application.DTOs;

/// <summary>
/// Запрос на сохранение или обновление персонального токена GitHub (PAT).
/// </summary>
public class GitHubTokenRequestDto
{
    /// <summary>
    /// Персональный токен доступа GitHub (PAT).
    /// </summary>
    [Required(ErrorMessage = "Токен GitHub обязателен для заполнения.")]
    public string Token { get; set; } = null!;
}
