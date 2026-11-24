namespace Application.DTOs;

/// <summary>
/// Содержит метаданные репозитория GitHub и его текущей отслеживаемой ветки.
/// </summary>
/// <param name="ApiHost">Хост API GitHub (например, api.github.com).</param>
/// <param name="RepositoryId">Устойчивый идентификатор репозитория на GitHub.</param>
/// <param name="DefaultBranch">Имя ветки по умолчанию (например, main или master).</param>
/// <param name="DefaultBranchHeadSha">SHA актуального HEAD-коммита отслеживаемой ветки.</param>
public record GitHubRepositoryInfoDto(
    string ApiHost,
    string RepositoryId,
    string DefaultBranch,
    string DefaultBranchHeadSha);
