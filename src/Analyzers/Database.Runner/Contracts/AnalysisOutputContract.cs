using System.Text.Json.Serialization;

namespace NetScope.DatabaseAnalyzer.Runner.Contracts;

public sealed class AnalysisOutputContract
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0";

    [JsonPropertyName("analyzerVersion")]
    public string AnalyzerVersion { get; set; } = "1.0.0";

    [JsonPropertyName("analysisId")]
    public int AnalysisId { get; set; }

    [JsonPropertyName("executionToken")]
    public Guid ExecutionToken { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "Success"; // "Success", "PartialSuccess", "Failed"

    [JsonPropertyName("completedAtUtc")]
    public DateTimeOffset CompletedAtUtc { get; set; }

    [JsonPropertyName("metrics")]
    public ExecutionMetricsDto Metrics { get; set; } = new();

    [JsonPropertyName("findings")]
    public List<FindingDto> Findings { get; set; } = [];

    [JsonPropertyName("diagnostics")]
    public List<DiagnosticDto> Diagnostics { get; set; } = [];

    [JsonPropertyName("errors")]
    public List<ErrorDto> Errors { get; set; } = [];
}

public sealed class ExecutionMetricsDto
{
    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("entitiesAnalyzed")]
    public int EntitiesAnalyzed { get; set; }

    [JsonPropertyName("rulesEvaluated")]
    public int RulesEvaluated { get; set; }

    [JsonPropertyName("findingsCount")]
    public int FindingsCount { get; set; }
}

public sealed class FindingDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = string.Empty;

    [JsonPropertyName("ruleName")]
    public string RuleName { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty; // "Info", "Warning", "Error", "Critical"

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("recommendation")]
    public string Recommendation { get; set; } = string.Empty;

    [JsonPropertyName("contextId")]
    public string ContextId { get; set; } = string.Empty;

    [JsonPropertyName("entityId")]
    public string? EntityId { get; set; }

    [JsonPropertyName("targetEntityName")]
    public string? TargetEntityName { get; set; }

    [JsonPropertyName("targetTable")]
    public string? TargetTable { get; set; }

    [JsonPropertyName("location")]
    public SourceLocationDto? Location { get; set; }
}

public sealed class DiagnosticDto
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("contextId")]
    public string? ContextId { get; set; }

    [JsonPropertyName("entityId")]
    public string? EntityId { get; set; }

    [JsonPropertyName("ruleId")]
    public string? RuleId { get; set; }
}

public sealed class ErrorDto
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
