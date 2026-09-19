using Application.DTOs;
using Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>
/// Предоставляет операции для работы с проектами.
/// </summary>
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
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProjectDto>> GetById(int id, CancellationToken cancellationToken)
    {
        var project = await projectService.GetByIdAsync(id, cancellationToken);

        if (project is null)
        {
            return NotFound();
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
    public async Task<ActionResult<IReadOnlyList<ProjectDto>>> GetByOwnerId(int ownerId, CancellationToken cancellationToken)
    {
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
    public async Task<ActionResult<ProjectDto>> Create(CreateProjectDto dto, CancellationToken cancellationToken)
    {
        var project = await projectService.CreateAsync(dto, cancellationToken);

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