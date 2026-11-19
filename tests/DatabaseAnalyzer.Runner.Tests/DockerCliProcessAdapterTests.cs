using System.Diagnostics;
using DatabaseAnalyzer.Process;

namespace DatabaseAnalyzer.Runner.Tests;

public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!DockerCliProcessAdapterTests.IsDockerAvailable())
        {
            Skip = "Тест пропущен: Docker демона нет на хосте.";
        }
    }
}

public sealed class DockerCliProcessAdapterTests
{
    internal static bool IsDockerAvailable()
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
            if (!proc.WaitForExit(3000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public void BuildCreateArguments_WithoutExchangeVolume_GeneratesCorrectHostBindMounts()
    {
        var options = new ContainerRunOptions
        {
            Image = "netscope-database-analyzer:latest",
            InputFilePath = Path.Combine(Path.GetTempPath(), "test_in", "input.json"),
            OutputFilePath = Path.Combine(Path.GetTempPath(), "test_out", "output.json"),
            ExchangeVolume = null,
            CpuLimit = "1.0",
            MemoryLimit = "512m",
            PidsLimit = 64
        };

        var args = DockerCliProcessAdapter.BuildCreateArguments(options);

        Assert.Contains("--network none", args);
        Assert.Contains("--read-only", args);
        Assert.Contains("--user 10001:10001", args);
        Assert.Contains("--security-opt no-new-privileges", args);
        Assert.Contains("--cap-drop ALL", args);
        Assert.Contains("--cpus 1.0", args);
        Assert.Contains("--memory 512m", args);
        Assert.Contains("--pids-limit 64", args);
        Assert.Contains("--tmpfs /tmp:rw,noexec,nosuid,size=64m", args);
        Assert.Contains(":/exchange/in/input.json:ro", args);
        Assert.Contains(":/exchange/out:rw", args);
        Assert.Contains("--input /exchange/in/input.json", args);
        Assert.Contains("--output /exchange/out/output.json", args);
    }

    [Fact]
    public void BuildCreateArguments_WithExchangeVolume_MountsOnlySeparateSubpaths_AndNeverMountsWholeVolume()
    {
        var inputPath = "/tmp/netscope-database-analyzer/10_tok/in/input.json";
        var outputPath = "/tmp/netscope-database-analyzer/10_tok/out/output.json";

        var options = new ContainerRunOptions
        {
            Image = "netscope-database-analyzer:latest",
            InputFilePath = inputPath,
            OutputFilePath = outputPath,
            ExchangeVolume = "courseproject-exchange-data",
            CpuLimit = "1.0",
            MemoryLimit = "512m",
            PidsLimit = 64
        };

        var args = DockerCliProcessAdapter.BuildCreateArguments(options);

        // 1. Проверяем, что весь том целиком НЕ монтируется
        Assert.DoesNotContain("courseproject-exchange-data:/tmp/netscope-database-analyzer", args);
        Assert.DoesNotContain("-v courseproject-exchange-data", args);

        // 2. Входные данные монтируются отдельным subpath в режиме readonly
        Assert.Contains("--mount type=volume,source=courseproject-exchange-data,target=/exchange/in,volume-subpath=10_tok/in,readonly", args);

        // 3. Выходной каталог монтируется отдельным subpath для записи
        Assert.Contains("--mount type=volume,source=courseproject-exchange-data,target=/exchange/out,volume-subpath=10_tok/out", args);

        // 4. Входные и выходные аргументы контейнера ссылаются на изолированные пути /exchange
        Assert.Contains("--input /exchange/in/input.json", args);
        Assert.Contains("--output /exchange/out/output.json", args);

        // 5. Все флаги изоляции безопасности сохранены
        Assert.Contains("--network none", args);
        Assert.Contains("--read-only", args);
        Assert.Contains("--user 10001:10001", args);
        Assert.Contains("--security-opt no-new-privileges", args);
        Assert.Contains("--cap-drop ALL", args);
        Assert.Contains("--tmpfs /tmp:rw,noexec,nosuid,size=64m", args);
    }

    [Fact]
    public void BuildCreateArgumentList_UsesSeparateArgumentsWithoutShellParsing()
    {
        var options = new ContainerRunOptions
        {
            Image = "netscope-database-analyzer:latest",
            InputFilePath = "/tmp/netscope-database-analyzer/42_d0b8/in/input.json",
            OutputFilePath = "/tmp/netscope-database-analyzer/42_d0b8/out/output.json",
            ExchangeVolume = "courseproject-exchange-data",
            CpuLimit = "1.0",
            MemoryLimit = "512m",
            PidsLimit = 64
        };

        var argList = DockerCliProcessAdapter.BuildCreateArgumentList(options);

        Assert.Equal("create", argList[0]);
        Assert.Contains("--network", argList);
        Assert.Contains("none", argList);
        Assert.Contains("--read-only", argList);
        Assert.Contains("--user", argList);
        Assert.Contains("10001:10001", argList);
        Assert.Contains("--mount", argList);
        Assert.Contains("type=volume,source=courseproject-exchange-data,target=/exchange/in,volume-subpath=42_d0b8/in,readonly", argList);
        Assert.Contains("type=volume,source=courseproject-exchange-data,target=/exchange/out,volume-subpath=42_d0b8/out", argList);
        Assert.Contains("--input", argList);
        Assert.Contains("/exchange/in/input.json", argList);
        Assert.Contains("--output", argList);
        Assert.Contains("/exchange/out/output.json", argList);
    }

    [Fact]
    public void BuildCreateArguments_WithTestDelay_IncludesDelayFlag()
    {
        var options = new ContainerRunOptions
        {
            Image = "netscope-database-analyzer:latest",
            InputFilePath = Path.Combine(Path.GetTempPath(), "in", "input.json"),
            OutputFilePath = Path.Combine(Path.GetTempPath(), "out", "output.json"),
            ExchangeVolume = null,
            TestDelayMs = 250
        };

        var args = DockerCliProcessAdapter.BuildCreateArguments(options);

        Assert.Contains("--test-delay-ms 250", args);
    }

    [Theory]
    [InlineData("bad volume name; rm -rf /")]
    [InlineData("vol$name")]
    [InlineData("vol/name")]
    [InlineData("vol\\name")]
    [InlineData(" vol")]
    public void ValidateOptions_RejectsInvalidVolumeNames(string invalidVolume)
    {
        var options = new ContainerRunOptions
        {
            Image = "netscope-database-analyzer:latest",
            InputFilePath = "in.json",
            OutputFilePath = "out.json",
            ExchangeVolume = invalidVolume
        };

        var ex = Assert.Throws<ArgumentException>(() => DockerCliProcessAdapter.BuildCreateArguments(options));
        Assert.Contains("Docker-тома", ex.Message);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("run1/../../root")]
    [InlineData("/etc/passwd")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("run1\\backslash")]
    public void ResolveVolumeSubpaths_PathTraversalAndEscape_ThrowsArgumentException(string maliciousPath)
    {
        var options = new ContainerRunOptions
        {
            Image = "netscope-database-analyzer:latest",
            InputFilePath = $"/tmp/netscope-database-analyzer/{maliciousPath}/in/input.json",
            OutputFilePath = $"/tmp/netscope-database-analyzer/{maliciousPath}/out/output.json",
            ExchangeVolume = "courseproject-exchange-data",
            ExchangeDirectory = $"/tmp/netscope-database-analyzer/{maliciousPath}"
        };

        Assert.Throws<ArgumentException>(() => DockerCliProcessAdapter.ResolveVolumeSubpaths(options));
    }

    [Fact]
    public void BuildCreateArguments_ParallelAnalyses_GeneratesCompletelyIsolatedSubpaths()
    {
        var options1 = new ContainerRunOptions
        {
            Image = "netscope-database-analyzer:latest",
            InputFilePath = "/tmp/netscope-database-analyzer/100_sessionA/in/input.json",
            OutputFilePath = "/tmp/netscope-database-analyzer/100_sessionA/out/output.json",
            ExchangeDirectory = "/tmp/netscope-database-analyzer/100_sessionA",
            ExchangeVolume = "courseproject-exchange-data"
        };

        var options2 = new ContainerRunOptions
        {
            Image = "netscope-database-analyzer:latest",
            InputFilePath = "/tmp/netscope-database-analyzer/200_sessionB/in/input.json",
            OutputFilePath = "/tmp/netscope-database-analyzer/200_sessionB/out/output.json",
            ExchangeDirectory = "/tmp/netscope-database-analyzer/200_sessionB",
            ExchangeVolume = "courseproject-exchange-data"
        };

        var args1 = DockerCliProcessAdapter.BuildCreateArguments(options1);
        var args2 = DockerCliProcessAdapter.BuildCreateArguments(options2);

        // Сессия А содержит только subpath sessionA
        Assert.Contains("volume-subpath=100_sessionA/in", args1);
        Assert.Contains("volume-subpath=100_sessionA/out", args1);
        Assert.DoesNotContain("200_sessionB", args1);

        // Сессия B содержит только subpath sessionB
        Assert.Contains("volume-subpath=200_sessionB/in", args2);
        Assert.Contains("volume-subpath=200_sessionB/out", args2);
        Assert.DoesNotContain("100_sessionA", args2);
    }

    [Fact]
    public async Task RunAsync_WhenDockerCliNotFound_ReturnsExitCode127()
    {
        var adapter = new DockerCliProcessAdapter("non-existent-docker-cli-binary-99999");
        var options = new ContainerRunOptions
        {
            Image = "netscope-database-analyzer:latest",
            InputFilePath = Path.Combine(Path.GetTempPath(), "in", "input.json"),
            OutputFilePath = Path.Combine(Path.GetTempPath(), "out", "output.json")
        };

        var result = await adapter.RunAsync(options);

        Assert.Equal(127, result.ExitCode);
        Assert.Contains("Failed to start process", result.StandardError);
    }

    [DockerFact]
    public async Task RunAsync_WhenImageNotFound_ReturnsFailureExitCode()
    {
        var adapter = new DockerCliProcessAdapter("docker");
        var options = new ContainerRunOptions
        {
            Image = "non-existent-netscope-image:never-built-99999",
            InputFilePath = Path.Combine(Path.GetTempPath(), "in", "input.json"),
            OutputFilePath = Path.Combine(Path.GetTempPath(), "out", "output.json")
        };

        var result = await adapter.RunAsync(options);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Failed to create container", result.StandardError);
    }
}
