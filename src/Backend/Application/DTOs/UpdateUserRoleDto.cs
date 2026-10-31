using System.ComponentModel.DataAnnotations;

namespace Application.DTOs;

/// <summary>
/// Данные для обновления роли пользователя администратором.
/// </summary>
public class UpdateUserRoleDto
{
    /// <summary>
    /// Идентификатор новой роли пользователя.
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "Идентификатор роли должен быть положительным числом.")]
    public int RoleId { get; set; }
}
