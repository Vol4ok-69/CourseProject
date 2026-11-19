using System.Diagnostics;
using DatabaseAnalyzer.Metadata;
using DatabaseAnalyzer.Process;
using DatabaseAnalyzer.Roslyn;
using DatabaseAnalyzer.Runner;

namespace DatabaseAnalyzer.Runner.Tests;

public sealed class DockerContainerIntegrationTests
{
    private static bool IsDockerAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "info",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    [DockerFact]
    public async Task DockerContainer_ExecutesSafely_WithIsolationFlags()
    {
        var adapter = new DockerCliProcessAdapter();
        var validator = new TestLeaseValidator(true);
        var runner = new DatabaseContainerRunner(adapter, "netscope-database-analyzer:latest", validator);

        var extraction = new RoslynModelExtractionResult
        {
            MetadataCapabilities = ["CoreRelational", "KeylessEntities"],
            Contexts =
            [
                new DiscoveredDbContext
                {
                    Name = "SampleDbContext",
                    Entities =
                    [
                        new DiscoveredEntity
                        {
                            EntityName = "TestEntity",
                            TableName = "TestEntities",
                            MappingType = EntityMappingType.Table,
                            KeyType = EntityKeyType.Unknown, // Will trigger DB01
                            PrimaryLocation = new SourceLocation("Models/TestEntity.cs", 10, 5)
                        }
                    ]
                }
            ]
        };

        var analysisId = 123;
        var token = Guid.NewGuid();

        var guardProvider = new InMemoryAnalysisLeaseGuardProvider();
        await using var guard = await guardProvider.AcquireGuardAsync(analysisId, token);

        var result = await runner.RunAnalysisAsync(extraction, analysisId, token, guard);

        Assert.NotNull(result);
        Assert.Equal("Success", result.Status);
        Assert.Single(result.Findings);
        Assert.Equal("DB01", result.Findings[0].Rule);
    }

    [DockerFact]
    public async Task DockerContainer_RunsWithPinnedDigest_Successfully()
    {
        // Получаем pinned sha256 id собранного образа
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = "inspect --format \"{{.Id}}\" netscope-database-analyzer:latest",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) return;
        var imageId = (await proc.StandardOutput.ReadToEndAsync()).Trim();
        await proc.WaitForExitAsync();

        if (string.IsNullOrWhiteSpace(imageId) || !imageId.StartsWith("sha256:"))
        {
            return;
        }

        var adapter = new DockerCliProcessAdapter();
        var validator = new TestLeaseValidator(true);
        var runner = new DatabaseContainerRunner(adapter, imageId, validator);

        var extraction = new RoslynModelExtractionResult
        {
            MetadataCapabilities = ["CoreRelational", "KeylessEntities"],
            Contexts =
            [
                new DiscoveredDbContext
                {
                    Name = "SampleDbContext",
                    Entities =
                    [
                        new DiscoveredEntity
                        {
                            EntityName = "TestEntity",
                            TableName = "TestEntities",
                            MappingType = EntityMappingType.Table,
                            KeyType = EntityKeyType.Unknown,
                            PrimaryLocation = new SourceLocation("Models/TestEntity.cs", 10, 5)
                        }
                    ]
                }
            ]
        };

        var analysisId = 124;
        var token = Guid.NewGuid();

        var result = await runner.RunAnalysisAsync(extraction, analysisId, token);

        Assert.NotNull(result);
        Assert.Equal("Success", result.Status);
        Assert.Single(result.Findings);
        Assert.Equal("DB01", result.Findings[0].Rule);
    }

    [DockerFact]
    public async Task DockerContainer_FullRoslynExtractionAndDatabaseAnalysis_SucceedsWithDB01Finding()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "netscope_test_repo_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var sourceCode = """
                using Microsoft.EntityFrameworkCore;

                namespace TestApp;

                public class SampleDbContext : DbContext
                {
                    public DbSet<TestEntity> TestEntities => Set<TestEntity>();
                }

                public class TestEntity
                {
                    public string Name { get; set; } = string.Empty;
                }
                """;

            await File.WriteAllTextAsync(Path.Combine(tempDir, "SampleDbContext.cs"), sourceCode);

            var extractor = new RoslynMetadataExtractor();
            var extraction = await extractor.ExtractAsync(tempDir);

            Assert.Single(extraction.Contexts);
            Assert.Equal("SampleDbContext", extraction.Contexts[0].Name);

            var adapter = new DockerCliProcessAdapter();
            var validator = new TestLeaseValidator(true);
            var runner = new DatabaseContainerRunner(adapter, "netscope-database-analyzer:latest", validator);

            var analysisId = 125;
            var token = Guid.NewGuid();

            var result = await runner.RunAnalysisAsync(extraction, analysisId, token);

            Assert.NotNull(result);
            Assert.Equal("Success", result.Status);
            Assert.Single(result.Findings);
            Assert.Equal("DB01", result.Findings[0].Rule);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private sealed class TestLeaseValidator(bool isValid) : ILeaseOwnershipValidator
    {
        public Task<bool> ValidateOwnershipAsync(int analysisId, Guid executionToken, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(isValid);
        }
    }
}
