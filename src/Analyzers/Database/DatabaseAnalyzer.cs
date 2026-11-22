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
public sealed class DatabaseAnalyzer : IDatabaseAnalyzer
{
    public const string ContainerUnavailableRule = DatabaseAnalyzerRules.ContainerUnavailable;
    public const string MissingDbContextRule = DatabaseAnalyzerRules.MissingDbContext;
    public const string RoslynWarningRule = DatabaseAnalyzerRules.RoslynWarning;
    public const string ExtractionErrorRule = DatabaseAnalyzerRules.ExtractionError;
    public const string ContainerExecutionErrorRule = DatabaseAnalyzerRules.ContainerError;
    public const string ContainerErrorRule = DatabaseAnalyzerRules.ContainerError;
    public const string ContainerTimeoutRule = DatabaseAnalyzerRules.ContainerTimeout;
    public const string InternalAnalyzerErrorRule = DatabaseAnalyzerRules.InternalAnalyzerError;
    public const string UnsupportedFluentApiRule = DatabaseAnalyzerRules.UnsupportedFluentApi;
    public const string SyntaxErrorRule = DatabaseAnalyzerRules.SyntaxError;
    public const string AmbiguousModelRule = DatabaseAnalyzerRules.AmbiguousModel;

    private readonly IRoslynMetadataExtractor _metadataExtractor;

    public AnalyzerType Type => AnalyzerType.Database;

    /// <summary>
    /// Фактический статус последнего выполнения анализатора в соответствии с ADR-001 / TD-002.
    /// </summary>
    public DatabaseAnalysisExecutionStatus ExecutionStatus { get; private set; } = DatabaseAnalysisExecutionStatus.NotStarted;

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
        ExecutionStatus = DatabaseAnalysisExecutionStatus.NotStarted;
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var findings = new List<AnalyzerFinding>();

