using NetScope.DatabaseAnalyzer.Runner.Contracts;
using NetScope.DatabaseAnalyzer.Runner.Rules;

namespace DatabaseAnalyzer.Runner.Tests;

public sealed class CapabilitiesNegotiationTests
{
    private readonly DatabaseRulesEngine _engine = new();

    [Fact]
    public void MissingBaselineCapability_FailsEvaluation()
    {
        var input = new AnalysisInputContract
        {
            SchemaVersion = "1.0",
            AnalysisId = 1,
            ExecutionToken = Guid.NewGuid(),
            MetadataCapabilities = ["FilteredIndexes"], // Missing CoreRelational!
            Configuration = new ConfigurationDto(),
            Metadata = new MetadataDto()
        };

        var output = _engine.Evaluate(input);

        Assert.Equal("Failed", output.Status);
        Assert.Single(output.Errors);
        Assert.Equal("MISSING_BASELINE_CAPABILITY", output.Errors[0].Code);
    }

    [Fact]
    public void FullCapabilities_EvaluatesSuccessfully()
    {
        var input = new AnalysisInputContract
        {
            SchemaVersion = "1.0",
            AnalysisId = 1,
            ExecutionToken = Guid.NewGuid(),
            MetadataCapabilities = ["CoreRelational", "FilteredIndexes", "KeylessEntities"],
            Configuration = new ConfigurationDto(),
            Metadata = new MetadataDto()
        };

        var output = _engine.Evaluate(input);

        Assert.Equal("Success", output.Status);
        Assert.Empty(output.Errors);
    }
}
