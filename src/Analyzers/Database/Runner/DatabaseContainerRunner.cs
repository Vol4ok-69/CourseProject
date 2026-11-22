using System.Text.Json;
using Application.Analyzers;
using DatabaseAnalyzer.Contracts;
using DatabaseAnalyzer.Metadata;
using DatabaseAnalyzer.Process;
using DatabaseAnalyzer.Roslyn;
using Domain.Enums;

namespace DatabaseAnalyzer.Runner;

/// <summary>
/// Исполнитель анализа базы данных в изолированном контейнере.
/// Управляет жизненным циклом рабочего каталога обмена внутри защитной области lease guard (TD-003).
/// </summary>
public sealed class DatabaseContainerRunner : IDatabaseContainerRunner
{
    private const long MaxOutputFileSizeBytes = 10 * 1024 * 1024; // 10 MB limit
    private readonly IContainerProcessAdapter _processAdapter;
    private readonly string _image;
    private readonly IAnalysisLeaseGuardProvider? _leaseGuardProvider;
    private readonly string? _exchangeVolume;

    public DatabaseContainerRunner(
        IContainerProcessAdapter processAdapter,
        string image = "netscope-database-analyzer:latest",
        IAnalysisLeaseGuardProvider? leaseGuardProvider = null,
        string? exchangeVolume = null)
    {
        _processAdapter = processAdapter ?? throw new ArgumentNullException(nameof(processAdapter));
        _image = image;
        _leaseGuardProvider = leaseGuardProvider;
        _exchangeVolume = exchangeVolume;
    }

    /// <summary>
    /// Конструктор обратной совместимости с ILeaseOwnershipValidator.
    /// Оборачивает валидатор в адаптер IAnalysisLeaseGuardProvider.
    /// </summary>
    public DatabaseContainerRunner(
        IContainerProcessAdapter processAdapter,
        string image,
        ILeaseOwnershipValidator? ownershipValidator)
        : this(processAdapter, image, ownershipValidator != null ? new LeaseOwnershipValidatorGuardAdapter(ownershipValidator) : null, null)
    {
    }

    public async Task<ContainerAnalysisResult> RunAnalysisAsync(
        RoslynModelExtractionResult extraction,
        int analysisId,
        Guid executionToken,
        CancellationToken cancellationToken = default)
    {
        if (_leaseGuardProvider != null)
        {
            await using var fallbackGuard = await _leaseGuardProvider.AcquireGuardAsync(analysisId, executionToken, cancellationToken);
            return await RunAnalysisAsync(extraction, analysisId, executionToken, fallbackGuard, cancellationToken);
        }

        return await RunAnalysisAsync(extraction, analysisId, executionToken, leaseGuard: null!, cancellationToken);
    }

    public async Task<ContainerAnalysisResult> RunAnalysisAsync(
        RoslynModelExtractionResult extraction,
        int analysisId,
        Guid executionToken,
        Application.Interfaces.IAnalysisLeaseGuard leaseGuard,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(analysisId);
        if (executionToken == Guid.Empty)
        {
            throw new ArgumentException("Execution token cannot be empty.", nameof(executionToken));
        }

        if (leaseGuard == null ||
            !leaseGuard.IsAcquired ||
            leaseGuard.AnalysisId != analysisId ||
            leaseGuard.ExecutionToken != executionToken)
        {
            return new ContainerAnalysisResult
            {
                Status = "Failed",
                ErrorMessage = "Active workspace lease guard is missing or does not match the analysis execution."
            };
        }

        var isOwner = await leaseGuard.ValidateOwnershipAsync(
            [(int)AnalysisStatus.Running],
            cancellationToken);

        if (!isOwner)
        {
            return new ContainerAnalysisResult
            {
                Status = "Cancelled",
                ErrorMessage = "Workspace ownership has been lost."
            };
        }

        var setupDiagnostics = new List<string>();
        var preserveExchangeDirectory = false;
        var exchangeDirectory = Path.Combine(
            Path.GetTempPath(),
            "netscope-database-analyzer",
            $"{analysisId}_{executionToken:N}");

        var inputDirectory = Path.Combine(exchangeDirectory, "in");
        var outputDirectory = Path.Combine(exchangeDirectory, "out");

        try
        {
            Directory.CreateDirectory(inputDirectory);
            Directory.CreateDirectory(outputDirectory);

            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    // Входной каталог: владелец app (1654) имеет rwx, группа и остальные (включая appuser 10001) - r-x (0755).
                    // Права на запись для Other исключены; контейнер монтирует этот каталог в режиме readonly.
                    File.SetUnixFileMode(
                        inputDirectory,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

                    // Выходной каталог: владелец app (1654) имеет rwx. Непривилегированный контейнер анализатора
                    // выполняется с UID 10001 (appuser) и должен иметь возможность создать output.json.
                    // В рамках стандартной модели Linux DAC межпользовательская запись без эскалации root
                    // требует прав rwx для Other (0777). Изоляция от других задач анализа обеспечивается
                    // тем, что Docker монтирует в конкретный контейнер только его собственный volume-subpath.
                    File.SetUnixFileMode(
                        outputDirectory,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
                }
                catch (PlatformNotSupportedException)
                {
                    // Файловая система или платформа не поддерживает POSIX-права
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    setupDiagnostics.Add($"Предупреждение: не удалось настроить права каталогов обмена: {ex.Message}");
                }
            }

            var inputFilePath = Path.Combine(inputDirectory, "input.json");
            var outputFilePath = Path.Combine(outputDirectory, "output.json");

            var inputDoc = BuildInputDocument(extraction, analysisId, executionToken);
            var inputJson = JsonSerializer.Serialize(inputDoc, DatabaseAnalyzerJsonContext.Default.DatabaseAnalyzerInputDocument);
            await File.WriteAllTextAsync(inputFilePath, inputJson, cancellationToken);

            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    // Входной файл: rw-r--r-- (0644), только чтение для пользователя контейнера
                    File.SetUnixFileMode(
                        inputFilePath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite |
                        UnixFileMode.GroupRead |
                        UnixFileMode.OtherRead);
                }
                catch (PlatformNotSupportedException)
                {
                    // Игнорируем на неподдерживающих платформах
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    setupDiagnostics.Add($"Предупреждение: не удалось настроить права входного файла: {ex.Message}");
                }
            }

