using DatabaseAnalyzer.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DatabaseAnalyzer.Roslyn;

public sealed class RoslynModelInspector
{
    public const long MaxSingleFileSizeBytes = 2 * 1024 * 1024; // 2 MiB (SEC-01)
    public const long MaxTotalSourceSizeBytes = 100 * 1024 * 1024; // 100 MiB (SEC-01)

    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        ".idea",
        ".vscode",
        "bin",
        "obj",
        "packages",
        "node_modules",
        "TestResults",
        "artifacts"
    };

    public RoslynModelExtractionResult InspectSource(string sourceCode, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);

        var path = filePath ?? "InMemory.cs";
        return InspectSources([(sourceCode, path)]);
    }

    public RoslynModelExtractionResult InspectSources(
        IEnumerable<(string SourceText, string FilePath)> sources,
        string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var warnings = new List<string>();
        var parsedFiles = new List<(ParsedFileSyntax FileSyntax, SyntaxTree Tree)>();
        var globalAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var globalImportedNamespaces = new HashSet<string>(StringComparer.Ordinal);
        var isEfCoreGloballyImported = false;
        var isDataAnnotationsGloballyImported = false;
        var isSchemaGloballyImported = false;

        long totalSourceBytes = 0;

        // Первый проход: парсинг синтаксических деревьев и сбор глобальных using/псевдонимов.
        foreach (var (sourceText, filePath) in sources)
        {
            var displayPath = NormalizeDisplayPath(filePath, baseDirectory);

            var fileBytes = System.Text.Encoding.UTF8.GetByteCount(sourceText);
            if (fileBytes > MaxSingleFileSizeBytes)
            {
                warnings.Add($"Файл '{displayPath}' превышает максимально допустимый размер (2 МБ) и пропущен.");
                continue;
            }

            if (totalSourceBytes + fileBytes > MaxTotalSourceSizeBytes)
            {
                warnings.Add($"Превышен суммарный лимит размера исходных файлов (100 МБ). Обработка последующих файлов остановлена.");
                break;
            }

            totalSourceBytes += fileBytes;

            var tree = CSharpSyntaxTree.ParseText(sourceText, path: filePath);

            var errors = tree.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .ToList();

            if (errors.Count > 0)
            {
                warnings.Add($"Файл '{displayPath}' содержит синтаксические ошибки: {errors[0].GetMessage()}");
            }

            var fileSyntax = new ParsedFileSyntax
            {
                FilePath = filePath
            };

            var root = tree.GetRoot();

            var visitor = new DbContextSyntaxVisitor(
                fileSyntax,
                onGlobalUsingFound: ns =>
                {
                    globalImportedNamespaces.Add(ns);

                    if (string.Equals(ns, "Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
                        string.Equals(ns, "global::Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
                    {
                        isEfCoreGloballyImported = true;
                    }
                    else if (string.Equals(ns, "System.ComponentModel.DataAnnotations", StringComparison.Ordinal) ||
                             string.Equals(ns, "global::System.ComponentModel.DataAnnotations", StringComparison.Ordinal))
                    {
                        isDataAnnotationsGloballyImported = true;
                    }
                    else if (string.Equals(ns, "System.ComponentModel.DataAnnotations.Schema", StringComparison.Ordinal) ||
                             string.Equals(ns, "global::System.ComponentModel.DataAnnotations.Schema", StringComparison.Ordinal))
                    {
                        isSchemaGloballyImported = true;
                    }
                },
                onGlobalAliasFound: (alias, target) =>
                {
                    globalAliases[alias] = target;
                });

            visitor.Visit(root);

            parsedFiles.Add((fileSyntax, tree));
        }

        // Индексация всех классов проекта
        var allCandidates = new List<ClassCandidate>();
        var groupedClasses = new Dictionary<(string Namespace, string ClassName), List<ClassCandidate>>();
        var entityTypeConfigurations = new List<DiscoveredConfiguration>();

        foreach (var (fileSyntax, _) in parsedFiles)
        {
            foreach (var candidate in fileSyntax.Classes)
            {
                allCandidates.Add(candidate);

                var key = (candidate.Namespace ?? string.Empty, candidate.ClassName);

                if (!groupedClasses.TryGetValue(key, out var list))
                {
                    list = [];
                    groupedClasses[key] = list;
                }

                list.Add(candidate);

                // Поиск IEntityTypeConfiguration<T>
                if (TryExtractEntityTypeConfiguration(candidate, out var config) && config is not null)
                {
                    entityTypeConfigurations.Add(config);
                }
            }
        }

        var allInterfaceNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (fileSyntax, _) in parsedFiles)
        {
            foreach (var iface in fileSyntax.Interfaces)
            {
                allInterfaceNames.Add(iface);
            }
        }

        var dataAnnotationParser = new DataAnnotationParser(
            isEfCoreGloballyImported,
            isDataAnnotationsGloballyImported,
            isSchemaGloballyImported,
            globalAliases,
            warnings,
            allCandidates,
            globalImportedNamespaces,
            allInterfaceNames);

        var discoveredContexts = new List<DiscoveredDbContext>();

        // Второй проход: обнаружение DbContext, извлечение DbSet, Fluent API и Data Annotations
        foreach (var ((ns, className), candidates) in groupedClasses)
        {
            var isDbContext = candidates.Any(candidate =>
                DbContextSyntaxVisitor.IsDbContext(candidate, isEfCoreGloballyImported, globalAliases));

            if (!isDbContext)
            {
                continue;
            }

            var entityMap = new Dictionary<string, DiscoveredEntity>(StringComparer.Ordinal);

            DiscoveredEntity GetOrAddEntity(string entityName)
            {
                if (!entityMap.TryGetValue(entityName, out var entity))
                {
                    entity = new DiscoveredEntity
                    {
                        EntityName = entityName
                    };
                    entityMap[entityName] = entity;
                }
                return entity;
            }

            string? primaryFilePath = null;

            // 1. Извлечение сущностей из свойств DbSet<T>
            foreach (var candidate in candidates)
            {
                primaryFilePath ??= candidate.FilePath;

                var dbSetEntities = DbContextSyntaxVisitor.ExtractDbSetEntities(
                    candidate.SyntaxNode,
                    candidate.FileSyntax,
                    isEfCoreGloballyImported,
                    globalAliases);

                foreach (var dbSetEntity in dbSetEntities)
                {
                    var entity = GetOrAddEntity(dbSetEntity.EntityName);
                    entity.DbSetPropertyName ??= dbSetEntity.DbSetPropertyName;
                }
            }

            // 2. Извлечение Fluent API из OnModelCreating и локальных методов конфигурации
            var localMethodTraverser = new LocalMethodTraverser(
                candidates.Select(c => c.SyntaxNode),
                warnings);

            var fluentApiParser = new FluentApiParser(GetOrAddEntity, warnings, () => entityMap.Values);

            foreach (var candidate in candidates)
            {
                foreach (var method in candidate.SyntaxNode.Members.OfType<MethodDeclarationSyntax>())
                {
                    if (string.Equals(method.Identifier.ValueText, "OnModelCreating", StringComparison.Ordinal))
                    {
                        fluentApiParser.ParseOnModelCreating(method, localMethodTraverser);
                    }
                }
            }

            // 3. Применение обнаруженных IEntityTypeConfiguration<T> (DEF-003)
            // Применяются только те конфигурации, для которых в данном DbContext был явный вызов modelBuilder.ApplyConfiguration(...)
            foreach (var config in entityTypeConfigurations)
            {
                var isExplicitlyApplied = fluentApiParser.AppliedConfigurationClassNames.Contains(config.Candidate.ClassName);

                if (isExplicitlyApplied && entityMap.ContainsKey(config.TargetEntityName))
                {
                    fluentApiParser.ParseEntityTypeConfiguration(config.ConfigureMethod, config.TargetEntityName);
                }
            }

            // 4. Извлечение Data Annotations из классов объявленных сущностей
            // DEF-002: Разрешение коллизий одинаковых коротких имён классов в разных пространствах имён
            var contextNamespaces = candidates
                .Select(c => c.Namespace)
                .OfType<string>()
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);

            var contextImportedNamespaces = candidates
                .SelectMany(c => c.FileSyntax.ImportedNamespaces)
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);

            var contextAliases = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var c in candidates)
            {
                foreach (var (alias, target) in c.FileSyntax.Aliases)
                {
                    contextAliases[alias] = target;
                }
            }

            foreach (var entity in entityMap.Values.ToList())
            {
                var matchingCandidates = allCandidates
                    .Where(c => string.Equals(c.ClassName, entity.EntityName, StringComparison.Ordinal))
                    .ToList();

                if (matchingCandidates.Count == 0)
                {
                    continue;
                }

                List<ClassCandidate> resolvedCandidates;

                if (matchingCandidates.Count == 1)
                {
                    resolvedCandidates = matchingCandidates;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(entity.FullEntityTypeName) && entity.FullEntityTypeName.Contains('.'))
                    {
                        var exactMatches = matchingCandidates.Where(c =>
                            string.Equals(
                                (!string.IsNullOrEmpty(c.Namespace) ? c.Namespace + "." : string.Empty) + c.ClassName,
                                entity.FullEntityTypeName,
                                StringComparison.Ordinal)).ToList();

                        if (exactMatches.Count > 0)
                        {
                            resolvedCandidates = exactMatches;
                        }
                        else
                        {
                            resolvedCandidates = DisambiguateCandidates(
                                entity.EntityName,
                                matchingCandidates,
                                contextNamespaces,
                                contextImportedNamespaces,
                                globalImportedNamespaces,
                                contextAliases,
                                globalAliases,
                                className,
                                warnings);
                        }
                    }
                    else
                    {
                        resolvedCandidates = DisambiguateCandidates(
                            entity.EntityName,
                            matchingCandidates,
                            contextNamespaces,
                            contextImportedNamespaces,
                            globalImportedNamespaces,
                            contextAliases,
                            globalAliases,
                            className,
                            warnings);
                    }
                }

                foreach (var entityCandidate in resolvedCandidates)
                {
                    dataAnnotationParser.ParseEntityAnnotations(
                        entityCandidate.SyntaxNode,
                        entityCandidate.FileSyntax,
                        entity);
                }

                // 5. EF Core соглашение: если первичный ключ не задан явно,
                // проверяем наличие свойства Id или {EntityName}Id
                if (entity.Keys.Count == 0)
                {
                    var idProp = entity.Properties.FirstOrDefault(p =>
                        string.Equals(p.PropertyName, "Id", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.PropertyName, entity.EntityName + "Id", StringComparison.OrdinalIgnoreCase));

                    if (idProp is not null)
                    {
                        var keyName = $"PK_{entity.TableName ?? entity.EntityName}";
                        entity.AddKey(new DatabaseKeyMetadata
                        {
                            Name = keyName,
                            PropertyNames = [idProp.PropertyName],
                            IsPrimaryKey = true
                        });
                    }
                }

                // 6. DEF-005: Моделирование ForeignKeyIndexConvention EF Core
                // В EF Core для каждого внешнего ключа автоматически создаётся индекс по столбцам FK,
                // если отсутствует подходящий индекс, начинающийся с этих столбцов в том же порядке.
                foreach (var fk in entity.ForeignKeys)
                {
                    var fkProperties = fk.PropertyNames;
                    if (fkProperties.Count == 0)
                    {
                        continue;
                    }

                    var hasMatchingIndex = entity.Indexes.Any(index =>
                        index.PropertyNames.Count >= fkProperties.Count &&
                        index.PropertyNames
                            .Take(fkProperties.Count)
                            .SequenceEqual(fkProperties, StringComparer.Ordinal));

                    if (!hasMatchingIndex)
                    {
                        var targetName = entity.TableName ?? entity.EntityName;
                        var indexName = $"IX_{targetName}_{string.Join("_", fkProperties)}";

                        entity.AddIndex(new DatabaseIndexMetadata
                        {
                            Name = indexName,
                            PropertyNames = fkProperties,
                            IsUnique = false
                        });
                    }
                }
            }

            discoveredContexts.Add(new DiscoveredDbContext
            {
                Name = className,
                Namespace = string.IsNullOrEmpty(ns) ? null : ns,
                FilePath = primaryFilePath,
                Entities = entityMap.Values.ToList()
            });
        }

        return new RoslynModelExtractionResult
        {
            Contexts = discoveredContexts,
            Warnings = warnings
        };
    }

    public async Task<RoslynModelExtractionResult> InspectDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(directoryPath);

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Directory path not found: {fullPath}");
        }

        var files = EnumerateSourceFiles(fullPath, fullPath, cancellationToken).ToList();

        return await InspectFilesAsync(files, fullPath, cancellationToken);
    }

    public Task<RoslynModelExtractionResult> InspectFilesAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        return InspectFilesAsync(filePaths, baseDirectory: null, cancellationToken);
    }

    public async Task<RoslynModelExtractionResult> InspectFilesAsync(
        IEnumerable<string> filePaths,
        string? baseDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var sources = new List<(string SourceText, string FilePath)>();
        var warnings = new List<string>();
        long totalBytes = 0;

        foreach (var file in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var displayPath = NormalizeDisplayPath(file, baseDirectory);

            try
            {
                var fileInfo = new FileInfo(file);
                if (!fileInfo.Exists)
                {
                    warnings.Add($"Файл '{displayPath}' не существует.");
                    continue;
                }

                if (fileInfo.Length > MaxSingleFileSizeBytes)
                {
                    warnings.Add($"Файл '{displayPath}' превышает максимально допустимый размер (2 МБ) и пропущен.");
                    continue;
                }

                if (totalBytes + fileInfo.Length > MaxTotalSourceSizeBytes)
                {
                    warnings.Add($"Превышен суммарный лимит размера исходных файлов (100 МБ). Чтение файлов остановлено.");
                    break;
                }

                var content = await File.ReadAllTextAsync(file, cancellationToken);
                totalBytes += fileInfo.Length;
                sources.Add((content, file));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Не удалось прочитать файл '{displayPath}': {ex.Message}");
            }
        }

        var result = InspectSources(sources, baseDirectory);

        if (warnings.Count > 0)
        {
            var combinedWarnings = new List<string>(warnings);
            combinedWarnings.AddRange(result.Warnings);

            return new RoslynModelExtractionResult
            {
                Contexts = result.Contexts,
                Warnings = combinedWarnings
            };
        }

        return result;
    }

    public static string NormalizeDisplayPath(string filePath, string? baseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return filePath;
        }

        if (baseDirectory is not null && Path.IsPathRooted(filePath))
        {
            try
            {
                var relative = Path.GetRelativePath(baseDirectory, filePath);
                return relative.Replace('\\', '/');
            }
            catch
            {
                // fallback
            }
        }

        return filePath.Replace('\\', '/');
    }

    private static List<ClassCandidate> DisambiguateCandidates(
        string entityName,
        List<ClassCandidate> candidates,
        HashSet<string> contextNamespaces,
        HashSet<string> contextImportedNamespaces,
        HashSet<string> globalImportedNamespaces,
        IReadOnlyDictionary<string, string> contextAliases,
        IReadOnlyDictionary<string, string> globalAliases,
        string contextClassName,
        List<string> warnings)
    {
        var groups = candidates.GroupBy(c => c.Namespace ?? string.Empty).ToList();

        // Если все кандидаты находятся в одном пространстве имён (partial-классы одной сущности)
        if (groups.Count == 1)
        {
            return [.. groups[0]];
        }

        // 0. Проверяем наличие псевдонима типа (type alias) для имени сущности
        string? aliasTarget = null;
        if (contextAliases.TryGetValue(entityName, out var localTarget))
        {
            aliasTarget = localTarget;
        }
        else if (globalAliases.TryGetValue(entityName, out var globalTarget))
        {
            aliasTarget = globalTarget;
        }

        if (!string.IsNullOrWhiteSpace(aliasTarget))
        {
            if (aliasTarget.StartsWith("global::", StringComparison.Ordinal))
            {
                aliasTarget = aliasTarget["global::".Length..];
            }

            var aliasMatches = candidates.Where(c =>
            {
                var fullName = (!string.IsNullOrEmpty(c.Namespace) ? c.Namespace + "." : string.Empty) + c.ClassName;
                return string.Equals(fullName, aliasTarget, StringComparison.Ordinal);
            }).ToList();

            if (aliasMatches.Count > 0)
            {
                var matchedNamespace = aliasMatches[0].Namespace ?? string.Empty;
                return candidates.Where(c => string.Equals(c.Namespace ?? string.Empty, matchedNamespace, StringComparison.Ordinal)).ToList();
            }

            warnings.Add($"Псевдоним сущности '{entityName}' указывает на тип '{aliasTarget}', который не найден среди исходных файлов проекта.");
            return [];
        }

        // 1. Ищем namespace, совпадающий с пространством имён самого DbContext
        var sameNamespaceGroup = groups.FirstOrDefault(g => contextNamespaces.Contains(g.Key));
        if (sameNamespaceGroup is not null && groups.Count(g => contextNamespaces.Contains(g.Key)) == 1)
        {
            return [.. sameNamespaceGroup];
        }

        // 2. Ищем namespace среди импортированных в файле DbContext (using или global using)
        var importedGroups = groups.Where(g => contextImportedNamespaces.Contains(g.Key) || globalImportedNamespaces.Contains(g.Key)).ToList();
        if (importedGroups.Count == 1)
        {
            return [.. importedGroups[0]];
        }

        if (importedGroups.Count > 1)
        {
            var nsList = string.Join(", ", importedGroups.Select(g => string.IsNullOrEmpty(g.Key) ? "<global>" : g.Key));
            warnings.Add($"Сущность '{entityName}' найдена в нескольких импортированных пространствах имён ({nsList}) контекста '{contextClassName}'. Метаданные не извлечены во избежание коллизий.");
            return [];
        }

        // 3. Ни один namespace не импортирован явно в DbContext
        var allNsList = string.Join(", ", groups.Select(g => string.IsNullOrEmpty(g.Key) ? "<global>" : g.Key));
        warnings.Add($"Сущность '{entityName}' найдена в нескольких пространствах имён ({allNsList}), но ни одно из них не импортировано в контексте '{contextClassName}'. Метаданные не извлечены.");
        return [];
    }

    private static bool TryExtractEntityTypeConfiguration(
        ClassCandidate candidate,
        out DiscoveredConfiguration? configuration)
    {
        configuration = null;

        var baseList = candidate.SyntaxNode.BaseList;

        if (baseList is null || baseList.Types.Count == 0)
        {
            return false;
        }

        foreach (var baseType in baseList.Types)
        {
            GenericNameSyntax? generic = null;

            if (baseType.Type is GenericNameSyntax g)
            {
                generic = g;
            }
            else if (baseType.Type is QualifiedNameSyntax q && q.Right is GenericNameSyntax gRight)
            {
                generic = gRight;
            }

            if (generic is not null &&
                string.Equals(generic.Identifier.ValueText, "IEntityTypeConfiguration", StringComparison.Ordinal) &&
                generic.TypeArgumentList.Arguments.Count == 1)
            {
                var targetEntityName = ExtractTypeName(generic.TypeArgumentList.Arguments[0]);

                var configureMethod = candidate.SyntaxNode.Members
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => string.Equals(m.Identifier.ValueText, "Configure", StringComparison.Ordinal));

                if (configureMethod is not null)
                {
                    configuration = new DiscoveredConfiguration
                    {
                        TargetEntityName = targetEntityName,
                        ConfigureMethod = configureMethod,
                        Candidate = candidate
                    };
                    return true;
                }
            }
        }

        return false;
    }

    private static string ExtractTypeName(TypeSyntax typeSyntax)
    {
        return typeSyntax switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            GenericNameSyntax g => g.Identifier.ValueText,
            _ => typeSyntax.ToString().Trim()
        };
    }

    private static IEnumerable<string> EnumerateSourceFiles(
        string directory,
        string canonicalRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileAttributes fileAttributes;
            try
            {
                fileAttributes = File.GetAttributes(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if ((fileAttributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var canonicalFile = Path.GetFullPath(file);
            if (!IsUnderDirectory(canonicalFile, canonicalRoot))
            {
                continue;
            }

            yield return canonicalFile;
        }

        IEnumerable<string> subdirectories;
        try
        {
            subdirectories = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var subdirectory in subdirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directoryName = Path.GetFileName(subdirectory);

            if (IgnoredDirectoryNames.Contains(directoryName))
            {
                continue;
            }

            FileAttributes subAttributes;
            try
            {
                subAttributes = File.GetAttributes(subdirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if ((subAttributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var canonicalSubdir = Path.GetFullPath(subdirectory);
            if (!IsUnderDirectory(canonicalSubdir, canonicalRoot))
            {
                continue;
            }

            foreach (var file in EnumerateSourceFiles(canonicalSubdir, canonicalRoot, cancellationToken))
            {
                yield return file;
            }
        }
    }

    private static bool IsUnderDirectory(string candidatePath, string rootPath)
    {
        var rootWithSeparator = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return candidatePath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(candidatePath, rootPath, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DiscoveredConfiguration
    {
        public string TargetEntityName { get; init; } = null!;
        public MethodDeclarationSyntax ConfigureMethod { get; init; } = null!;
        public ClassCandidate Candidate { get; init; } = null!;
    }
}
