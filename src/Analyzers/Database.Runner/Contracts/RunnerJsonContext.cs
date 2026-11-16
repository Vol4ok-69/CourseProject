using System.Text.Json.Serialization;

namespace NetScope.DatabaseAnalyzer.Runner.Contracts;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(AnalysisInputContract))]
[JsonSerializable(typeof(AnalysisOutputContract))]
[JsonSerializable(typeof(ConfigurationDto))]
[JsonSerializable(typeof(MetadataDto))]
[JsonSerializable(typeof(DbContextDto))]
[JsonSerializable(typeof(EntityDto))]
[JsonSerializable(typeof(PropertyDto))]
[JsonSerializable(typeof(KeyDto))]
[JsonSerializable(typeof(ForeignKeyDto))]
[JsonSerializable(typeof(IndexDto))]
[JsonSerializable(typeof(SkipNavigationDto))]
[JsonSerializable(typeof(SourceLocationDto))]
[JsonSerializable(typeof(ExecutionMetricsDto))]
[JsonSerializable(typeof(FindingDto))]
[JsonSerializable(typeof(DiagnosticDto))]
[JsonSerializable(typeof(ErrorDto))]
public partial class RunnerJsonContext : JsonSerializerContext
{
}
