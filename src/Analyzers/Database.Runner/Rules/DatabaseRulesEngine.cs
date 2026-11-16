using NetScope.DatabaseAnalyzer.Runner.Contracts;

namespace NetScope.DatabaseAnalyzer.Runner.Rules;

/// <summary>
/// Автономный изолированный движок правил DB01–DB05 для выполнения внутри контейнера.
/// Реализует нулевой общий код (Zero Shared Code) с бэкендом (ADR-001).
/// </summary>
public sealed class DatabaseRulesEngine
{
    private static readonly HashSet<string> SupportedRules = ["DB01", "DB02", "DB03", "DB04", "DB05"];

    public AnalysisOutputContract Evaluate(AnalysisInputContract input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var output = new AnalysisOutputContract
        {
            SchemaVersion = "1.0",
            AnalysisId = input.AnalysisId,
            ExecutionToken = input.ExecutionToken,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            AnalyzerVersion = "1.0.0",
            Status = "Success"
        };

        var enabledRules = new HashSet<string>(input.Configuration.EnabledRules ?? ["DB01", "DB02", "DB03", "DB04", "DB05"], StringComparer.OrdinalIgnoreCase);
        var metadataCaps = new HashSet<string>(input.MetadataCapabilities ?? [], StringComparer.OrdinalIgnoreCase);

        // Capabilities negotiation: проверяем наличие базовой возможности CoreRelational
        if (!metadataCaps.Contains("CoreRelational"))
        {
            output.Status = "Failed";
            output.Errors.Add(new ErrorDto
            {
                Code = "MISSING_BASELINE_CAPABILITY",
                Message = "Baseline metadata capability 'CoreRelational' is required for database analysis."
            });
            return output;
        }

        // Capabilities negotiation: проверяем поддержку запрошенных правил текущей версией Runner
        foreach (var rule in enabledRules)
        {
            if (!SupportedRules.Contains(rule))
            {
                output.Diagnostics.Add(new DiagnosticDto
                {
                    Code = "UNSUPPORTED_RULE_REQUESTED",
                    Message = $"Rule '{rule}' is not supported by Runner version {output.AnalyzerVersion}.",
                    RuleId = rule
                });
            }
        }

        var findings = new List<FindingDto>();
        var diagnostics = new List<DiagnosticDto>();
        var findingIdSequence = 1;
        var rulesEvaluatedCount = 0;
        var entitiesAnalyzedCount = 0;

        var processedManyToManyPairs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var dbContext in input.Metadata.DbContexts)
        {
            var effectiveContextId = !string.IsNullOrWhiteSpace(dbContext.ContextId) ? dbContext.ContextId : dbContext.Name;

            foreach (var entity in dbContext.Entities)
            {
                entitiesAnalyzedCount++;
                var entityId = entity.EntityId;
                var effectiveEntityId = !string.IsNullOrWhiteSpace(entityId)
                    ? entityId
                    : (!string.IsNullOrWhiteSpace(entity.ClrTypeName) ? entity.ClrTypeName : entity.Name);

                // DB01: Primary Key Required
                if (enabledRules.Contains("DB01"))
                {
                    rulesEvaluatedCount++;
                    EvaluateDb01(entity, effectiveContextId, effectiveEntityId, metadataCaps, findings, diagnostics, ref findingIdSequence, ref output);
                }

                // DB02: Missing Navigation to Principal
                if (enabledRules.Contains("DB02"))
                {
                    rulesEvaluatedCount++;
                    EvaluateDb02(entity, effectiveContextId, effectiveEntityId, metadataCaps, findings, diagnostics, ref findingIdSequence, ref output);
                }

                // DB03: Many-to-Many Pattern Detection
                if (enabledRules.Contains("DB03"))
                {
                    rulesEvaluatedCount++;
                    EvaluateDb03(entity, dbContext.ContextId, entity.EntityId, metadataCaps, processedManyToManyPairs, findings, diagnostics, ref findingIdSequence, ref output, effectiveContextId);
                }

                // DB04: Missing Foreign Key Index
                if (enabledRules.Contains("DB04"))
                {
                    rulesEvaluatedCount++;
                    EvaluateDb04(entity, effectiveContextId, effectiveEntityId, metadataCaps, findings, diagnostics, ref findingIdSequence, ref output);
                }

                // DB05: Duplicate Index Detection
                if (enabledRules.Contains("DB05"))
                {
                    rulesEvaluatedCount++;
                    EvaluateDb05(entity, effectiveContextId, effectiveEntityId, metadataCaps, findings, diagnostics, ref findingIdSequence, ref output);
                }
            }
        }

