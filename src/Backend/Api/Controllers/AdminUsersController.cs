using Application.DTOs;
using Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Api.Controllers;
/// <summary>
/// Предоставляет операции для управления пользователями в административной панели.
/// </summary>
/// <param name="userService">Сервис для работы с пользователями</param>
[ApiController]
[Route("api/admin/users")]
[Authorize(Roles = "Admin")]
public class AdminUsersController(UserService userService) : ControllerBase
{
    /// <summary>
    /// Получает список всех пользователей.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены операции</param>
    /// <returns>Список пользователей</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserDto>>> GetAll(CancellationToken cancellationToken)
    {
        var users = await userService.GetAllAsync(cancellationToken);

        return Ok(users.Select(MapToDto).ToList());
    }

    /// <summary>
    /// Получает пользователя по его идентификатору.
    /// </summary>
    /// <param name="id">Идентификатор пользователя</param>
    /// <param name="cancellationToken">Токен отмены операции</param>
    /// <returns>Пользователь</returns>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<UserDto>> GetById(int id, CancellationToken cancellationToken)
    {
        var user = await userService.GetByIdAsync(id, cancellationToken);

        if (user is null)
        {
            return NotFound();
        }

        return Ok(MapToDto(user));
    }
    /// <summary>
    /// Обновляет информацию о пользователе по его идентификатору.
    /// </summary>
    /// <param name="id">Идентификатор пользователя</param>
    /// <param name="dto">DTO для обновления пользователя</param>
    /// <param name="cancellationToken">Токен отмены операции</param>
    /// <returns>Обновленный пользователь</returns>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<UserDto>> Update(
        int id,
        UpdateUserDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            var user = await userService.UpdateAsync(id, dto, cancellationToken);

            if (user is null)
            {
                return NotFound();
            }

            return Ok(MapToDto(user));
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(exception.Message);
        }
    }
    /// <summary>
    /// Обновляет роль пользователя по его идентификатору.
    /// </summary>
    /// <param name="id">Идентификатор пользователя</param>
    /// <param name="dto">DTO для обновления роли пользователя</param>
    /// <param name="cancellationToken">Токен отмены операции</param>
    /// <returns>Обновленный пользователь</returns>
    [HttpPut("{id:int}/role")]
    public async Task<ActionResult<UserDto>> UpdateRole(
        int id,
        UpdateUserRoleDto dto,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var currentUserId))
        {
            return Unauthorized();
        }

        if (id == currentUserId)
        {
            return Forbid();
        }

        try
        {
            var user = await userService.UpdateRoleAsync(id, dto, cancellationToken);

            if (user is null)
            {
                return NotFound();
            }

            return Ok(MapToDto(user));
        }
        catch (InvalidOperationException exception)
        {
            return NotFound(exception.Message);
        }
    }
    /// <summary>
    /// Удаляет пользователя по его идентификатору.
    /// </summary>
    /// <param name="id">Идентификатор пользователя</param>
    /// <param name="cancellationToken">Токен отмены операции</param>
    /// <returns>204 No Content, если пользователь успешно удален</returns>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var currentUserId))
        {
            return Unauthorized();
        }

        if (id == currentUserId)
        {
            return Forbid();
        }

        try
        {
            var deleted = await userService.DeleteAsync(id, cancellationToken);

            if (!deleted)
            {
                return NotFound();
            }

            return NoContent();
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(exception.Message);
        }
    }

    private bool TryGetCurrentUserId(out int userId)
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);

        return int.TryParse(claim, out userId);
    }

    private static UserDto MapToDto(Domain.Entities.User user)
    {
        return new UserDto
        {
            Id = user.Id,
            Username = user.Username,
            RoleId = user.RoleId,
            RoleName = user.Role.Name
        };
    }
}