using Application.DTOs;
using Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Api.Controllers;

/// <summary>
/// Управление анализами коммитов проектов.
/// </summary>
[Authorize]
[ApiController]
[Route("api/projects/{projectId:int}/commits/{commitId:int}/analyses")]
[Produces("application/json")]
public class AnalysesController(
    AnalysisService analysisService,
    CommitService commitService,
    ProjectService projectService) : ControllerBase
{
    /// <summary>
    /// Запускает анализ коммита проекта.
    /// </summary>
    /// <param name="projectId">Идентификатор проекта.</param>
    /// <param name="commitId">Идентификатор коммита.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Результат анализа.</returns>
    [HttpPost]
    [ProducesResponseType(typeof(AnalysisDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AnalysisDto>> Create(
        int projectId,
        int commitId,
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

        var commit = await commitService.GetByIdAsync(commitId, cancellationToken);

        if (commit is null || commit.ProjectId != projectId)
        {
            return NotFound();
        }

        var analysis = await analysisService.AnalyzeAsync(
            commitId,
            cancellationToken);

        return Ok(MapToDto(analysis));
    }

    /// <summary>
    /// Возвращает список анализов коммита.
    /// </summary>
    /// <param name="projectId">Идентификатор проекта.</param>
    /// <param name="commitId">Идентификатор коммита.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Список анализов.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AnalysisDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<AnalysisDto>>> GetByCommitId(
        int projectId,
        int commitId,
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

        var commit = await commitService.GetByIdAsync(commitId, cancellationToken);

        if (commit is null || commit.ProjectId != projectId)
        {
            return NotFound();
        }

        var analyses = await analysisService.GetByCommitIdAsync(commitId, cancellationToken);

        return Ok(analyses.Select(MapToDto).ToList());
    }

    /// <summary>
    /// Возвращает конкретный анализ коммита.
    /// </summary>
    /// <param name="projectId">Идентификатор проекта.</param>
    /// <param name="commitId">Идентификатор коммита.</param>
    /// <param name="analysisId">Идентификатор анализа.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Результат анализа.</returns>
    [HttpGet("{analysisId:int}")]
    [ProducesResponseType(typeof(AnalysisDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AnalysisDto>> GetById(
        int projectId,
        int commitId,
        int analysisId,
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

        var commit = await commitService.GetByIdAsync(commitId, cancellationToken);

        if (commit is null || commit.ProjectId != projectId)
        {
            return NotFound();
        }

        var analysis = await analysisService.GetByIdAsync(analysisId, cancellationToken);

        if (analysis is null || analysis.CommitId != commitId)
        {
            return NotFound();
        }

        return Ok(MapToDto(analysis));
    }


    private static AnalysisDto MapToDto(Domain.Entities.Analysis analysis)
    {
        return new AnalysisDto
        {
            Id = analysis.Id,
            CommitId = analysis.CommitId,
            Status = analysis.Status,
            StartedAt = analysis.StartedAt,
            CompletedAt = analysis.CompletedAt,
            Results = [.. analysis.Results
                .Select(result => new AnalysisResultDto
                {
                    Id = result.Id,
                    FilePath = result.FilePath,
                    LineNumber = result.LineNumber,
                    Rule = result.Rule,
                    Message = result.Message,
                    Severity = result.Severity,
                    Recommendation = result.Recommendation,
                    AnalyzerType = result.AnalyzerType
                })]
        };
    }
}