        output.Findings = findings;
        output.Diagnostics = diagnostics;
        output.Metrics = new ExecutionMetricsDto
        {
            EntitiesAnalyzed = entitiesAnalyzedCount,
            RulesEvaluated = rulesEvaluatedCount,
            FindingsCount = findings.Count
        };

        if (diagnostics.Count > 0 && output.Status == "Success")
        {
            output.Status = "PartialSuccess";
        }

        return output;
    }

    private static void EvaluateDb01(
        EntityDto entity,
        string contextId,
        string entityId,
        HashSet<string> metadataCaps,
        List<FindingDto> findings,
        List<DiagnosticDto> diagnostics,
        ref int findingIdSequence,
        ref AnalysisOutputContract output)
    {
        // Неполные сведения о ключах влияют на DB01
        if (entity.HasIncompleteKeys || entity.HasIncompleteMetadata)
        {
            var code = entity.HasIncompleteKeys ? "INCOMPLETE_KEYS_SKIPPED" : "INCOMPLETE_METADATA_SKIPPED";
            diagnostics.Add(new DiagnosticDto
            {
                Code = code,
                Message = $"DB01 skipped for entity '{entity.Name}' due to incomplete keys metadata.",
                ContextId = contextId,
                EntityId = entityId,
                RuleId = "DB01"
            });
            return;
        }

        if (string.Equals(entity.MappingType, "Keyless", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entity.KeyType, "Keyless", StringComparison.OrdinalIgnoreCase))
        {
            // Keyless entity - by EF Core design does not require PK
            return;
        }

        if (!string.Equals(entity.MappingType, "Table", StringComparison.OrdinalIgnoreCase))
        {
            // Views, SQL queries, Functions, Unmapped do not require a PK
            return;
        }

        if (entity.IsDerivedEntity)
        {
            // Derived entity in inheritance hierarchy inherits PK or is mapped to parent table
            return;
        }

        var hasPrimaryKey = entity.Keys.Any(k => k.IsPrimaryKey) || entity.Properties.Any(p => p.IsPrimaryKey);
        if (!hasPrimaryKey)
        {
            findings.Add(new FindingDto
            {
                Id = findingIdSequence++,
                RuleId = "DB01",
                RuleName = "PrimaryKeyRequired",
                Severity = "Warning",
                Message = $"Entity '{entity.Name}' mapped to table '{entity.TableName ?? entity.Name}' does not have a primary key defined.",
                Recommendation = $"Define a primary key using [Key] attribute or modelBuilder.Entity<{entity.Name}>().HasKey(...).",
                ContextId = contextId,
                EntityId = entityId,
                TargetEntityName = entity.Name,
                TargetTable = entity.TableName ?? entity.Name,
                Location = entity.PrimaryLocation
            });
        }
    }

    private static void EvaluateDb02(
        EntityDto entity,
        string contextId,
        string entityId,
        HashSet<string> metadataCaps,
        List<FindingDto> findings,
        List<DiagnosticDto> diagnostics,
        ref int findingIdSequence,
        ref AnalysisOutputContract output)
    {
        // Неполные навигации или внешние ключи влияют на DB02
        if (entity.HasIncompleteForeignKeys || entity.HasIncompleteNavigations || entity.HasIncompleteMetadata)
        {
            var code = (entity.HasIncompleteForeignKeys || entity.HasIncompleteNavigations)
                ? "INCOMPLETE_NAVIGATIONS_SKIPPED"
                : "INCOMPLETE_METADATA_SKIPPED";
            diagnostics.Add(new DiagnosticDto
            {
                Code = code,
                Message = $"DB02 skipped for entity '{entity.Name}' due to incomplete navigation/foreign key metadata.",
                ContextId = contextId,
                EntityId = entityId,
                RuleId = "DB02"
            });
            return;
        }

        foreach (var fk in entity.ForeignKeys)
        {
            if (!fk.IsNavigationResolved)
            {
                diagnostics.Add(new DiagnosticDto
                {
                    Code = "UNRESOLVED_NAVIGATION_SKIPPED",
                    Message = $"DB02 navigation unresolved for foreign key referencing '{fk.PrincipalEntityName}' in entity '{entity.Name}'. Rule skipped.",
                    ContextId = contextId,
                    EntityId = entityId,
                    RuleId = "DB02"
                });
                continue;
            }

            var navPrincipal = fk.NavigationToPrincipal ?? fk.NavigationPropertyName;
            var navDependent = fk.NavigationToDependent;

            var hasPrincipalNav = !string.IsNullOrWhiteSpace(navPrincipal);
            var hasDependentNav = !string.IsNullOrWhiteSpace(navDependent);

            // Наличие навигационного свойства к главной сущности (NavigationToPrincipal)
            // удовлетворяет проектному соглашению (даже при отсутствии обратной навигации)
            if (hasPrincipalNav)
            {
                continue;
            }

            // Навигационное свойство к главной сущности отсутствует
            var message = hasDependentNav
                ? $"Рекомендация: внешний ключ '{fk.PrincipalEntityName}' сущности '{entity.Name}' не имеет навигационного свойства к главной сущности '{fk.PrincipalEntityName}'. Это допустимо в EF Core, но не соответствует рекомендуемому проектному соглашению."
                : $"Рекомендация: внешний ключ '{fk.PrincipalEntityName}' сущности '{entity.Name}' не имеет навигационных свойств к главной сущности '{fk.PrincipalEntityName}' (навигационные свойства отсутствуют с обеих сторон). Это допустимо в EF Core, но не соответствует рекомендуемому проектному соглашению.";

            findings.Add(new FindingDto
            {
                Id = findingIdSequence++,
                RuleId = "DB02",
                RuleName = "UnidirectionalRelationship",
                Severity = "Warning",
                Message = message,
                Recommendation = $"В рамках проектного соглашения рекомендуется добавить навигационное свойство в сущность '{entity.Name}' для внешнего ключа. Это обеспечивает типобезопасный доступ к связанной сущности '{fk.PrincipalEntityName}' и поддержку удобной загрузки связанных данных через LINQ (.Include()). Это является рекомендацией по стилю моделирования и не нарушает целостность БД или требования EF Core.",
                ContextId = contextId,
                EntityId = entityId,
                TargetEntityName = entity.Name,
                TargetTable = entity.TableName ?? entity.Name,
                Location = fk.Location ?? entity.PrimaryLocation
            });
        }
    }

    private static void EvaluateDb03(
        EntityDto entity,
        string? contextId,
        string? entityId,
        HashSet<string> metadataCaps,
        HashSet<string> processedManyToManyRelationships,
        List<FindingDto> findings,
        List<DiagnosticDto> diagnostics,
        ref int findingIdSequence,
        ref AnalysisOutputContract output,
        string effectiveContextId)
    {
        // Неполные skip navigations влияют на DB03
        if (entity.HasIncompleteSkipNavigations || entity.HasIncompleteMetadata)
        {
            var code = entity.HasIncompleteSkipNavigations
                ? "INCOMPLETE_SKIP_NAVIGATIONS_SKIPPED"
                : "INCOMPLETE_METADATA_SKIPPED";
            diagnostics.Add(new DiagnosticDto
            {
                Code = code,
                Message = $"DB03 skipped for entity '{entity.Name}' due to incomplete skip navigations metadata.",
                ContextId = effectiveContextId,
                EntityId = entityId,
                RuleId = "DB03"
            });
            return;
        }

        foreach (var skipNav in entity.SkipNavigations)
        {
            // Проверка наличия стабильных идентификаторов:
            // Не использовать DbContext.Name как замену ContextId, не использовать TargetEntityName как замену TargetEntityId.
            var hasStableIdentifiers =
                !string.IsNullOrWhiteSpace(contextId) &&
                !string.IsNullOrWhiteSpace(entityId) &&
                !string.IsNullOrWhiteSpace(skipNav.TargetEntityId) &&
                !string.IsNullOrWhiteSpace(skipNav.JoinEntityId) &&
                !string.IsNullOrWhiteSpace(skipNav.NavigationPropertyName);

            if (!hasStableIdentifiers)
            {
                diagnostics.Add(new DiagnosticDto
                {
                    Code = "AMBIGUOUS_MANY_TO_MANY_SKIPPED",
                    Message = $"Many-to-many relationship on entity '{entity.Name}' (navigation '{skipNav.NavigationPropertyName}') lacks required stable identifiers (ContextId, EntityId, TargetEntityId, or JoinEntityId). Deduplication was safely skipped to prevent merging distinct relationships.",
                    ContextId = effectiveContextId,
                    EntityId = entityId,
                    RuleId = "DB03"
                });

                // При отсутствии стабильных идентификаторов дедупликация безопасно пропускается (не объединяем отношения)
                findings.Add(new FindingDto
                {
                    Id = findingIdSequence++,
                    RuleId = "DB03",
                    RuleName = "ManyToManyPattern",
                    Severity = "Info",
                    Message = $"Рекомендация: связь многие-ко-многим между сущностями '{entity.Name}' и '{skipNav.TargetEntityName}' настроена через неявную промежуточную таблицу '{skipNav.JoinEntityName ?? "JoinTable"}'.",
                    Recommendation = "Рассмотрите возможность создания явной связующей сущности (join entity). Это позволит явно управлять схемой связующей таблицы, индексами и добавлять дополнительные атрибуты связи (например, дату создания или статус) без изменения типа связи в будущем.",
                    ContextId = effectiveContextId,
                    EntityId = entityId,
                    TargetEntityName = entity.Name,
                    TargetTable = entity.TableName ?? entity.Name,
                    Location = skipNav.Location ?? entity.PrimaryLocation
                });
                continue;
            }

            var ep1 = $"{entityId}:{skipNav.NavigationPropertyName}";
            var ep2 = $"{skipNav.TargetEntityId}:{skipNav.InverseNavigationPropertyName ?? string.Empty}";

            // Упорядочиваем концы связи по Ordinal для дедупликации обратного представления
            var (minEp, maxEp) = string.CompareOrdinal(ep1, ep2) <= 0 ? (ep1, ep2) : (ep2, ep1);

            // Стабильный ключ: включает ContextId, JoinEntityId и обе стороны навигации
            var relationshipKey = $"{contextId}::{skipNav.JoinEntityId}::{minEp}<->{maxEp}";

            // Для двунаправленной связи канонической сущностью для отчёта является
            // сущность с лексикографически меньшим идентификатором (или меньшим именем навигации при рефлексивной связи)
            if (!string.IsNullOrWhiteSpace(skipNav.InverseNavigationPropertyName))
            {
                var entityComparison = string.Compare(entityId, skipNav.TargetEntityId, StringComparison.Ordinal);
                if (entityComparison > 0)
                {
                    continue;
                }

                if (entityComparison == 0 &&
                    string.Compare(skipNav.NavigationPropertyName, skipNav.InverseNavigationPropertyName, StringComparison.Ordinal) > 0)
                {
                    continue;
                }
            }

            if (!processedManyToManyRelationships.Add(relationshipKey))
            {
                continue;
            }

            findings.Add(new FindingDto
            {
                Id = findingIdSequence++,
                RuleId = "DB03",
                RuleName = "ManyToManyPattern",
                Severity = "Info",
                Message = $"Рекомендация: связь многие-ко-многим между сущностями '{entity.Name}' и '{skipNav.TargetEntityName}' настроена через неявную промежуточную таблицу '{skipNav.JoinEntityName ?? "JoinTable"}'.",
                Recommendation = "Рассмотрите возможность создания явной связующей сущности (join entity). Это позволит явно управлять схемой связующей таблицы, индексами и добавлять дополнительные атрибуты связи (например, дату создания или статус) без изменения типа связи в будущем.",
                ContextId = effectiveContextId,
                EntityId = entityId,
                TargetEntityName = entity.Name,
                TargetTable = entity.TableName ?? entity.Name,
                Location = skipNav.Location ?? entity.PrimaryLocation
            });
        }
    }

    private static void EvaluateDb04(
        EntityDto entity,
        string contextId,
        string entityId,
        HashSet<string> metadataCaps,
        List<FindingDto> findings,
        List<DiagnosticDto> diagnostics,
        ref int findingIdSequence,
        ref AnalysisOutputContract output)
    {
        // Неполные внешние ключи, индексы или ключи влияют на DB04
        if (entity.HasIncompleteForeignKeys || entity.HasIncompleteIndexes || entity.HasIncompleteKeys || entity.HasIncompleteMetadata)
        {
            var code = (entity.HasIncompleteForeignKeys || entity.HasIncompleteIndexes || entity.HasIncompleteKeys)
                ? "INCOMPLETE_INDEXES_SKIPPED"
                : "INCOMPLETE_METADATA_SKIPPED";
            diagnostics.Add(new DiagnosticDto
            {
                Code = code,
                Message = $"DB04 skipped for entity '{entity.Name}' due to incomplete index/key metadata.",
                ContextId = contextId,
                EntityId = entityId,
                RuleId = "DB04"
            });
            return;
        }

        if (!string.Equals(entity.MappingType, "Table", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var fk in entity.ForeignKeys)
        {
            if (fk.Properties.Count == 0)
            {
                continue;
            }

            var fkProps = fk.Properties;

            // Check if any index covers FK (starts with FK properties in exact order)
            var isCoveredByIndex = entity.Indexes.Any(idx =>
                idx.Properties.Count >= fkProps.Count &&
                idx.Properties.Take(fkProps.Count).SequenceEqual(fkProps, StringComparer.OrdinalIgnoreCase));

            if (isCoveredByIndex)
            {
                continue;
            }

            // Check if Primary Key covers FK
            var pk = entity.Keys.FirstOrDefault(k => k.IsPrimaryKey);
            var pkProps = pk != null ? pk.Properties : entity.Properties.Where(p => p.IsPrimaryKey).Select(p => p.Name).ToList();
            if (pkProps.Count >= fkProps.Count &&
                pkProps.Take(fkProps.Count).SequenceEqual(fkProps, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // Check if Alternate Keys cover FK
            var isCoveredByAk = entity.Keys
                .Where(k => !k.IsPrimaryKey)
                .Any(ak => ak.Properties.Count >= fkProps.Count &&
                           ak.Properties.Take(fkProps.Count).SequenceEqual(fkProps, StringComparer.OrdinalIgnoreCase));

            if (isCoveredByAk)
            {
                continue;
            }

            findings.Add(new FindingDto
            {
                Id = findingIdSequence++,
                RuleId = "DB04",
                RuleName = "MissingForeignKeyIndex",
                Severity = "Warning",
                Message = $"Foreign key on entity '{entity.Name}' referencing '{fk.PrincipalEntityName}' on ({string.Join(", ", fk.Properties)}) is not covered by any index.",
                Recommendation = $"Create an index on ({string.Join(", ", fk.Properties)}) to prevent table scans during joins and cascade deletes.",
                ContextId = contextId,
                EntityId = entityId,
                TargetEntityName = entity.Name,
                TargetTable = entity.TableName ?? entity.Name,
                Location = fk.Location ?? entity.PrimaryLocation
            });
        }
    }

    private static void EvaluateDb05(
        EntityDto entity,
        string contextId,
        string entityId,
        HashSet<string> metadataCaps,
        List<FindingDto> findings,
        List<DiagnosticDto> diagnostics,
        ref int findingIdSequence,
        ref AnalysisOutputContract output)
    {
        // Неполные индексы влияют на DB05
        if (entity.HasIncompleteIndexes || entity.HasIncompleteMetadata)
        {
            var code = entity.HasIncompleteIndexes ? "INCOMPLETE_INDEXES_SKIPPED" : "INCOMPLETE_METADATA_SKIPPED";
            diagnostics.Add(new DiagnosticDto
            {
                Code = code,
                Message = $"DB05 skipped for entity '{entity.Name}' due to incomplete indexes metadata.",
                ContextId = contextId,
                EntityId = entityId,
                RuleId = "DB05"
            });
            return;
        }

        var indexes = entity.Indexes;
        var reportedPairs = new HashSet<int>();

        for (var i = 0; i < indexes.Count; i++)
        {
            for (var j = i + 1; j < indexes.Count; j++)
            {
                if (reportedPairs.Contains(j))
                {
                    continue;
                }

                var idx1 = indexes[i];
                var idx2 = indexes[j];

                // Columns must match exactly in count and order
                if (!idx1.Properties.SequenceEqual(idx2.Properties, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Uniqueness must match
                if (idx1.IsUnique != idx2.IsUnique)
                {
                    continue;
                }

                // Filters must match conservatively
                var areFiltersEqual = SqlFilterComparer.AreFiltersEquivalent(idx1.Filter, idx2.Filter, out var isAmbiguous);

                if (isAmbiguous)
                {
                    diagnostics.Add(new DiagnosticDto
                    {
                        Code = "AMBIGUOUS_SQL_FILTER_SKIPPED",
                        Message = $"DB05 skipped for indexes '{idx1.Name}' and '{idx2.Name}' on entity '{entity.Name}' due to complex/ambiguous SQL filter syntax.",
                        ContextId = contextId,
                        EntityId = entityId,
                        RuleId = "DB05"
                    });
                    continue;
                }

                if (!areFiltersEqual)
                {
                    continue;
                }

                reportedPairs.Add(j);

                findings.Add(new FindingDto
                {
                    Id = findingIdSequence++,
                    RuleId = "DB05",
                    RuleName = "DuplicateIndex",
                    Severity = "Warning",
                    Message = $"Duplicate indexes '{idx1.Name}' and '{idx2.Name}' on entity '{entity.Name}' cover the same columns ({string.Join(", ", idx1.Properties)}) with identical uniqueness and filter semantics.",
                    Recommendation = $"Remove the redundant index '{idx2.Name}' to reduce storage overhead and write penalty.",
                    ContextId = contextId,
                    EntityId = entityId,
                    TargetEntityName = entity.Name,
                    TargetTable = entity.TableName ?? entity.Name,
                    Location = idx2.Location ?? entity.PrimaryLocation
                });
            }
        }
    }
}
