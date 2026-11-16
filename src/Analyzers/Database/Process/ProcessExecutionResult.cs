namespace DatabaseAnalyzer.Process;

public sealed class ProcessExecutionResult
{
    public int ExitCode { get; init; }

    public bool IsOomKilled { get; init; }

    public bool IsTimedOut { get; init; }

    public bool IsCancelled { get; init; }

    public string StandardOutput { get; init; } = string.Empty;

    public string StandardError { get; init; } = string.Empty;

    public bool IsSuccess => ExitCode == 0 && !IsOomKilled && !IsTimedOut && !IsCancelled;
}
