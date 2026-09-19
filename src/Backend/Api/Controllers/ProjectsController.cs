using Application.DTOs;
using Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Api.Controllers;

/// <summary>
/// Предоставляет операции для работы с проектами.
/// </summary>
[Authorize]
[ApiController]
[Route("api/projects")]
[Produces("application/json")]
public class ProjectsController(ProjectService projectService) : ControllerBase
{
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

        var projectDtos = projects
            .Select(project => new ProjectDto
            {
                Id = project.Id,
                Name = project.Name,
                RepoUrl = project.RepoUrl,
                OwnerId = project.OwnerId
            })
            .ToList();

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
    public async Task<ActionResult<ProjectDto>> Create(CreateProjectDto dto, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userIdClaim is null || !int.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

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
}