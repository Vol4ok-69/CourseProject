namespace Application.DTOs;

/// <summary>
/// Данные учетной записи пользователя.
/// </summary>
public class UserDto
{
    /// <summary>
    /// Уникальный идентификатор пользователя.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Имя пользователя.
    /// </summary>
    public string Username { get; set; } = null!;

    /// <summary>
    /// Идентификатор роли пользователя.
    /// </summary>
    public int RoleId { get; set; }

    /// <summary>
    /// Название роли пользователя.
    /// </summary>
    public string RoleName { get; set; } = null!;
}