        // Проверка пути к репозиторию
        if (string.IsNullOrWhiteSpace(context.RepositoryPath))
        {
            ExecutionStatus = DatabaseAnalysisExecutionStatus.ExtractionError;
            findings.Add(new AnalyzerFinding
            {
                Rule = ExtractionErrorRule,
                FilePath = string.Empty,
                LineNumber = 0,
                Message = "Каталог репозитория не указан (RepositoryPath пуст). Извлечение метаданных EF Core пропущено.",
                Severity = Severity.Warning,
                Recommendation = "Убедитесь, что репозиторий успешно загружен перед запуском анализа."
            });

            return findings;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(context.RepositoryPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ExecutionStatus = DatabaseAnalysisExecutionStatus.ExtractionError;
            findings.Add(new AnalyzerFinding
            {
                Rule = ExtractionErrorRule,
                FilePath = string.Empty,
                LineNumber = 0,
                Message = $"Некорректный путь к каталогу репозитория '{context.RepositoryPath}': {ex.Message}",
                Severity = Severity.Warning,
                Recommendation = "Проверьте корректность пути к рабочей директории анализа."
            });

            return findings;
        }

        if (!Directory.Exists(fullPath))
        {
            ExecutionStatus = DatabaseAnalysisExecutionStatus.ExtractionError;
            findings.Add(new AnalyzerFinding
            {
                Rule = ExtractionErrorRule,
                FilePath = string.Empty,
                LineNumber = 0,
                Message = $"Каталог репозитория не найден: {fullPath}. Извлечение метаданных EF Core пропущено.",
                Severity = Severity.Warning,
                Recommendation = "Проверьте путь к рабочему каталогу репозитория и статус загрузки исходного кода."
            });

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
        catch (Exception ex) when (ex is AggregateException agg && agg.Flatten().InnerExceptions.Any(e => e is OperationCanceledException))
        {
            throw;
        }
        catch (Exception ex) when (ex.InnerException is OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ExecutionStatus = DatabaseAnalysisExecutionStatus.ExtractionError;
            findings.Add(new AnalyzerFinding
            {
                Rule = ExtractionErrorRule,
                FilePath = string.Empty,
                LineNumber = 0,
                Message = $"Ошибка статического извлечения метаданных EF Core: {ex.Message}",
                Severity = Severity.Warning,
                Recommendation = "Проверьте структуру файлов C# и синтаксическую корректность моделей Entity Framework Core."
            });

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

        // 2. Отсутствие DbContext в репозитории (ADR-001 / TD-002: InformationSkipped)
        if (extraction.Contexts.Count == 0)
        {
            ExecutionStatus = DatabaseAnalysisExecutionStatus.InformationSkipped;
            findings.Add(new AnalyzerFinding
            {
                Rule = MissingDbContextRule,
                FilePath = string.Empty,
                LineNumber = 0,
                Message = "В проекте не найдено контекстов данных Entity Framework Core (DbContext). Проверки схемы базы данных пропущены.",
                Severity = Severity.Info,
                Recommendation = "Если в репозитории используется база данных, убедитесь, что класс контекста наследует Microsoft.EntityFrameworkCore.DbContext."
            });

            return findings;
        }

        // 3. Выполнение проверок DB01–DB05 через изолированный контейнер
        if (ContainerRunner == null)
        {
            ExecutionStatus = DatabaseAnalysisExecutionStatus.ContainerError;
            // Диагностическое предупреждение о том, что контейнер не подключён
            findings.Add(CreateContainerUnavailableFinding());
            return findings;
        }

        if (context.LeaseGuard == null)
        {
            ExecutionStatus = DatabaseAnalysisExecutionStatus.InternalError;
            findings.Add(new AnalyzerFinding
            {
                Rule = InternalAnalyzerErrorRule,
                FilePath = string.Empty,
                LineNumber = 0,
                Message = "Не предоставлен IAnalysisLeaseGuard для запуска контейнера базы данных.",
                Severity = Severity.Warning,
                Recommendation = "Проверьте инициализацию контекста выполнения анализа."
            });
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

            if (string.Equals(containerResult.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(containerResult.ErrorMessage ?? "Database analyzer container execution was cancelled.");
            }

            if (containerResult.Status == "Success" || containerResult.Status == "PartialSuccess")
            {
                ExecutionStatus = containerResult.Status == "Success"
                    ? DatabaseAnalysisExecutionStatus.Success
                    : DatabaseAnalysisExecutionStatus.PartialSuccess;

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
                var isTimeout = string.Equals(containerResult.Status, "TimedOut", StringComparison.OrdinalIgnoreCase);
                ExecutionStatus = isTimeout
                    ? DatabaseAnalysisExecutionStatus.ContainerTimeout
                    : DatabaseAnalysisExecutionStatus.ContainerError;

                var rule = isTimeout ? ContainerTimeoutRule : ContainerErrorRule;
                var message = isTimeout
                    ? "Превышено максимальное время выполнения изолированного анализатора базы данных (таймаут 60 с)."
                    : $"Ошибка выполнения изолированного анализатора базы данных: {containerResult.ErrorMessage ?? containerResult.Status}";
                var recommendation = isTimeout
                    ? "Увеличьте лимиты времени или проверьте размер и сложность анализируемой модели базы данных."
                    : "Проверьте доступность контейнерной среды и корректность структуры моделей.";

                findings.Add(new AnalyzerFinding
                {
                    Rule = rule,
                    FilePath = string.Empty,
                    LineNumber = 0,
                    Message = message,
                    Severity = Severity.Warning,
                    Recommendation = recommendation
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is AggregateException agg && agg.Flatten().InnerExceptions.Any(e => e is OperationCanceledException))
        {
            throw;
        }
        catch (Exception ex) when (ex.InnerException is OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ExecutionStatus = DatabaseAnalysisExecutionStatus.ContainerError;
            findings.Add(new AnalyzerFinding
            {
                Rule = ContainerErrorRule,
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
