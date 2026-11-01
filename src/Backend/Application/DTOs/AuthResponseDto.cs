namespace Application.DTOs;

/// <summary>
/// Результат аутентификации или регистрации пользователя.
/// </summary>
public class AuthResponseDto
{
    /// <summary>
    /// JWT-токен доступа.
    /// </summary>
    public string AccessToken { get; set; } = null!;
}
