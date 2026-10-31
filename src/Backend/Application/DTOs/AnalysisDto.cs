using Domain.Enums;

namespace Application.DTOs;

/// <summary>
/// Данные о задаче анализа коммита.
/// </summary>
public class AnalysisDto
{
    /// <summary>
    /// Идентификатор анализа.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Идентификатор коммита, для которого выполнен анализ.
    /// </summary>
    public int CommitId { get; set; }

    /// <summary>
    /// Текущий статус выполнения анализа.
    /// </summary>
    public AnalysisStatus Status { get; set; }

    /// <summary>
    /// Дата и время создания задачи анализа в очереди.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Дата и время фактического начала выполнения анализа.
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// Дата и время завершения анализа.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Сообщение об ошибке при сбое выполнения анализа. Равно null при успешном завершении.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Результаты проверок анализаторов.
    /// </summary>
    public IReadOnlyList<AnalysisResultDto> Results { get; set; } = [];
}

/// <summary>
/// Результат отдельной проверки анализатора.
/// </summary>
public class AnalysisResultDto
{
    /// <summary>
    /// Идентификатор результата анализа.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Путь к файлу с обнаруженным замечанием.
    /// </summary>
    public string FilePath { get; set; } = null!;

    /// <summary>
    /// Номер строки с замечанием.
    /// </summary>
    public int LineNumber { get; set; }

    /// <summary>
    /// Идентификатор сработавшего правила анализатора.
    /// </summary>
    public string Rule { get; set; } = null!;

    /// <summary>
    /// Описание обнаруженного замечания на русском языке.
    /// </summary>
    public string Message { get; set; } = null!;

    /// <summary>
    /// Уровень важности замечания.
    /// </summary>
    public Severity Severity { get; set; }

    /// <summary>
    /// Рекомендация по исправлению замечания.
    /// </summary>
    public string? Recommendation { get; set; }

    /// <summary>
    /// Тип анализатора, сформировавшего результат.
    /// </summary>
    public AnalyzerType AnalyzerType { get; set; }
}
