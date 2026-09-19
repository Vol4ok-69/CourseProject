using Application.DTOs;
using Application.Services;
using Microsoft.AspNetCore.Mvc;

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
}