using Api.Common;
using Application.DTOs;
using Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Api.Controllers;

/// <summary>
/// Управление коммитами проектов.
/// </summary>
[Authorize]
[ApiController]
[Route("api/projects/{projectId:int}/commits")]
[Produces("application/json")]
public class CommitsController(CommitService commitService, ProjectService projectService) : ControllerBase
{
    /// <summary>
    /// Возвращает список коммитов проекта с постраничной пагинацией.
    /// </summary>
    /// <param name="projectId">Идентификатор проекта.</param>
    /// <param name="page">Номер страницы (по умолчанию 1).</param>
    /// <param name="pageSize">Размер страницы (по умолчанию 20, максимум 100).</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Страница коммитов проекта с метаданными пагинации.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<CommitDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResultDto<CommitDto>>> GetByProjectId(
        int projectId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
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

        if (page < 1)
        {
            return ApiProblemDetails.Create(
                this,
                StatusCodes.Status400BadRequest,
                "Ошибка валидации",
                "Номер страницы (page) должен быть больше или равен 1.",
                ApiErrorCodes.ValidationFailed);
        }

        if (pageSize < 1)
        {
            return ApiProblemDetails.Create(
                this,
                StatusCodes.Status400BadRequest,
                "Ошибка валидации",
                "Размер страницы (pageSize) должен быть больше или равен 1.",
                ApiErrorCodes.ValidationFailed);
        }

        var actualPageSize = Math.Min(pageSize, 100);

        var (commits, totalCount) = await commitService.GetByProjectIdAsync(projectId, page, actualPageSize, cancellationToken);

        var commitDtos = commits
            .Select(commit => new CommitDto
            {
                Id = commit.Id,
                ProjectId = commit.ProjectId,
                CommitHash = commit.CommitHash,
                CommitDate = commit.CommitDate
            })
            .ToList();

        return Ok(new PagedResultDto<CommitDto>(commitDtos, totalCount, page, actualPageSize));
    }

    /// <summary>
    /// Возвращает конкретный коммит проекта.
    /// </summary>
    /// <param name="projectId">Идентификатор проекта.</param>
    /// <param name="id">Идентификатор коммита.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Конкретный коммит проекта.</returns>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(CommitDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CommitDto>> GetById(
        int projectId,
        int id,
        CancellationToken cancellationToken)
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

        var commit = await commitService.GetByIdAsync(id, cancellationToken);

        if (commit is null || commit.ProjectId != projectId)
        {
            return NotFound();
        }

        var commitDto = new CommitDto
        {
            Id = commit.Id,
            ProjectId = commit.ProjectId,
            CommitHash = commit.CommitHash,
            CommitDate = commit.CommitDate
        };

        return Ok(commitDto);
    }

    /// <summary>
    /// Создаёт коммит в проекте.
    /// </summary>
    /// <param name="projectId">Идентификатор проекта.</param>
    /// <param name="dto">Данные для создания коммита.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Созданный коммит.</returns>
    [HttpPost]
    [ProducesResponseType(typeof(CommitDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CommitDto>> Create(
        int projectId,
        CreateCommitDto dto,
        CancellationToken cancellationToken)
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
            var commit = await commitService.CreateAsync(
                projectId,
                dto,
                cancellationToken);

            if (commit is null)
            {
                return NotFound();
            }

            var commitDto = new CommitDto
            {
                Id = commit.Id,
                ProjectId = commit.ProjectId,
                CommitHash = commit.CommitHash,
                CommitDate = commit.CommitDate
            };

            return CreatedAtAction(
                nameof(GetById),
                new { projectId, id = commit.Id },
                commitDto);
        }
        catch (InvalidOperationException exception)
        {
            return ApiProblemDetails.Create(
                this,
                StatusCodes.Status409Conflict,
                "Конфликт данных",
                exception.Message,
                ApiErrorCodes.CommitAlreadyExists);
        }
        catch (DbUpdateException exception) when (IsCommitUniqueConstraintViolation(exception))
        {
            return ApiProblemDetails.Create(
                this,
                StatusCodes.Status409Conflict,
                "Конфликт данных",
                "Коммит с таким хэшем уже существует в проекте.",
                ApiErrorCodes.CommitAlreadyExists);
        }
    }

    private static bool IsCommitUniqueConstraintViolation(DbUpdateException exception)
    {
        if (exception.InnerException is Npgsql.PostgresException postgresException)
        {
            return postgresException.SqlState == "23505" &&
                   (postgresException.ConstraintName == "IX_Commits_ProjectId_CommitHash" ||
                    postgresException.ConstraintName?.Contains("Commits_ProjectId_CommitHash", StringComparison.OrdinalIgnoreCase) == true);
        }

        var inner = exception.InnerException;
        if (inner != null && inner.GetType().Name == "SqliteException")
        {
            return inner.Message.Contains("Commits.ProjectId", StringComparison.OrdinalIgnoreCase) ||
                   inner.Message.Contains("Commits.CommitHash", StringComparison.OrdinalIgnoreCase) ||
                   inner.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
