using Application.Analyzers;
using DatabaseAnalyzer.Roslyn;
using DatabaseAnalyzer.Runner;
using Domain.Enums;

namespace DatabaseAnalyzer;

/// <summary>
/// Анализатор базы данных.
/// В целевой архитектуре ADR-001 отвечает за координацию статического извлечения метаданных EF Core
/// и взаимодействие с изолированным контейнером анализа БД.
/// На этапе 1 (TD-001) выполняет безопасное Roslyn-извлечение метаданных без запуска сборки и динамической загрузки сборок.
/// Проверки реляционной модели DB01–DB05 в основном процессе backend строго запрещены (ADR-001 п. 2.1, 2.2).
/// При наличии подключённого IDatabaseContainerRunner делегирует выполнение правил DB01–DB05 изолированному контейнеру.
/// При отсутствии подключённого контейнера формирует явное диагностическое предупреждение.
/// </summary>
public sealed class DatabaseAnalyzer : IAnalyzer
{
    public const string ContainerUnavailableRule = "DB_CONTAINER_UNAVAILABLE";
    public const string MissingDbContextRule = "MISSING_DBCONTEXT";
    public const string RoslynWarningRule = "DB_ROSLYN_WARNING";
    public const string ExtractionErrorRule = "DB_EXTRACTION_ERROR";
    public const string ContainerExecutionErrorRule = "DB_CONTAINER_ERROR";

    private readonly IRoslynMetadataExtractor _metadataExtractor;

    public AnalyzerType Type => AnalyzerType.Database;

    /// <summary>
    /// Опциональный исполнитель изолированного контейнера базы данных.
    /// </summary>
    public IDatabaseContainerRunner? ContainerRunner { get; init; }

    /// <summary>
    /// Основной конструктор для production pipeline (ADR-001).
    /// Использует Roslyn-экстрактор для статического извлечения метаданных.
    /// Выполнение DB01–DB05 в основном процессе backend строго исключено (ADR-001).
    /// </summary>
    public DatabaseAnalyzer(IRoslynMetadataExtractor metadataExtractor)
    {
        _metadataExtractor = metadataExtractor ?? throw new ArgumentNullException(nameof(metadataExtractor));
    }

