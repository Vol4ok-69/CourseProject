namespace DatabaseAnalyzer.Process;

public sealed class ContainerRunOptions
{
    public string Image { get; init; } = "netscope-database-analyzer:latest";

    public string InputFilePath { get; init; } = string.Empty;

    public string OutputFilePath { get; init; } = string.Empty;

    public string ExchangeDirectory { get; init; } = string.Empty;

    public string? ExchangeVolume { get; init; } = Environment.GetEnvironmentVariable("DOCKER_EXCHANGE_VOLUME");

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    public string MemoryLimit { get; init; } = "512m";

    public string CpuLimit { get; init; } = "1.0";

    public int PidsLimit { get; init; } = 64;

    public int TestDelayMs { get; init; }
}
