using Api.Common;
using Application.DTOs;
using Application.Exceptions;
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
    /// Создаёт задачу на анализ коммита проекта и ставит её в очередь.
    /// </summary>
    /// <param name="projectId">Идентификатор проекта.</param>
    /// <param name="commitId">Идентификатор коммита.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Созданная задача анализа.</returns>
    [HttpPost]
    [ProducesResponseType(typeof(AnalysisDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
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

        try
        {
            var analysis = await analysisService.EnqueueAsync(
                commitId,
                cancellationToken);

            return AcceptedAtAction(
                nameof(GetById),
                new { projectId, commitId, analysisId = analysis.Id },
                MapToDto(analysis));
        }
        catch (AnalysisAlreadyQueuedException exception)
        {
            return ApiProblemDetails.Create(
                this,
                StatusCodes.Status409Conflict,
                "Конфликт данных",
                exception.Message,
                ApiErrorCodes.AnalysisAlreadyQueued);
        }
        catch (InvalidOperationException exception) when (exception.Message == AnalysisAlreadyQueuedException.DefaultMessage)
        {
            return ApiProblemDetails.Create(
                this,
                StatusCodes.Status409Conflict,
                "Конфликт данных",
                exception.Message,
                ApiErrorCodes.AnalysisAlreadyQueued);
        }
    }

    /// <summary>
    /// Возвращает список анализов коммита с постраничной пагинацией.
    /// </summary>
    /// <param name="projectId">Идентификатор проекта.</param>
    /// <param name="commitId">Идентификатор коммита.</param>
    /// <param name="page">Номер страницы (по умолчанию 1).</param>
    /// <param name="pageSize">Размер страницы (по умолчанию 20, максимум 100).</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Страница анализов коммита с метаданными пагинации.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<AnalysisDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResultDto<AnalysisDto>>> GetByCommitId(
        int projectId,
        int commitId,
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

        var commit = await commitService.GetByIdAsync(commitId, cancellationToken);

        if (commit is null || commit.ProjectId != projectId)
        {
            return NotFound();
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

        var (analyses, totalCount) = await analysisService.GetByCommitIdAsync(commitId, page, actualPageSize, cancellationToken);

        var analysisDtos = analyses.Select(MapToDto).ToList();

        return Ok(new PagedResultDto<AnalysisDto>(analysisDtos, totalCount, page, actualPageSize));
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
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
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
            CreatedAt = analysis.CreatedAt,
            StartedAt = analysis.StartedAt,
            CompletedAt = analysis.CompletedAt,
            ErrorMessage = analysis.ErrorMessage,
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