            var runOptions = new ContainerRunOptions
            {
                Image = _image,
                InputFilePath = inputFilePath,
                OutputFilePath = outputFilePath,
                ExchangeDirectory = exchangeDirectory,
                ExchangeVolume = _exchangeVolume,
                Timeout = TimeSpan.FromSeconds(60)
            };

            var execResult = await _processAdapter.RunAsync(runOptions, cancellationToken);

            if (execResult.IsTimedOut)
            {
                return new ContainerAnalysisResult
                {
                    Status = "TimedOut",
                    ErrorMessage = "Database analyzer container execution timed out."
                };
            }

            if (execResult.IsCancelled)
            {
                return new ContainerAnalysisResult
                {
                    Status = "Cancelled",
                    ErrorMessage = "Database analyzer container execution was cancelled."
                };
            }

            if (execResult.IsOomKilled)
            {
                return new ContainerAnalysisResult
                {
                    Status = "Failed",
                    ErrorMessage = "Database analyzer container was terminated due to memory limit (OOMKilled)."
                };
            }

            cancellationToken.ThrowIfCancellationRequested();

            isOwner = await leaseGuard.ValidateOwnershipAsync(
                [(int)AnalysisStatus.Running],
                cancellationToken);

            if (!isOwner)
            {
                preserveExchangeDirectory = true;
                return new ContainerAnalysisResult
                {
                    Status = "Cancelled",
                    ErrorMessage = "Workspace ownership has been lost."
                };
            }

            if (!File.Exists(outputFilePath))
            {
                return new ContainerAnalysisResult
                {
                    Status = "Failed",
                    ErrorMessage = $"Output file was not produced by runner (ExitCode: {execResult.ExitCode}). Error: {execResult.StandardError}"
                };
            }

            var fileInfo = new FileInfo(outputFilePath);

            // Защита от symlinks и точек повторной обработки
            if (fileInfo.LinkTarget != null || fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("Output file is a symbolic link or reparse point. Possible path traversal attempt.");
            }

            // Защита от избыточного размера выходных данных
            if (fileInfo.Length > MaxOutputFileSizeBytes)
            {
                throw new InvalidOperationException($"Output file size ({fileInfo.Length} bytes) exceeds maximum allowed limit of {MaxOutputFileSizeBytes} bytes.");
            }

            var outputJson = await File.ReadAllTextAsync(outputFilePath, cancellationToken);
            var outputDoc = JsonSerializer.Deserialize(outputJson, DatabaseAnalyzerJsonContext.Default.DatabaseAnalyzerOutputDocument);

            if (outputDoc == null)
            {
                return new ContainerAnalysisResult
                {
                    Status = "Failed",
                    ErrorMessage = "Failed to deserialize output document from runner."
                };
            }

            // Валидация ExecutionToken и AnalysisId в документе вывода
            if (outputDoc.AnalysisId != analysisId || outputDoc.ExecutionToken != executionToken)
            {
                preserveExchangeDirectory = true;
                throw new InvalidOperationException(
                    $"Output token mismatch. Expected AnalysisId: {analysisId}, Token: {executionToken}; Actual: {outputDoc.AnalysisId}, Token: {outputDoc.ExecutionToken}");
            }

