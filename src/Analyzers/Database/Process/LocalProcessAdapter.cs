using System.Diagnostics;

namespace DatabaseAnalyzer.Process;

/// <summary>
/// Адаптер локального запуска процесса Runner.
/// ВНИМАНИЕ: Разрешён ИСКЛЮЧИТЕЛЬНО для тестов и CI-окружений, где недоступен Docker daemon!
/// Не обеспечивает безопасность и изоляцию. Запрещён к регистрации в production DI-конфигурации (ADR-001).
/// </summary>
public sealed class LocalProcessAdapter : IContainerProcessAdapter
{
    private readonly string? _runnerDllPath;

    public LocalProcessAdapter(string? runnerDllPath = null)
    {
        _runnerDllPath = runnerDllPath;
    }

    public async Task<ProcessExecutionResult> RunAsync(
        ContainerRunOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var runnerDll = ResolveRunnerDllPath(_runnerDllPath);
        if (!File.Exists(runnerDll))
        {
            return new ProcessExecutionResult
            {
                ExitCode = 1,
                StandardError = $"Runner DLL not found at: {runnerDll}"
            };
        }

        var delayArg = options.TestDelayMs > 0 ? $" --test-delay-ms {options.TestDelayMs}" : string.Empty;
        var arguments = $"\"{runnerDll}\" --input \"{Path.GetFullPath(options.InputFilePath)}\" --output \"{Path.GetFullPath(options.OutputFilePath)}\"{delayArg}";

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new System.Diagnostics.Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = new CancellationTokenSource(options.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var isTimedOut = false;
        var isCancelled = false;

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return new ProcessExecutionResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = stdout,
                StandardError = stderr
            };
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore kill errors
            }

            if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                isTimedOut = true;
            }
            else
            {
                isCancelled = true;
            }

            return new ProcessExecutionResult
            {
                ExitCode = -1,
                IsTimedOut = isTimedOut,
                IsCancelled = isCancelled,
                StandardError = isTimedOut ? "Execution timed out." : "Execution was cancelled."
            };
        }
    }

    private static string ResolveRunnerDllPath(string? customPath)
    {
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
        {
            return customPath;
        }

        var candidates = new[]
        {
            // Direct path relative to current app domain
            Path.Combine(AppContext.BaseDirectory, "NetScope.DatabaseAnalyzer.Runner.dll"),
            Path.Combine(AppContext.BaseDirectory, "publish", "NetScope.DatabaseAnalyzer.Runner.dll"),
            // Debug build of Database.Runner
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Analyzers", "Database.Runner", "bin", "Debug", "net10.0", "NetScope.DatabaseAnalyzer.Runner.dll")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "Analyzers", "Database.Runner", "bin", "Debug", "net10.0", "NetScope.DatabaseAnalyzer.Runner.dll")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Analyzers", "Database.Runner", "bin", "Release", "net10.0", "NetScope.DatabaseAnalyzer.Runner.dll"))
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return customPath ?? candidates[0];
    }
}
