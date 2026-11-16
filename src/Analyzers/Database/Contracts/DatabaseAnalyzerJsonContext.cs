using System.Text.Json.Serialization;

namespace DatabaseAnalyzer.Contracts;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(DatabaseAnalyzerInputDocument))]
[JsonSerializable(typeof(DatabaseAnalyzerOutputDocument))]
[JsonSerializable(typeof(DatabaseAnalyzerConfigurationDto))]
[JsonSerializable(typeof(DatabaseAnalyzerMetadataDto))]
[JsonSerializable(typeof(DatabaseAnalyzerDbContextDto))]
[JsonSerializable(typeof(DatabaseAnalyzerEntityDto))]
[JsonSerializable(typeof(DatabaseAnalyzerPropertyDto))]
[JsonSerializable(typeof(DatabaseAnalyzerKeyDto))]
[JsonSerializable(typeof(DatabaseAnalyzerForeignKeyDto))]
[JsonSerializable(typeof(DatabaseAnalyzerIndexDto))]
[JsonSerializable(typeof(DatabaseAnalyzerSkipNavigationDto))]
[JsonSerializable(typeof(DatabaseAnalyzerSourceLocationDto))]
[JsonSerializable(typeof(DatabaseAnalyzerExecutionMetricsDto))]
[JsonSerializable(typeof(DatabaseAnalyzerFindingDto))]
[JsonSerializable(typeof(DatabaseAnalyzerDiagnosticDto))]
[JsonSerializable(typeof(DatabaseAnalyzerErrorDto))]
public partial class DatabaseAnalyzerJsonContext : JsonSerializerContext
{
}