            var findings = new List<AnalyzerFinding>();
            foreach (var findingDto in outputDoc.Findings)
            {
                findings.Add(new AnalyzerFinding
                {
                    Rule = findingDto.RuleId,
                    FilePath = findingDto.Location?.FilePath ?? string.Empty,
                    LineNumber = findingDto.Location?.StartLine ?? 0,
                    Message = findingDto.Message,
                    Severity = MapSeverity(findingDto.Severity),
                    Recommendation = findingDto.Recommendation
                });
            }

            var diagnostics = outputDoc.Diagnostics.Select(d => $"[{d.Code}] {d.Message}").ToList();
            diagnostics.AddRange(setupDiagnostics);

            return new ContainerAnalysisResult
            {
                Status = outputDoc.Status,
                Findings = findings,
                Diagnostics = diagnostics
            };
        }
        finally
        {
            var canDelete = false;
            if (!preserveExchangeDirectory)
            {
                try
                {
                    canDelete = await leaseGuard.ValidateOwnershipAsync(
                        [(int)AnalysisStatus.Running],
                        CancellationToken.None);
                }
                catch
                {
                    canDelete = false;
                }
            }

            if (canDelete)
            {
                // Очищаем только временный каталог обмена контейнера конкретного токена
                // Не удаляем рабочий каталог репозитория анализа!
                // Не вызываем DisposeAsync на leaseGuard!
                try
                {
                    if (Directory.Exists(exchangeDirectory))
                    {
                        var dirInfo = new DirectoryInfo(exchangeDirectory);
                        if (!dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            Directory.Delete(exchangeDirectory, recursive: true);
                        }
                    }
                }
                catch
                {
                }
            }
        }
    }

    public static DatabaseAnalyzerInputDocument BuildInputDocument(
        RoslynModelExtractionResult extraction,
        int analysisId,
        Guid executionToken)
    {
        var input = new DatabaseAnalyzerInputDocument
        {
            SchemaVersion = "1.0",
            AnalysisId = analysisId,
            ExecutionToken = executionToken,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            MetadataCapabilities = extraction.MetadataCapabilities.ToList(),
            Configuration = new DatabaseAnalyzerConfigurationDto
            {
                EnabledRules = ["DB01", "DB02", "DB03", "DB04", "DB05"],
                MaxFindings = 1000
            },
            Metadata = new DatabaseAnalyzerMetadataDto()
        };

        foreach (var ctx in extraction.Contexts)
        {
            var contextId = !string.IsNullOrWhiteSpace(ctx.Namespace) ? $"{ctx.Namespace}.{ctx.Name}" : ctx.Name;
            var ctxDto = new DatabaseAnalyzerDbContextDto
            {
                Name = ctx.Name,
                ContextId = contextId,
                PrimaryLocation = MapLocation(ctx.Location),
                UnsupportedFeatures = ctx.UnsupportedFeatures.ToList()
            };

            foreach (var discoveredEntity in ctx.Entities)
            {
                var entity = discoveredEntity.ToDatabaseEntityMetadata();
                var entityId = !string.IsNullOrWhiteSpace(entity.ClrTypeName) ? entity.ClrTypeName : entity.EntityName;
                var entityDto = new DatabaseAnalyzerEntityDto
                {
                    Name = entity.EntityName,
                    EntityId = entityId,
                    ClrTypeName = entity.ClrTypeName,
                    TableName = entity.TableName,
                    SchemaName = entity.SchemaName,
                    MappingType = entity.MappingType.ToString(),
                    KeyType = entity.KeyType.ToString(),
                    ViewName = entity.ViewName,
                    SqlQuery = entity.SqlQuery,
                    FunctionName = entity.FunctionName,
                    IsSharedType = entity.IsSharedType,
                    IsDerivedEntity = entity.IsDerivedEntity,
                    BaseEntityName = entity.BaseEntityName,
                    HasIncompleteMetadata = entity.HasIncompleteMetadata,
                    HasIncompleteKeys = entity.HasIncompleteKeys || (entity.HasIncompleteMetadata && entity.Keys.Count == 0),
                    HasIncompleteForeignKeys = entity.HasIncompleteForeignKeys,
                    HasIncompleteIndexes = entity.HasIncompleteIndexes,
                    HasIncompleteNavigations = entity.HasIncompleteNavigations,
                    HasIncompleteSkipNavigations = entity.HasIncompleteSkipNavigations,
                    PrimaryLocation = MapLocation(entity.PrimaryLocation),
                    UnsupportedFeatures = entity.UnsupportedFeatures.ToList(),
                    Properties = entity.Properties.Select(p => new DatabaseAnalyzerPropertyDto
                    {
                        Name = p.PropertyName,
                        ColumnName = p.ColumnName,
                        ClrTypeName = p.ClrTypeName,
                        IsPrimaryKey = p.IsPrimaryKey,
                        IsForeignKey = false,
                        IsShadowProperty = p.IsShadowProperty,
                        IsConcurrencyToken = p.IsConcurrencyToken,
                        IsRequired = !p.IsNullable,
                        Location = MapLocation(p.Location)
                    }).ToList(),
                    Keys = entity.Keys.Select(k => new DatabaseAnalyzerKeyDto
                    {
                        Properties = k.PropertyNames.ToList(),
                        IsPrimaryKey = k.IsPrimaryKey,
                        IsExplicitName = k.IsExplicitName,
                        Location = MapLocation(k.Location)
                    }).ToList(),
                    ForeignKeys = entity.ForeignKeys.Select(fk => new DatabaseAnalyzerForeignKeyDto
                    {
                        PrincipalEntityName = fk.PrincipalEntityName,
                        Properties = fk.PropertyNames.ToList(),
                        PrincipalProperties = fk.PrincipalPropertyNames.ToList(),
                        NavigationPropertyName = fk.NavigationToPrincipal,
                        NavigationToPrincipal = fk.NavigationToPrincipal,
                        NavigationToDependent = fk.NavigationToDependent,
                        DeleteBehavior = fk.DeleteBehavior,
                        IsExplicitName = fk.IsExplicitName,
                        IsNavigationResolved = fk.IsNavigationResolved,
                        Location = MapLocation(fk.Location)
                    }).ToList(),
                    Indexes = entity.Indexes.Select(idx => new DatabaseAnalyzerIndexDto
                    {
                        Name = idx.Name,
                        Properties = idx.PropertyNames.ToList(),
                        IsUnique = idx.IsUnique,
                        Filter = idx.Filter,
                        IsExplicitName = idx.IsExplicitName,
                        Location = MapLocation(idx.Location)
                    }).ToList(),
                    SkipNavigations = entity.SkipNavigations.Select(sn => new DatabaseAnalyzerSkipNavigationDto
                    {
                        NavigationPropertyName = sn.NavigationName,
                        TargetEntityName = sn.TargetEntityName,
                        TargetEntityId = sn.TargetEntityId,
                        JoinEntityName = sn.JoinEntityName,
                        JoinEntityId = sn.JoinEntityId,
                        InverseNavigationPropertyName = sn.InverseNavigationName,
                        Location = MapLocation(sn.Location)
                    }).ToList()
                };

                ctxDto.Entities.Add(entityDto);
            }

            input.Metadata.DbContexts.Add(ctxDto);
        }

        return input;
    }

    private static DatabaseAnalyzerSourceLocationDto? MapLocation(SourceLocation? loc)
    {
        if (loc == null)
        {
            return null;
        }

        return new DatabaseAnalyzerSourceLocationDto
        {
            FilePath = loc.FilePath,
            StartLine = loc.LineNumber ?? 0,
            StartColumn = loc.ColumnNumber ?? 0,
            EndLine = loc.LineNumber ?? 0,
            EndColumn = loc.ColumnNumber ?? 0
        };
    }

    private static Severity MapSeverity(string severity)
    {
        return severity.ToLowerInvariant() switch
        {
            "info" => Severity.Info,
            "warning" => Severity.Warning,
            "error" => Severity.Error,
            "critical" => Severity.Critical,
            _ => Severity.Warning
        };
    }

    private sealed class LeaseOwnershipValidatorGuardAdapter(ILeaseOwnershipValidator validator) : IAnalysisLeaseGuardProvider
    {
        public async Task<IAnalysisLeaseGuard> AcquireGuardAsync(
            int analysisId,
            Guid executionToken,
            CancellationToken cancellationToken = default)
        {
            var isOwner = await validator.ValidateOwnershipAsync(analysisId, executionToken, cancellationToken);
            return new DelegatingLeaseGuard(analysisId, executionToken, isOwner, validator);
        }

        private sealed class DelegatingLeaseGuard(
            int analysisId,
            Guid executionToken,
            bool isAcquired,
            ILeaseOwnershipValidator validator) : IAnalysisLeaseGuard
        {
            public int AnalysisId => analysisId;
            public Guid ExecutionToken => executionToken;
            public bool IsAcquired => isAcquired;

            public Task<bool> ValidateOwnershipAsync(CancellationToken cancellationToken = default)
            {
                return validator.ValidateOwnershipAsync(analysisId, executionToken, cancellationToken);
            }

            public Task<bool> ValidateOwnershipAsync(int[] allowedStatuses, CancellationToken cancellationToken = default)
            {
                return ValidateOwnershipAsync(cancellationToken);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