    public async Task<IReadOnlyList<AnalyzerFinding>> AnalyzeAsync(
        AnalyzerContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var findings = new List<AnalyzerFinding>();

        // Проверка пути к репозиторию
        if (string.IsNullOrWhiteSpace(context.RepositoryPath))
        {
            findings.Add(new AnalyzerFinding
            {
                Rule = ExtractionErrorRule,
                FilePath = string.Empty,
                LineNumber = 0,
                Message = "Каталог репозитория не указан (RepositoryPath пуст). Извлечение метаданных EF Core пропущено.",
                Severity = Severity.Warning,
                Recommendation = "Убедитесь, что репозиторий успешно загружен перед запуском анализа."
            });

            findings.Add(CreateContainerUnavailableFinding());
            return findings;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(context.RepositoryPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            findings.Add(new AnalyzerFinding
            {
                Rule = ExtractionErrorRule,
                FilePath = string.Empty,
                LineNumber = 0,
                Message = $"Некорректный путь к каталогу репозитория '{context.RepositoryPath}': {ex.Message}",
                Severity = Severity.Warning,
                Recommendation = "Проверьте корректность пути к рабочей директории анализа."
            });

            findings.Add(CreateContainerUnavailableFinding());
            return findings;
        }

        if (!Directory.Exists(fullPath))
        {
            findings.Add(new AnalyzerFinding
            {
                Rule = ExtractionErrorRule,
                FilePath = string.Empty,
                LineNumber = 0,
                Message = $"Каталог репозитория не найден: {fullPath}. Извлечение метаданных EF Core пропущено.",
                Severity = Severity.Warning,
                Recommendation = "Проверьте путь к рабочему каталогу репозитория и статус загрузки исходного кода."
            });

            findings.Add(CreateContainerUnavailableFinding());
            return findings;
        }

        RoslynModelExtractionResult extraction;
        try
        {
            extraction = await _metadataExtractor.ExtractAsync(fullPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            findings.Add(new AnalyzerFinding
            {
                Rule = ExtractionErrorRule,
                FilePath = string.Empty,
                LineNumber = 0,
                Message = $"Ошибка статического извлечения метаданных EF Core: {ex.Message}",
                Severity = Severity.Warning,
                Recommendation = "Проверьте структуру файлов C# и синтаксическую корректность моделей Entity Framework Core."
            });

            findings.Add(CreateContainerUnavailableFinding());
            return findings;
        }

        // 1. Предупреждения статического синтаксического анализа Roslyn
        foreach (var warning in extraction.Warnings)
        {
            findings.Add(new AnalyzerFinding
            {
                Rule = RoslynWarningRule,
                FilePath = string.Empty,
                LineNumber = 0,
                Message = warning,
                Severity = Severity.Warning,
                Recommendation = "Проверьте синтаксис C# и конфигурацию модели Entity Framework Core."
            });
        }

        // 2. Отсутствие DbContext в репозитории (ADR-001 / TD-002: Partial Success)
        if (extraction.Contexts.Count == 0)
        {
            findings.Add(new AnalyzerFinding
            {
                Rule = MissingDbContextRule,
                FilePath = string.Empty,
                LineNumber = 0,
                Message = "В проекте не найдено контекстов данных Entity Framework Core (DbContext). Проверки схемы базы данных пропущены.",
                Severity = Severity.Info,
                Recommendation = "Если в репозитории используется база данных, убедитесь, что класс контекста наследует Microsoft.EntityFrameworkCore.DbContext."
            });
        }

        // 3. Выполнение проверок DB01–DB05 через изолированный контейнер
        if (ContainerRunner == null)
        {
            // Диагностическое предупреждение о том, что контейнер не подключён
            findings.Add(CreateContainerUnavailableFinding());
            return findings;
        }

        if (context.LeaseGuard == null)
        {
            findings.Add(CreateContainerUnavailableFinding());
            return findings;
        }

        try
        {
            var analysisId = context.AnalysisId ?? 0;
            var executionToken = context.ExecutionToken ?? Guid.NewGuid();

            var containerResult = await ContainerRunner.RunAnalysisAsync(
                extraction,
                analysisId,
                executionToken,
                context.LeaseGuard,
                cancellationToken);

            if (containerResult.Status == "Success" || containerResult.Status == "PartialSuccess")
            {
                findings.AddRange(containerResult.Findings);

                foreach (var diag in containerResult.Diagnostics)
                {
                    findings.Add(new AnalyzerFinding
                    {
                        Rule = RoslynWarningRule,
                        FilePath = string.Empty,
                        LineNumber = 0,
                        Message = diag,
                        Severity = Severity.Info,
                        Recommendation = "Диагностическое сообщение от изолированного анализатора базы данных."
                    });
                }
            }
            else
            {
                findings.Add(new AnalyzerFinding
                {
                    Rule = ContainerExecutionErrorRule,
                    FilePath = string.Empty,
                    LineNumber = 0,
                    Message = $"Ошибка выполнения изолированного анализатора базы данных: {containerResult.ErrorMessage ?? containerResult.Status}",
                    Severity = Severity.Warning,
                    Recommendation = "Проверьте доступность контейнерной среды и корректность структуры моделей."
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            findings.Add(new AnalyzerFinding
            {
                Rule = ContainerExecutionErrorRule,
                FilePath = string.Empty,
                LineNumber = 0,
                Message = $"Исключение при вызове изолированного контейнерного анализатора: {ex.Message}",
                Severity = Severity.Warning,
                Recommendation = "Проверьте конфигурацию Docker и параметры запуска контейнера NetScope."
            });
        }

        return findings;
    }

    private static AnalyzerFinding CreateContainerUnavailableFinding()
    {
        return new AnalyzerFinding
        {
            Rule = ContainerUnavailableRule,
            FilePath = string.Empty,
            LineNumber = 0,
            Message = "Изолированный контейнерный анализатор базы данных временно не подключён. Проверки реляционной модели DB01–DB05 в основном процессе backend не выполнялись в соответствии с ADR-001.",
            Severity = Severity.Warning,
            Recommendation = "Для выполнения проверок структуры базы данных DB01–DB05 требуется подключение изолированного контейнерного анализатора (ADR-001). Метаданные моделей EF Core извлечены статически через Roslyn."
        };
    }
}
