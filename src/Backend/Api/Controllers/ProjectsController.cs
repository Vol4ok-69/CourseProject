using Api.Common;
using Application.DTOs;
using Application.Exceptions;
using Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Api.Controllers;

/// <summary>
/// Предоставляет операции для работы с проектами.
/// </summary>
[Authorize]
[ApiController]
[Route("api/projects")]
[Produces("application/json")]
public class ProjectsController(ProjectService projectService, GitHubSyncService gitHubSyncService) : ControllerBase
{
    /// <summary>
    /// Получает список проектов текущего пользователя или всех проектов (для администратора).
    /// </summary>
    /// <param name="all">Признак запроса всех проектов (учитывается только для роли Admin).</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Список проектов.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ProjectDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<ProjectDto>>> GetAll([FromQuery] bool all = false, CancellationToken cancellationToken = default)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userIdClaim is null || !int.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        var isAdmin = User.IsInRole("Admin");

        var projects = (isAdmin && all)
            ? await projectService.GetAllAsync(cancellationToken)
            : await projectService.GetByOwnerIdAsync(userId, cancellationToken);

        var projectDtos = projects.Select
        (
            project => new ProjectDto
            {
                Id = project.Id,
                Name = project.Name,
                RepoUrl = project.RepoUrl,
                OwnerId = project.OwnerId
            }
        ).ToList();

