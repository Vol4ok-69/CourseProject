using System.Text.Json.Serialization;

namespace Application.Enums;

/// <summary>
/// Классификация изменения истории коммитов относительно предыдущей контрольной точки.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HistoryClassification
{
    /// <summary>Первая синхронизация проекта (контрольная точка отсутствует).</summary>
    InitialSync,

    /// <summary>Изменился идентификатор или хост репозитория.</summary>
    RepositoryChanged,

    /// <summary>Изменилась отслеживаемая ветка репозитория.</summary>
    BranchSwitch,

    /// <summary>История развивается последовательно (fast-forward), старый HEAD является предком нового.</summary>
    FastForward,

    /// <summary>Расхождение истории (non-fast-forward), имеется общий предок, но старый HEAD не является предком нового.</summary>
    NonFastForwardHistory,

    /// <summary>Независимая история без общего предка.</summary>
    IndependentHistory,

    /// <summary>Недостаточно доказательств provenance для классификации истории.</summary>
    InsufficientEvidence
}
