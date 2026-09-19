using Application.DTOs;
using Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Api.Controllers;

/// <summary>
/// Предоставляет операции для регистрации и аутентификации пользователей.
/// </summary>
[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public class AuthController(AuthService authService) : ControllerBase
{
    /// <summary>
    /// Регистрирует нового пользователя.
    /// </summary>
    /// <param name="dto">Данные нового пользователя.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Токен доступа созданного пользователя.</returns>
    [HttpPost("register")]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AuthResponseDto>> Register(RegisterDto dto, CancellationToken cancellationToken)
    {
        try
        {
            var response = await authService.RegisterAsync(dto, cancellationToken);

            return Ok(response);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new
            {
                message = exception.Message
            });
        }
    }

    /// <summary>
    /// Выполняет аутентификацию пользователя.
    /// </summary>
    /// <param name="dto">Учетные данные пользователя.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Токен доступа аутентифицированного пользователя.</returns>
    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResponseDto>> Login(LoginDto dto, CancellationToken cancellationToken)
    {
        var response = await authService.LoginAsync(dto, cancellationToken);

        if (response is null)
        {
            return Unauthorized(new
            {
                message = "Неверное имя пользователя или пароль."
            });
        }

        return Ok(response);
    }
    /// <summary>
    /// Позволяет авторизованному пользователю изменить свое имя пользователя.
    /// </summary>
    /// <param name="dto">DTO для изменения имени пользователя</param>
    /// <param name="cancellationToken">Токен отмены операции</param>
    /// <returns>Обновленный токен доступа</returns>
    [Authorize]
    [HttpPut("me/username")]
    public async Task<ActionResult<AuthResponseDto>> ChangeUsername(ChangeUsernameDto dto, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!int.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            var result = await authService.ChangeUsernameAsync(userId, dto, cancellationToken);

            if (result is null)
            {
                return NotFound();
            }

            return Ok(result);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(exception.Message);
        }
    }
    /// <summary>
    /// Позволяет авторизованному пользователю изменить свой пароль.
    /// </summary>
    /// <param name="dto">DTO для изменения пароля</param>
    /// <param name="cancellationToken">Токен отмены операции</param>
    /// <returns>Обновленный токен доступа</returns>
    [Authorize]
    [HttpPut("me/password")]
    public async Task<ActionResult<AuthResponseDto>> ChangePassword(
        ChangePasswordDto dto,
        CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!int.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            var result = await authService.ChangePasswordAsync(userId, dto, cancellationToken);

            if (result is null)
            {
                return NotFound();
            }

            return Ok(result);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(exception.Message);
        }
    }
}