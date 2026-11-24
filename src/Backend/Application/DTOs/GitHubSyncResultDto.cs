using System.Text.Json.Serialization;
using Application.Enums;

namespace Application.DTOs;

/// <summary>
/// Результат синхронизации коммитов проекта с GitHub.
/// </summary>
public class GitHubSyncResultDto
{
    /// <summary>
    /// Количество добавленных новых коммитов.
    /// </summary>
    public int Added { get; set; }

    /// <summary>
    /// Количество пропущенных существующих коммитов.
    /// </summary>
    public int Skipped { get; set; }

    /// <summary>
    /// Флаг, указывающий, что проект уже синхронизирован с текущим HEAD (новых коммитов нет).
    /// </summary>
    public bool AlreadyCurrent { get; set; }

    /// <summary>
    /// Классификация изменения истории относительно предыдущего чекпоинта (если применимо).
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HistoryClassification? HistoryClassification { get; set; }
}
