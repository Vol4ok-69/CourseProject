using System.Text.Json;
using System.Text.Json.Nodes;
using NetScope.DatabaseAnalyzer.Runner.Contracts;

namespace DatabaseAnalyzer.Runner.Tests;

public sealed class SchemaConformityTests
{
    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "CourseProject.slnx")))
            {
                return current;
            }
            current = Path.GetDirectoryName(current);
        }

        throw new DirectoryNotFoundException("Could not find repository root with CourseProject.slnx.");
    }

    [Fact]
    public void InputSchema_FileExists_AndIsValidJson()
    {
        var repoRoot = FindRepoRoot();
        var schemaPath = Path.Combine(repoRoot, "docs", "architecture", "schemas", "database-analyzer-input-v1.schema.json");

        Assert.True(File.Exists(schemaPath), $"Schema file not found at: {schemaPath}");
        var content = File.ReadAllText(schemaPath);
        var jsonNode = JsonNode.Parse(content);

        Assert.NotNull(jsonNode);
        Assert.Equal("DatabaseAnalyzerInputV1", jsonNode["title"]?.ToString());
        Assert.NotNull(jsonNode["required"]);
    }

    [Fact]
    public void OutputSchema_FileExists_AndIsValidJson()
    {
        var repoRoot = FindRepoRoot();
        var schemaPath = Path.Combine(repoRoot, "docs", "architecture", "schemas", "database-analyzer-output-v1.schema.json");

        Assert.True(File.Exists(schemaPath), $"Schema file not found at: {schemaPath}");
        var content = File.ReadAllText(schemaPath);
        var jsonNode = JsonNode.Parse(content);

        Assert.NotNull(jsonNode);
        Assert.Equal("DatabaseAnalyzerOutputV1", jsonNode["title"]?.ToString());
        Assert.NotNull(jsonNode["required"]);
    }

    [Fact]
    public void SerializedInput_ContainsAllRequiredProperties()
    {
        var repoRoot = FindRepoRoot();
        var schemaPath = Path.Combine(repoRoot, "docs", "architecture", "schemas", "database-analyzer-input-v1.schema.json");
        var schemaNode = JsonNode.Parse(File.ReadAllText(schemaPath))!;
        var requiredFields = schemaNode["required"]!.AsArray().Select(n => n!.ToString()).ToList();

        var input = new AnalysisInputContract
        {
            SchemaVersion = "1.0",
            AnalysisId = 1,
            ExecutionToken = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            MetadataCapabilities = ["CoreRelational"],
            Configuration = new ConfigurationDto(),
            Metadata = new MetadataDto()
        };

        var json = JsonSerializer.Serialize(input, RunnerJsonContext.Default.AnalysisInputContract);
        var serializedNode = JsonNode.Parse(json)!.AsObject();

        foreach (var req in requiredFields)
        {
            Assert.True(serializedNode.ContainsKey(req), $"Serialized input JSON is missing required property: {req}");
        }
    }

    [Fact]
    public void SerializedOutput_ContainsAllRequiredProperties()
    {
        var repoRoot = FindRepoRoot();
        var schemaPath = Path.Combine(repoRoot, "docs", "architecture", "schemas", "database-analyzer-output-v1.schema.json");
        var schemaNode = JsonNode.Parse(File.ReadAllText(schemaPath))!;
        var requiredFields = schemaNode["required"]!.AsArray().Select(n => n!.ToString()).ToList();

        var output = new AnalysisOutputContract
        {
            SchemaVersion = "1.0",
            AnalyzerVersion = "1.0.0",
            AnalysisId = 1,
            ExecutionToken = Guid.NewGuid(),
            Status = "Success",
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Metrics = new ExecutionMetricsDto(),
            Findings = [],
            Diagnostics = [],
            Errors = []
        };

        var json = JsonSerializer.Serialize(output, RunnerJsonContext.Default.AnalysisOutputContract);
        var serializedNode = JsonNode.Parse(json)!.AsObject();

        foreach (var req in requiredFields)
        {
            Assert.True(serializedNode.ContainsKey(req), $"Serialized output JSON is missing required property: {req}");
        }
    }
}
