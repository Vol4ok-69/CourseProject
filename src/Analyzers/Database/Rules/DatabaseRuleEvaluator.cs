using Application.Analyzers;
using DatabaseAnalyzer.Metadata;
using Domain.Enums;

namespace DatabaseAnalyzer.Rules;

/// <summary>
/// Вычислитель правил реляционной модели DB01–DB05 над объектом DatabaseMetadata.
/// Содержит чистую логику проверки схемы, предназначенную для изолированного контейнера (ADR-001).
/// </summary>
public static class DatabaseRuleEvaluator
{
    public static IReadOnlyList<AnalyzerFinding> Evaluate(DatabaseMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var findings = new List<AnalyzerFinding>();
        var processedManyToManyRelationships = new HashSet<string>(StringComparer.Ordinal);
        var contextId = !string.IsNullOrWhiteSpace(metadata.ContextName) ? metadata.ContextName : "DefaultContext";

        foreach (var entity in metadata.Entities)
        {
            // DB01: сущность без первичного ключа.
            // Пропускается для:
            // 1. Нетабличных сущностей (View, SqlQuery, Function, Unmapped)
            // 2. Явно сконфигурированных Keyless сущностей (.HasNoKey() / [Keyless])
            // 3. Дочерних сущностей в иерархии наследования (PK в базовом типе)
            // 4. Сущностей с неполными метаданными (во избежание ложных срабатываний)
            var shouldCheckDb01 = entity.MappingType is EntityMappingType.Table or EntityMappingType.Unknown
                && entity.KeyType != EntityKeyType.Keyless
                && !entity.IsDerivedEntity
                && !entity.HasIncompleteKeys
                && !entity.HasIncompleteMetadata;

            if (shouldCheckDb01 && !entity.Keys.Any(key => key.IsPrimaryKey))
            {
                findings.Add(new AnalyzerFinding
                {
                    Rule = "DB01",
                    FilePath = entity.PrimaryLocation?.FilePath ?? entity.TableName,
                    LineNumber = entity.PrimaryLocation?.LineNumber ?? 0,
                    Message = $"Сущность '{entity.EntityName}' (таблица '{entity.TableName}') не имеет первичного ключа.",
                    Severity = Severity.Warning,
                    Recommendation = "Проверьте, что сущность намеренно настроена без первичного ключа. Для таблицы с изменяемыми данными задайте первичный ключ; для представления или проекции убедитесь, что отсутствие ключа предусмотрено."
                });
            }

            // DB02: внешний ключ без навигационного свойства к главной сущности (рекомендация соглашения проекта).
            // Неизвестная или неразрешённая навигация (IsNavigationResolved == false) не должна приравниваться к отсутствующей.
            if (!entity.HasIncompleteForeignKeys && !entity.HasIncompleteNavigations && !entity.HasIncompleteMetadata)
            {
                foreach (var foreignKey in entity.ForeignKeys)
                {
                    if (foreignKey.IsNavigationResolved && string.IsNullOrWhiteSpace(foreignKey.NavigationToPrincipal))
                    {
                        findings.Add(new AnalyzerFinding
                        {
                            Rule = "DB02",
                            FilePath = foreignKey.Location?.FilePath ?? entity.PrimaryLocation?.FilePath ?? entity.TableName,
                            LineNumber = foreignKey.Location?.LineNumber ?? entity.PrimaryLocation?.LineNumber ?? 0,
                            Message = $"Рекомендация: внешний ключ '{foreignKey.Name}' сущности '{entity.EntityName}' не имеет навигационного свойства к главной сущности '{foreignKey.PrincipalEntityName}'. Это допустимо в EF Core, но не соответствует рекомендуемому проектному соглашению.",
                            Severity = Severity.Warning,
                            Recommendation = $"В рамках проектного соглашения рекомендуется добавить навигационное свойство в сущность '{entity.EntityName}' для внешнего ключа '{foreignKey.Name}'. Это обеспечивает типобезопасный доступ к связанной сущности '{foreignKey.PrincipalEntityName}' и поддержку удобной загрузки связанных данных через LINQ (.Include()). Это является рекомендацией по стилю моделирования и не нарушает целостность БД или требования EF Core."
                        });
                    }
                }
            }

            // DB03: неявная связь многие-ко-многим без явной промежуточной сущности.
            if (!entity.HasIncompleteSkipNavigations && !entity.HasIncompleteMetadata)
            {
                foreach (var skipNav in entity.SkipNavigations)
                {
                    if (!skipNav.IsImplicitJoinEntity)
                    {
                        continue;
                    }

                    var sourceId = !string.IsNullOrWhiteSpace(entity.ClrTypeName) ? entity.ClrTypeName : entity.EntityName;
                    var targetId = !string.IsNullOrWhiteSpace(skipNav.TargetEntityId) ? skipNav.TargetEntityId : skipNav.TargetEntityName;
                    var joinId = !string.IsNullOrWhiteSpace(skipNav.JoinEntityId) ? skipNav.JoinEntityId : (!string.IsNullOrWhiteSpace(skipNav.JoinEntityTableName) ? skipNav.JoinEntityTableName : "implicit");

                    var ep1 = $"{sourceId}:{skipNav.NavigationName}";
                    var ep2 = $"{targetId}:{skipNav.InverseNavigationName ?? string.Empty}";

                    var (minEp, maxEp) = string.CompareOrdinal(ep1, ep2) <= 0 ? (ep1, ep2) : (ep2, ep1);
                    var relKey = $"{contextId}::{joinId}::{minEp}<->{maxEp}";

                    // Для двунаправленной связи канонической сущностью для отчёта является
                    // сущность с лексикографически меньшим идентификатором (или меньшим именем навигации при рефлексивной связи)
                    if (!string.IsNullOrWhiteSpace(skipNav.InverseNavigationName))
                    {
                        var entityComparison = string.Compare(sourceId, targetId, StringComparison.Ordinal);
                        if (entityComparison > 0)
                        {
                            continue;
                        }

                        if (entityComparison == 0 &&
                            string.Compare(skipNav.NavigationName, skipNav.InverseNavigationName, StringComparison.Ordinal) > 0)
                        {
                            continue;
                        }
                    }

                    if (!processedManyToManyRelationships.Add(relKey))
                    {
                        continue;
                    }

                    findings.Add(new AnalyzerFinding
                    {
                        Rule = "DB03",
                        FilePath = skipNav.Location?.FilePath ?? entity.PrimaryLocation?.FilePath ?? entity.TableName,
                        LineNumber = skipNav.Location?.LineNumber ?? entity.PrimaryLocation?.LineNumber ?? 0,
                        Message = $"Рекомендация: связь многие-ко-многим между сущностями '{entity.EntityName}' и '{skipNav.TargetEntityName}' настроена через неявную промежуточную таблицу '{skipNav.JoinEntityTableName}'.",
                        Severity = Severity.Info,
                        Recommendation = $"Рассмотрите возможность создания явной связующей сущности (join entity) для таблицы '{skipNav.JoinEntityTableName}'. Это позволит явно управлять схемой связующей таблицы, индексами и добавлять дополнительные атрибуты связи (например, дату создания или статус) без изменения типа связи в будущем. Использование неявной связи допустимо в EF Core и является рекомендацией по проектированию модели."
                    });
                }
            }

            // DB04: внешний ключ без подходящего индекса.
            // Проверяем покрытие упорядоченным префиксом индекса, первичного ключа или альтернативного ключа.
            if (entity.MappingType is EntityMappingType.Table or EntityMappingType.Unknown &&
                !entity.HasIncompleteForeignKeys &&
                !entity.HasIncompleteIndexes &&
                !entity.HasIncompleteKeys &&
                !entity.HasIncompleteMetadata)
            {
                foreach (var foreignKey in entity.ForeignKeys)
                {
                    var foreignKeyProperties = foreignKey.PropertyNames;

                    if (foreignKeyProperties.Count == 0)
                    {
                        continue;
                    }

                    // Проверяем индексы
                    var hasMatchingIndex = entity.Indexes.Any(index =>
                        index.PropertyNames.Count >= foreignKeyProperties.Count &&
                        index.PropertyNames
                            .Take(foreignKeyProperties.Count)
                            .SequenceEqual(foreignKeyProperties, StringComparer.Ordinal));

                    // Проверяем первичный ключ
                    var hasMatchingPrimaryKey = entity.Keys.Any(key =>
                        key.IsPrimaryKey &&
                        key.PropertyNames.Count >= foreignKeyProperties.Count &&
                        key.PropertyNames
                            .Take(foreignKeyProperties.Count)
                            .SequenceEqual(foreignKeyProperties, StringComparer.Ordinal));

                    // Проверяем альтернативные ключи
                    var hasMatchingAlternateKey = entity.AlternateKeys.Any(ak =>
                        ak.PropertyNames.Count >= foreignKeyProperties.Count &&
                        ak.PropertyNames
                            .Take(foreignKeyProperties.Count)
                            .SequenceEqual(foreignKeyProperties, StringComparer.Ordinal));

                    if (hasMatchingIndex || hasMatchingPrimaryKey || hasMatchingAlternateKey)
                    {
                        continue;
                    }

                    findings.Add(new AnalyzerFinding
                    {
                        Rule = "DB04",
                        FilePath = foreignKey.Location?.FilePath ?? entity.PrimaryLocation?.FilePath ?? entity.TableName,
                        LineNumber = foreignKey.Location?.LineNumber ?? entity.PrimaryLocation?.LineNumber ?? 0,
                        Message = $"Внешний ключ '{foreignKey.Name}' сущности '{entity.EntityName}' не имеет подходящего индекса по столбцам: {string.Join(", ", foreignKeyProperties)}.",
                        Severity = Severity.Warning,
                        Recommendation = "Проверьте частоту соединений и фильтрации по столбцам внешнего ключа. При необходимости добавьте индекс, начинающийся со столбцов внешнего ключа в указанном порядке."
                    });
                }
            }

            // DB05: дублирующиеся индексы.
            // Учитывает точный порядок столбцов, уникальность и фильтр индекса (WHERE).
            if (!entity.HasIncompleteIndexes && !entity.HasIncompleteMetadata)
            {
                for (var i = 0; i < entity.Indexes.Count; i++)
                {
                    for (var j = i + 1; j < entity.Indexes.Count; j++)
                    {
                        var firstIndex = entity.Indexes[i];
                        var secondIndex = entity.Indexes[j];

                        if (firstIndex.IsUnique != secondIndex.IsUnique)
                        {
                            continue;
                        }

                        if (!firstIndex.PropertyNames.SequenceEqual(secondIndex.PropertyNames, StringComparer.Ordinal))
                        {
                            continue;
                        }

                        // Консервативное сравнение фильтров с сохранением литералов и разбором скобок
                        if (!SqlFilterComparer.AreFiltersEquivalent(firstIndex.Filter, secondIndex.Filter, out var isAmbiguous))
                        {
                            continue;
                        }

                        if (isAmbiguous)
                        {
                            continue;
                        }

                        findings.Add(new AnalyzerFinding
                        {
                            Rule = "DB05",
                            FilePath = secondIndex.Location?.FilePath ?? entity.PrimaryLocation?.FilePath ?? entity.TableName,
                            LineNumber = secondIndex.Location?.LineNumber ?? entity.PrimaryLocation?.LineNumber ?? 0,
                            Message = $"Индексы '{firstIndex.Name}' и '{secondIndex.Name}' сущности '{entity.EntityName}' дублируются по столбцам: {string.Join(", ", firstIndex.PropertyNames)}.",
                            Severity = Severity.Info,
                            Recommendation = "Проверьте необходимость обоих индексов. Если они не отличаются по назначению или параметрам, рассмотрите удаление одного из них."
                        });
                    }
                }
            }
        }

        return findings;
    }
}
