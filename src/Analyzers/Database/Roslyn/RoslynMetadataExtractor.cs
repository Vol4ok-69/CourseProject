using DatabaseAnalyzer.Metadata;

namespace DatabaseAnalyzer.Roslyn;

/// <summary>
/// Реализация безопасного экстрактора метаданных EF Core на базе RoslynModelInspector.
/// Изолирует извлечение метаданных от выполнения правил и запуска контейнера (ADR-001).
/// </summary>
public sealed class RoslynMetadataExtractor(RoslynModelInspector inspector) : IRoslynMetadataExtractor
{
    private readonly RoslynModelInspector _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));

    public RoslynMetadataExtractor() : this(new RoslynModelInspector())
    {
    }

    /// <inheritdoc />
    public async Task<RoslynModelExtractionResult> ExtractAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(repositoryPath);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Каталог репозитория не найден: {fullPath}");
        }

        return await _inspector.InspectDirectoryAsync(fullPath, cancellationToken);
    }

    /// <inheritdoc />
    public RoslynModelExtractionResult ExtractFromSources(
        IEnumerable<(string SourceText, string FilePath)> sources,
        string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return _inspector.InspectSources(sources, baseDirectory);
    }

    /// <inheritdoc />
    public RoslynModelExtractionResult ExtractFromSource(
        string sourceCode,
        string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        return _inspector.InspectSource(sourceCode, filePath);
    }
}