        return Ok(projectDtos);
    }

    /// <summary>
    /// Получает проект по его идентификатору.
    /// </summary>
    /// <param name="id">Идентификатор проекта.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Данные проекта.</returns>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ProjectDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProjectDto>> GetById(int id, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userIdClaim is null || !int.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        var project = await projectService.GetByIdAsync(id, cancellationToken);

        if (project is null)
        {
            return NotFound();
        }

        var isAdmin = User.IsInRole("Admin");

        if (project.OwnerId != userId && !isAdmin)
        {
            return Forbid();
        }

        var projectDto = new ProjectDto
        {
            Id = project.Id,
            Name = project.Name,
            RepoUrl = project.RepoUrl,
            OwnerId = project.OwnerId
        };

        return Ok(projectDto);
    }

    /// <summary>
    /// Получает список проектов указанного владельца.
    /// </summary>
    /// <param name="ownerId">Идентификатор владельца проектов.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Список проектов владельца.</returns>
    [HttpGet("owner/{ownerId:int}")]
    [ProducesResponseType(typeof(IReadOnlyList<ProjectDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<ProjectDto>>> GetByOwnerId(int ownerId, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userIdClaim is null || !int.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        var isAdmin = User.IsInRole("Admin");

        if (ownerId != userId && !isAdmin)
        {
            return Forbid();
        }

        var projects = await projectService.GetByOwnerIdAsync(ownerId, cancellationToken);

        var projectDtos = projects.Select
        (
            project => new ProjectDto
            {
                Id = project.Id,
                Name = project.Name,
                RepoUrl = project.RepoUrl,
                OwnerId = project.OwnerId
            }
        ).ToList();

        return Ok(projectDtos);
    }

    /// <summary>
    /// Создает новый проект.
    /// </summary>
    /// <param name="dto">Данные создаваемого проекта.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Созданный проект.</returns>
    [HttpPost]
    [ProducesResponseType(typeof(ProjectDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProjectDto>> Create(CreateProjectDto dto, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userIdClaim is null || !int.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            var project = await projectService.CreateAsync(dto, userId, cancellationToken);

            var projectDto = new ProjectDto
            {
                Id = project.Id,
                Name = project.Name,
                RepoUrl = project.RepoUrl,
                OwnerId = project.OwnerId
            };

            return CreatedAtAction(nameof(GetById), new { id = project.Id }, projectDto);
        }
        catch (InvalidOperationException exception)
        {
            return ApiProblemDetails.Create(
                this,
                StatusCodes.Status409Conflict,
                "Конфликт данных",
                exception.Message,
                ApiErrorCodes.ProjectNameDuplicate);
        }
        catch (DbUpdateException exception) when (IsProjectUniqueConstraintViolation(exception))
        {
            return ApiProblemDetails.Create(
                this,
                StatusCodes.Status409Conflict,
                "Конфликт данных",
                "Проект с таким именем уже существует.",
                ApiErrorCodes.ProjectNameDuplicate);
        }
    }

    /// <summary>
    /// Обновляет данные проекта.
    /// </summary>
    /// <param name="id">Идентификатор проекта.</param>
    /// <param name="dto">Новые данные проекта.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Обновленный проект.</returns>
    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(ProjectDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProjectDto>> Update(int id, UpdateProjectDto dto, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userIdClaim is null || !int.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        var project = await projectService.GetByIdAsync(id, cancellationToken);

        if (project is null)
        {
            return NotFound();
        }

        var isAdmin = User.IsInRole("Admin");

        if (project.OwnerId != userId && !isAdmin)
        {
            return Forbid();
        }

        try
        {
            var updatedProject = await projectService.UpdateAsync(id, dto, cancellationToken);

            if (updatedProject is null)
            {
                return NotFound();
            }

            var projectDto = new ProjectDto
            {
                Id = updatedProject.Id,
                Name = updatedProject.Name,
                RepoUrl = updatedProject.RepoUrl,
                OwnerId = updatedProject.OwnerId
            };

            return Ok(projectDto);
        }
        catch (InvalidOperationException exception)
        {
            return ApiProblemDetails.Create(
                this,
                StatusCodes.Status409Conflict,
                "Конфликт данных",
                exception.Message,
                ApiErrorCodes.ProjectNameDuplicate);
        }
        catch (DbUpdateException exception) when (IsProjectUniqueConstraintViolation(exception))
        {
            return ApiProblemDetails.Create(
                this,
                StatusCodes.Status409Conflict,
                "Конфликт данных",
                "Проект с таким именем уже существует.",
                ApiErrorCodes.ProjectNameDuplicate);
        }
    }

    /// <summary>
    /// Удаляет проект.
    /// </summary>
    /// <param name="id">Идентификатор проекта.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userIdClaim is null || !int.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        var project = await projectService.GetByIdAsync(id, cancellationToken);

        if (project is null)
        {
            return NotFound();
        }

        var isAdmin = User.IsInRole("Admin");

        if (project.OwnerId != userId && !isAdmin)
        {
            return Forbid();
        }

        var deleted = await projectService.DeleteAsync(id, cancellationToken);

        if (!deleted)
        {
            return NotFound();
        }

        return NoContent();
    }

    /// <summary>
    /// Синхронизирует проект с GitHub.
    /// </summary>
    /// <param name="projectId">Идентификатор проекта.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Результат синхронизации.</returns>
    [HttpPost("{projectId:int}/github/sync")]
    [ProducesResponseType(typeof(GitHubSyncResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<GitHubSyncResultDto>> SyncGitHub(int projectId, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userIdClaim is null || !int.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        var project = await projectService.GetByIdAsync(projectId, cancellationToken);

        if (project is null)
        {
            return NotFound();
        }

        var isAdmin = User.IsInRole("Admin");

        if (project.OwnerId != userId && !isAdmin)
        {
            return Forbid();
        }

        try
        {
            var result = await gitHubSyncService.SyncAsync(projectId, cancellationToken);

            if (result is null)
            {
                return NotFound();
            }

            return Ok(result);
        }
        catch (GitHubIntegrationException exception)
        {
            return exception.Kind switch
            {
                GitHubErrorKind.InvalidRepoUrl => ApiProblemDetails.Create(
                    this,
                    StatusCodes.Status400BadRequest,
                    "Некорректный запрос",
                    exception.Message,
                    ApiErrorCodes.InvalidRepoUrl),

                GitHubErrorKind.RepoNotFound => ApiProblemDetails.Create(
                    this,
                    StatusCodes.Status404NotFound,
                    "Ресурс не найден",
                    exception.Message,
                    ApiErrorCodes.GitHubRepoNotFound),

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
                ApiErrorCodes.InvalidRepoUrl);
        }
    }

    private static bool IsProjectUniqueConstraintViolation(DbUpdateException exception)
    {
        if (exception.InnerException is Npgsql.PostgresException postgresException)
        {
            return postgresException.SqlState == "23505" &&
                   (postgresException.ConstraintName == "IX_Projects_OwnerId_Name" ||
                    postgresException.ConstraintName?.Contains("Projects_OwnerId_Name", StringComparison.OrdinalIgnoreCase) == true);
        }

        var inner = exception.InnerException;
        if (inner != null && inner.GetType().Name == "SqliteException")
        {
            return inner.Message.Contains("Projects.OwnerId", StringComparison.OrdinalIgnoreCase) ||
                   inner.Message.Contains("Projects.Name", StringComparison.OrdinalIgnoreCase) ||
                   inner.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
