using Application.DTOs;
using Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Api.Controllers;

/// <summary>
/// Управление коммитами проектов.
/// </summary>
[Authorize]
[ApiController]
[Route("api/projects/{projectId:int}/commits")]
[Produces("application/json")]
public class CommitsController(
    CommitService commitService,
    ProjectService projectService) : ControllerBase
{
    /// <summary>
    /// Возвращает список коммитов проекта.
    /// </summary>
    /// <param name="projectId">Идентификатор проекта.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Список коммитов проекта.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<CommitDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<CommitDto>>> GetByProjectId(
        int projectId,
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

        var commits = await commitService.GetByProjectIdAsync(projectId, cancellationToken);

        var commitDtos = commits
            .Select(commit => new CommitDto
            {
                Id = commit.Id,
                ProjectId = commit.ProjectId,
                CommitHash = commit.CommitHash,
                CommitDate = commit.CommitDate
            })
            .ToList();

        return Ok(commitDtos);
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
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
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
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
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
            return BadRequest(new { message = exception.Message });
        }
    }
}