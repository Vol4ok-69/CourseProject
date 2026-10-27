
using Application.Analyzers;
using DatabaseAnalyzer.Metadata;
using DatabaseAnalyzer.Providers;
using Domain.Enums;

namespace DatabaseAnalyzer;

public sealed class DatabaseAnalyzer(IDatabaseMetadataProvider metadataProvider) : IAnalyzer
{
    public AnalyzerType Type => AnalyzerType.Database;

    public async Task<IReadOnlyList<AnalyzerFinding>> AnalyzeAsync(AnalyzerContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var metadata = await metadataProvider.GetMetadataAsync(context, cancellationToken);
        var findings = new List<AnalyzerFinding>();

        foreach (var entity in metadata.Entities)
        {
            // DB01: сущность без первичного ключа.
            if (!entity.Keys.Any(key => key.IsPrimaryKey))
            {
                findings.Add(new AnalyzerFinding
                {
                    Rule = "DB01",
                    FilePath = entity.TableName,
                    LineNumber = 0,
                    Message = $"Сущность '{entity.EntityName}' (таблица '{entity.TableName}') не имеет первичного ключа.",
                    Severity = Severity.Warning,
                    Recommendation = "Проверьте, что сущность намеренно настроена без первичного ключа. Для таблицы с изменяемыми данными задайте первичный ключ; для представления или проекции убедитесь, что отсутствие ключа предусмотрено."
                });
            }

            // DB02: внешний ключ без навигационного свойства к главной сущности (рекомендация соглашения проекта).
            foreach (var foreignKey in entity.ForeignKeys)
            {
                if (string.IsNullOrWhiteSpace(foreignKey.NavigationToPrincipal))
                {
                    findings.Add(new AnalyzerFinding
                    {
                        Rule = "DB02",
                        FilePath = entity.TableName,
                        LineNumber = 0,
                        Message = $"Рекомендация: внешний ключ '{foreignKey.Name}' сущности '{entity.EntityName}' не имеет навигационного свойства к главной сущности '{foreignKey.PrincipalEntityName}'. Это допустимо в EF Core, но не соответствует рекомендуемому проектному соглашению.",
                        Severity = Severity.Warning,
                        Recommendation = $"В рамках проектного соглашения рекомендуется добавить навигационное свойство в сущность '{entity.EntityName}' для внешнего ключа '{foreignKey.Name}'. Это обеспечивает типобезопасный доступ к связанной сущности '{foreignKey.PrincipalEntityName}' и поддержку удобной загрузки связанных данных через LINQ (.Include()). Это является рекомендацией по стилю моделирования и не нарушает целостность БД или требования EF Core."
                    });
                }
            }

            // DB03: неявная связь многие-ко-многим без явной промежуточной сущности.
            foreach (var skipNav in entity.SkipNavigations)
            {
                if (!skipNav.IsImplicitJoinEntity)
                {
                    continue;
                }

                // Стабильная дедупликация физической связи:
                // Для двунаправленной связи регистрируем finding только один раз для сущности с лексикографически меньшим именем.
                // При равенстве имен (самоссылающаяся связь) регистрируем finding только для лексикографически меньшего имени навигации.
                // Если между теми же типами сущностей настроено несколько связей, они различаются по JoinEntityTableName/JoinEntityName.
                if (!string.IsNullOrWhiteSpace(skipNav.InverseNavigationName))
                {
                    var entityComparison = string.Compare(entity.EntityName, skipNav.TargetEntityName, StringComparison.Ordinal);
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

                findings.Add(new AnalyzerFinding
                {
                    Rule = "DB03",
                    FilePath = entity.TableName,
                    LineNumber = 0,
                    Message = $"Рекомендация: связь многие-ко-многим между сущностями '{entity.EntityName}' и '{skipNav.TargetEntityName}' настроена через неявную промежуточную таблицу '{skipNav.JoinEntityTableName}'.",
                    Severity = Severity.Info,
                    Recommendation = $"Рассмотрите возможность создания явной связующей сущности (join entity) для таблицы '{skipNav.JoinEntityTableName}'. Это позволит явно управлять схемой связующей таблицы, индексами и добавлять дополнительные атрибуты связи (например, дату создания или статус) без изменения типа связи в будущем. Использование неявной связи допустимо в EF Core и является рекомендацией по проектированию модели."
                });
            }

            // DB04: внешний ключ без подходящего индекса.
            foreach (var foreignKey in entity.ForeignKeys)
            {
                var foreignKeyProperties = foreignKey.PropertyNames;

                if (foreignKeyProperties.Count == 0)
                {
                    continue;
                }

                var hasMatchingIndex = entity.Indexes.Any(index =>
                    index.PropertyNames.Count >= foreignKeyProperties.Count &&
                    index.PropertyNames
                        .Take(foreignKeyProperties.Count)
                        .SequenceEqual(foreignKeyProperties, StringComparer.Ordinal));

                if (hasMatchingIndex)
                {
                    continue;
                }

                findings.Add(new AnalyzerFinding
                {
                    Rule = "DB04",
                    FilePath = entity.TableName,
                    LineNumber = 0,
                    Message = $"Внешний ключ '{foreignKey.Name}' сущности '{entity.EntityName}' не имеет подходящего индекса по столбцам: {string.Join(", ", foreignKeyProperties)}.",
                    Severity = Severity.Warning,
                    Recommendation = "Проверьте частоту соединений и фильтрации по столбцам внешнего ключа. При необходимости добавьте индекс, начинающийся со столбцов внешнего ключа в указанном порядке."
                });
            }


            // DB05: дублирующиеся индексы.
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

                    findings.Add(new AnalyzerFinding
                    {
                        Rule = "DB05",
                        FilePath = entity.TableName,
                        LineNumber = 0,
                        Message = $"Индексы '{firstIndex.Name}' и '{secondIndex.Name}' сущности '{entity.EntityName}' дублируются по столбцам: {string.Join(", ", firstIndex.PropertyNames)}.",
                        Severity = Severity.Info,
                        Recommendation = "Проверьте необходимость обоих индексов. Если они не отличаются по назначению или параметрам, рассмотрите удаление одного из них."
                    });
                }
            }

        }

        return findings;
    }
}
