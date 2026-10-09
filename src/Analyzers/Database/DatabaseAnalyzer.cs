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
            if (entity.Keys.Any(key => key.IsPrimaryKey))
            {
                continue;
            }

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

        return findings;
    }
}
