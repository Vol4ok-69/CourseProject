using System.Text.Json;
using NetScope.DatabaseAnalyzer.Runner;
using NetScope.DatabaseAnalyzer.Runner.Contracts;

namespace DatabaseAnalyzer.Runner.Tests;

public sealed class RunnerCliTests
{
    [Fact]
    public async Task Main_WithoutArguments_ReturnsExitCode2()
    {
        var exitCode = await Program.Main([]);
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Main_NonexistentInputFile_ReturnsExitCode2()
    {
        var tempOut = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var exitCode = await Program.Main(["--input", "nonexistent-file.json", "--output", tempOut]);
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Main_InvalidJson_ReturnsExitCode2()
    {
        var tempIn = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_in.json");
        var tempOut = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_out.json");

        await File.WriteAllTextAsync(tempIn, "{ invalid json content }");

        try
        {
            var exitCode = await Program.Main(["--input", tempIn, "--output", tempOut]);
            Assert.Equal(2, exitCode);
        }
        finally
        {
            if (File.Exists(tempIn)) File.Delete(tempIn);
            if (File.Exists(tempOut)) File.Delete(tempOut);
        }
    }

    [Fact]
    public async Task Main_UnsupportedSchemaMajorVersion_ReturnsExitCode3()
    {
        var tempIn = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_in.json");
        var tempOut = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_out.json");

        var input = new AnalysisInputContract
        {
            SchemaVersion = "2.0", // Unsupported major version
            AnalysisId = 1,
            ExecutionToken = Guid.NewGuid(),
            MetadataCapabilities = ["CoreRelational"]
        };

        var json = JsonSerializer.Serialize(input, RunnerJsonContext.Default.AnalysisInputContract);
        await File.WriteAllTextAsync(tempIn, json);

        try
        {
            var exitCode = await Program.Main(["--input", tempIn, "--output", tempOut]);
            Assert.Equal(3, exitCode);
        }
        finally
        {
            if (File.Exists(tempIn)) File.Delete(tempIn);
            if (File.Exists(tempOut)) File.Delete(tempOut);
        }
    }

    [Fact]
    public async Task Main_MissingBaselineCapability_ReturnsExitCode2()
    {
        var tempIn = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_in.json");
        var tempOut = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_out.json");

        var input = new AnalysisInputContract
        {
            SchemaVersion = "1.0",
            AnalysisId = 1,
            ExecutionToken = Guid.NewGuid(),
            MetadataCapabilities = [] // Missing CoreRelational
        };

        var json = JsonSerializer.Serialize(input, RunnerJsonContext.Default.AnalysisInputContract);
        await File.WriteAllTextAsync(tempIn, json);

        try
        {
            var exitCode = await Program.Main(["--input", tempIn, "--output", tempOut]);
            Assert.Equal(2, exitCode);
        }
        finally
        {
            if (File.Exists(tempIn)) File.Delete(tempIn);
            if (File.Exists(tempOut)) File.Delete(tempOut);
        }
    }

    [Fact]
    public async Task Main_ValidInput_AtomicWriteAndExitCode0()
    {
        var tempIn = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_in.json");
        var tempOut = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_out.json");

        var input = new AnalysisInputContract
        {
            SchemaVersion = "1.0",
            AnalysisId = 100,
            ExecutionToken = Guid.NewGuid(),
            MetadataCapabilities = ["CoreRelational"],
            Configuration = new ConfigurationDto(),
            Metadata = new MetadataDto()
        };

        var json = JsonSerializer.Serialize(input, RunnerJsonContext.Default.AnalysisInputContract);
        await File.WriteAllTextAsync(tempIn, json);

        try
        {
            var exitCode = await Program.Main(["--input", tempIn, "--output", tempOut]);
            Assert.Equal(0, exitCode);

            // Output file must exist and be valid JSON
            Assert.True(File.Exists(tempOut));
            var outputJson = await File.ReadAllTextAsync(tempOut);
            var output = JsonSerializer.Deserialize(outputJson, RunnerJsonContext.Default.AnalysisOutputContract);

            Assert.NotNull(output);
            Assert.Equal("1.0", output.SchemaVersion);
            Assert.Equal(100, output.AnalysisId);
            Assert.Equal(input.ExecutionToken, output.ExecutionToken);
            Assert.Equal("Success", output.Status);
        }
        finally
        {
            if (File.Exists(tempIn)) File.Delete(tempIn);
            if (File.Exists(tempOut)) File.Delete(tempOut);
        }
    }
}
