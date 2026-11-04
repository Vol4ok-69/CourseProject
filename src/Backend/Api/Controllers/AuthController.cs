using Api.Common;
using Application.DTOs;
using Application.Exceptions;
using Application.Interfaces;
using Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Api.Controllers;

/// <summary>
/// Предоставляет операции для регистрации и аутентификации пользователей.
/// </summary>
[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public class AuthController(
    AuthService authService,
    UserService userService,
    IGitHubCredentialService gitHubCredentialService) : ControllerBase
{
    /// <summary>
    /// Инициализирует новый экземпляр контроллера для обратной совместимости.
    /// </summary>
    /// <param name="authService">Сервис аутентификации.</param>
    /// <param name="userService">Сервис пользователей.</param>
    public AuthController(AuthService authService, UserService userService)
        : this(authService, userService, null!)
    {
    }
    /// <summary>
    /// Регистрирует нового пользователя.
    /// </summary>
    /// <param name="dto">Данные нового пользователя.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Токен доступа созданного пользователя.</returns>
    [EnableRateLimiting(RateLimitingPolicies.AuthPolicy)]
    [HttpPost("register")]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AuthResponseDto>> Register(RegisterDto dto, CancellationToken cancellationToken)
    {
        try
        {
            var response = await authService.RegisterAsync(dto, cancellationToken);

            return Ok(response);
        }
        catch (InvalidOperationException exception)
        {
            return ApiProblemDetails.Create(
                this,
                StatusCodes.Status409Conflict,
                "Конфликт данных",
                exception.Message,
                ApiErrorCodes.UsernameAlreadyExists);
        }
        catch (DbUpdateException exception) when (IsUsernameUniqueConstraintViolation(exception))
        {
            return ApiProblemDetails.Create(
                this,
                StatusCodes.Status409Conflict,
                "Конфликт данных",
                "Пользователь с таким именем уже существует.",
                ApiErrorCodes.UsernameAlreadyExists);
        }
    }

    /// <summary>
    /// Выполняет аутентификацию пользователя.
    /// </summary>
    /// <param name="dto">Учетные данные пользователя.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Токен доступа аутентифицированного пользователя.</returns>
    [EnableRateLimiting(RateLimitingPolicies.AuthPolicy)]
    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AuthResponseDto>> Login(LoginDto dto, CancellationToken cancellationToken)
    {
        var response = await authService.LoginAsync(dto, cancellationToken);

        if (response is null)
        {
            return ApiProblemDetails.Create(
                this,
                StatusCodes.Status401Unauthorized,
                "Ошибка аутентификации",
                "Неверное имя пользователя или пароль.",
                ApiErrorCodes.InvalidCredentials);
        }

        return Ok(response);
    }

    /// <summary>
    /// Получает профиль текущего аутентифицированного пользователя.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Профиль текущего пользователя.</returns>
    [Authorize]
    [HttpGet("me")]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserDto>> GetCurrentUser(CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!int.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        var user = await userService.GetByIdAsync(userId, cancellationToken);

        if (user is null)
        {
            return NotFound();
        }

        var userDto = new UserDto
        {
            Id = user.Id,
            Username = user.Username,
            RoleId = user.RoleId,
            RoleName = user.Role.Name
        };

        return Ok(userDto);
    }

    /// <summary>
    /// Позволяет авторизованному пользователю изменить свое имя пользователя.
    /// </summary>
    /// <param name="dto">DTO для изменения имени пользователя</param>
    /// <param name="cancellationToken">Токен отмены операции</param>
    /// <returns>Обновленный токен доступа</returns>
    [Authorize]
    [HttpPut("me/username")]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
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
            return ApiProblemDetails.Create(
                this,
                StatusCodes.Status409Conflict,
                "Конфликт данных",
                exception.Message,
                ApiErrorCodes.UsernameAlreadyExists);
        }
        catch (DbUpdateException exception) when (IsUsernameUniqueConstraintViolation(exception))
        {
            return ApiProblemDetails.Create(
                this,
                StatusCodes.Status409Conflict,
                "Конфликт данных",
                "Пользователь с таким именем уже существует.",
                ApiErrorCodes.UsernameAlreadyExists);
        }
    }

    /// <summary>
    /// Позволяет авторизованному пользователю изменить свой пароль.
    /// </summary>
    /// <param name="dto">DTO для изменения пароля</param>
    /// <param name="cancellationToken">Токен отмены операции</param>
    /// <returns>Обновленный токен доступа</returns>
    [Authorize]
    [EnableRateLimiting(RateLimitingPolicies.AuthPolicy)]
    [HttpPut("me/password")]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AuthResponseDto>> ChangePassword(ChangePasswordDto dto, CancellationToken cancellationToken)
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
            return ApiProblemDetails.Create(
                this,
                StatusCodes.Status400BadRequest,
                "Некорректный запрос",
                exception.Message,
                ApiErrorCodes.InvalidPassword);
        }
    }

    /// <summary>
    /// Получает статус интеграции с GitHub текущего пользователя.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Статус подключения персонального токена GitHub.</returns>
    [Authorize]
    [HttpGet("me/github")]
    [ProducesResponseType(typeof(GitHubIntegrationStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GitHubIntegrationStatusDto>> GetGitHubStatus(CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!int.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        var status = await gitHubCredentialService.GetIntegrationStatusAsync(userId, cancellationToken);

        if (status is null)
        {
            return ApiProblemDetails.Create(
                this,
                StatusCodes.Status404NotFound,
                "Ресурс не найден",
                "Пользователь не найден.",
                ApiErrorCodes.UserNotFound);
        }

        return Ok(status);
    }

    /// <summary>
    /// Подключает или обновляет проверенный персональный токен доступа GitHub (PAT) текущего пользователя.
    /// </summary>
    /// <param name="dto">Запрос с токеном GitHub.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Обновленный статус подключения к GitHub.</returns>
    [Authorize]
    [HttpPut("me/github")]
    [ProducesResponseType(typeof(GitHubIntegrationStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<GitHubIntegrationStatusDto>> SetGitHubToken(
        [FromBody] GitHubTokenRequestDto dto,
        CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!int.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(dto.Token))
        {
            return ApiProblemDetails.Create(
                this,
                StatusCodes.Status400BadRequest,
                "Ошибка валидации",
                "Токен GitHub не может быть пустым.",
                ApiErrorCodes.ValidationFailed);
        }

        try
        {
            var status = await gitHubCredentialService.SetUserTokenAsync(userId, dto.Token, cancellationToken);

            if (status is null)
            {
                return ApiProblemDetails.Create(
                    this,
                    StatusCodes.Status404NotFound,
                    "Ресурс не найден",
                    "Пользователь не найден.",
                    ApiErrorCodes.UserNotFound);
            }

            return Ok(status);
        }
        catch (GitHubIntegrationException exception)
        {
            return exception.Kind switch
            {
                GitHubErrorKind.TokenExpired => ApiProblemDetails.Create(
                    this,
                    StatusCodes.Status400BadRequest,
                    "Некорректный запрос",
                    exception.Message,
                    ApiErrorCodes.GitHubTokenExpired),

                GitHubErrorKind.RateLimited => ApiProblemDetails.Create(
                    this,
                    StatusCodes.Status502BadGateway,
                    "Ошибка внешнего сервиса",
                    exception.Message,
                    ApiErrorCodes.GitHubRateLimited),

                GitHubErrorKind.AccessDenied => ApiProblemDetails.Create(
                    this,
                    StatusCodes.Status502BadGateway,
                    "Ошибка внешнего сервиса",
                    exception.Message,
                    ApiErrorCodes.GitHubAccessDenied),

                _ => ApiProblemDetails.Create(
                    this,
                    StatusCodes.Status502BadGateway,
                    "Ошибка внешнего сервиса",
                    exception.Message,
                    ApiErrorCodes.GitHubServiceUnavailable)
            };
        }
        catch (ArgumentException exception)
        {
            return ApiProblemDetails.Create(
                this,
                StatusCodes.Status400BadRequest,
                "Некорректный запрос",
                exception.Message,
                ApiErrorCodes.ValidationFailed);
        }
        catch (Exception)
        {
            return ApiProblemDetails.Create(
                this,
                StatusCodes.Status500InternalServerError,
                "Внутренняя ошибка сервера",
                "Произошла ошибка при сохранении учетных данных GitHub.",
                ApiErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// Удаляет персональный токен доступа GitHub текущего пользователя.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Актуальный статус интеграции с отключенным токеном.</returns>
    [Authorize]
    [HttpDelete("me/github")]
    [ProducesResponseType(typeof(GitHubIntegrationStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GitHubIntegrationStatusDto>> RemoveGitHubToken(CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!int.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        var status = await gitHubCredentialService.RemoveUserTokenAsync(userId, cancellationToken);

        if (status is null)
        {
            return ApiProblemDetails.Create(
                this,
                StatusCodes.Status404NotFound,
                "Ресурс не найден",
                "Пользователь не найден.",
                ApiErrorCodes.UserNotFound);
        }

        return Ok(status);
    }

    private static bool IsUsernameUniqueConstraintViolation(DbUpdateException exception)
    {
        if (exception.InnerException is Npgsql.PostgresException postgresException)
        {
            return postgresException.SqlState == "23505" &&
                   (postgresException.ConstraintName == "IX_Users_Username" ||
                    postgresException.ConstraintName?.Contains("Users_Username", StringComparison.OrdinalIgnoreCase) == true);
        }

        var inner = exception.InnerException;
        if (inner != null && inner.GetType().Name == "SqliteException")
        {
            return inner.Message.Contains("Users.Username", StringComparison.OrdinalIgnoreCase) ||
                   inner.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
