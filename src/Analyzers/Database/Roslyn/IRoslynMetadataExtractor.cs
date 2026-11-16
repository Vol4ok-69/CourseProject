using DatabaseAnalyzer.Metadata;

namespace DatabaseAnalyzer.Roslyn;

/// <summary>
/// Интерфейс статического извлечения метаданных моделей Entity Framework Core через Roslyn (ADR-001).
/// Выполняет синтаксический анализ C#-исходников без компиляции проектов и без динамической загрузки сборок.
/// </summary>
public interface IRoslynMetadataExtractor
{
    /// <summary>
    /// Выполняет рекурсивный статический анализ исходного кода C# в указанном каталоге репозитория.
    /// </summary>
    Task<RoslynModelExtractionResult> ExtractAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Выполняет статический анализ набора исходных текстов с относительными путями файлов.
    /// </summary>
    RoslynModelExtractionResult ExtractFromSources(
        IEnumerable<(string SourceText, string FilePath)> sources,
        string? baseDirectory = null);

    /// <summary>
    /// Выполняет статический анализ одиночного исходного текста C#.
    /// </summary>
    RoslynModelExtractionResult ExtractFromSource(
        string sourceCode,
        string? filePath = null);
}
