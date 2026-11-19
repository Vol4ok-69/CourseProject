using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DatabaseAnalyzer.Process;

public sealed partial class DockerCliProcessAdapter : IContainerProcessAdapter
{
    private const int MaxLogChars = 64 * 1024; // 64 KB max stdout/stderr
    private readonly string _dockerCommand;

    public DockerCliProcessAdapter(string dockerCommand = "docker")
    {
        _dockerCommand = string.IsNullOrWhiteSpace(dockerCommand) ? "docker" : dockerCommand;
    }

    public async Task<ProcessExecutionResult> RunAsync(
        ContainerRunOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Валидация входных параметров для предотвращения инъекции аргументов
        ValidateOptions(options);

        var outputFullPath = Path.GetFullPath(options.OutputFilePath);
        var outputDir = Path.GetDirectoryName(outputFullPath) ?? Path.GetTempPath();

        if (!Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        // 1. docker create с жесткими ограничениями безопасности и изоляции через ArgumentList
        var createArgs = BuildCreateArgumentList(options);

        var createResult = await RunCommandAsync(_dockerCommand, createArgs, TimeSpan.FromSeconds(30), cancellationToken);
        if (createResult.ExitCode != 0)
        {
            return new ProcessExecutionResult
            {
                ExitCode = createResult.ExitCode,
                StandardOutput = TruncateLog(createResult.StandardOutput),
                StandardError = TruncateLog($"Failed to create container: {createResult.StandardError}")
            };
        }

        var containerId = createResult.StandardOutput.Trim();
        var isOomKilled = false;
        var isTimedOut = false;
        var isCancelled = false;
        var exitCode = -1;
        var stdout = string.Empty;
        var stderr = string.Empty;

        try
        {
            // 2. docker start
            var startResult = await RunCommandAsync(_dockerCommand, ["start", containerId], TimeSpan.FromSeconds(30), cancellationToken);
            if (startResult.ExitCode != 0)
            {
                return new ProcessExecutionResult
                {
                    ExitCode = startResult.ExitCode,
                    StandardOutput = TruncateLog(startResult.StandardOutput),
                    StandardError = TruncateLog($"Failed to start container {containerId}: {startResult.StandardError}")
                };
            }

            // 3. docker wait с ограничением по времени (wall-clock limit)
            using var timeoutCts = new CancellationTokenSource(options.Timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                var waitResult = await RunCommandAsync(_dockerCommand, ["wait", containerId], options.Timeout, linkedCts.Token);
                if (int.TryParse(waitResult.StandardOutput.Trim(), out var parsedCode))
                {
                    exitCode = parsedCode;
                }
                else
                {
                    exitCode = waitResult.ExitCode;
                }
            }
            catch (OperationCanceledException)
            {
                if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    isTimedOut = true;
                }
                else
                {
                    isCancelled = true;
                }
            }

            // 4. Проверка статуса OOM через docker inspect
            try
            {
                var inspectResult = await RunCommandAsync(_dockerCommand, ["inspect", "--format", "{{.State.OOMKilled}}", containerId], TimeSpan.FromSeconds(10), CancellationToken.None);
                if (bool.TryParse(inspectResult.StandardOutput.Trim(), out var oom))
                {
                    isOomKilled = oom;
                }
            }
            catch
            {
                // Игнорируем сбой inspect
            }

            // 5. Чтение логов (stdout/stderr) с ограничением размера
            try
            {
                var logsResult = await RunCommandAsync(_dockerCommand, ["logs", "--tail", "200", containerId], TimeSpan.FromSeconds(10), CancellationToken.None);
                stdout = TruncateLog(logsResult.StandardOutput);
                stderr = TruncateLog(logsResult.StandardError);
            }
            catch
            {
                // Игнорируем сбой logs
            }
        }
        finally
        {
            // 6. Гарантированная остановка и удаление контейнера
            try
            {
                if (isTimedOut || isCancelled)
                {
                    await RunCommandAsync(_dockerCommand, ["kill", containerId], TimeSpan.FromSeconds(10), CancellationToken.None);
                }
                await RunCommandAsync(_dockerCommand, ["rm", "-f", containerId], TimeSpan.FromSeconds(15), CancellationToken.None);
            }
            catch
            {
                // Best effort cleanup
            }
        }

        return new ProcessExecutionResult
        {
            ExitCode = exitCode,
            IsOomKilled = isOomKilled,
            IsTimedOut = isTimedOut,
            IsCancelled = isCancelled,
            StandardOutput = stdout,
            StandardError = stderr
        };
    }

    /// <summary>
    /// Формирует структурированный список аргументов docker create с флагами строгой изоляции (ADR-001)
    /// и целевого механизма файлового обмена (хостовые bind mounts или Docker Named Volume с volume-subpath).
    /// </summary>
    public static IReadOnlyList<string> BuildCreateArgumentList(ContainerRunOptions options)
    {
        ValidateOptions(options);

        var args = new List<string>
        {
            "create",
            "--network", "none",
            "--read-only",
            "--user", "10001:10001",
            "--security-opt", "no-new-privileges",
            "--cap-drop", "ALL",
            "--cpus", options.CpuLimit,
            "--memory", options.MemoryLimit,
            "--pids-limit", options.PidsLimit.ToString(CultureInfo.InvariantCulture),
            "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m"
        };

        var outputFileName = Path.GetFileName(options.OutputFilePath);
        if (string.IsNullOrWhiteSpace(outputFileName))
        {
            outputFileName = "output.json";
        }

        if (!string.IsNullOrWhiteSpace(options.ExchangeVolume))
        {
            var (inputSubpath, outputSubpath) = ResolveVolumeSubpaths(options);

            args.Add("--mount");
            args.Add($"type=volume,source={options.ExchangeVolume},target=/exchange/in,volume-subpath={inputSubpath},readonly");

            args.Add("--mount");
            args.Add($"type=volume,source={options.ExchangeVolume},target=/exchange/out,volume-subpath={outputSubpath}");

            args.Add(options.Image);
            args.Add("--input");
            args.Add("/exchange/in/input.json");
            args.Add("--output");
            args.Add($"/exchange/out/{outputFileName}");
        }
        else
        {
            var inputFullPath = Path.GetFullPath(options.InputFilePath);
            var outputFullPath = Path.GetFullPath(options.OutputFilePath);
            var outputDir = Path.GetDirectoryName(outputFullPath) ?? Path.GetTempPath();

            args.Add("-v");
            args.Add($"{inputFullPath}:/exchange/in/input.json:ro");

            args.Add("-v");
            args.Add($"{outputDir}:/exchange/out:rw");

            args.Add(options.Image);
            args.Add("--input");
            args.Add("/exchange/in/input.json");
            args.Add("--output");
            args.Add($"/exchange/out/{outputFileName}");
        }

        if (options.TestDelayMs > 0)
        {
            args.Add("--test-delay-ms");
            args.Add(options.TestDelayMs.ToString(CultureInfo.InvariantCulture));
        }

        return args;
    }

    /// <summary>
    /// Формирует строку аргументов docker create (для обратной совместимости и строкового вывода).
    /// </summary>
    public static string BuildCreateArguments(ContainerRunOptions options)
    {
        var argList = BuildCreateArgumentList(options);
        return string.Join(" ", argList.Select(QuoteIfNeeded));
    }

    private static string QuoteIfNeeded(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        if (arg.Contains(' ') || arg.Contains('\t') || arg.Contains('"'))
        {
            return $"\"{arg.Replace("\"", "\\\"")}\"";
        }
        return arg;
    }

    /// <summary>
    /// Вычисляет и валидирует изолированные относительные пути внутри Docker-тома обмена данными.
    /// Защищает от path traversal, directory escape и доступа к чужим каталогам анализов.
    /// </summary>
    public static (string inputSubpath, string outputSubpath) ResolveVolumeSubpaths(ContainerRunOptions options)
    {
        string runId;

        if (!string.IsNullOrWhiteSpace(options.ExchangeDirectory))
        {
            var normalizedExchangeDir = options.ExchangeDirectory.Replace('\\', '/').TrimEnd('/');
            const string marker = "netscope-database-analyzer/";
            var markerIdx = normalizedExchangeDir.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

            if (markerIdx >= 0)
            {
                runId = normalizedExchangeDir[(markerIdx + marker.Length)..].Trim('/');
            }
            else
            {
                runId = Path.GetFileName(normalizedExchangeDir);
            }
        }
        else
        {
            var normalizedInput = options.InputFilePath.Replace('\\', '/').TrimEnd('/');
            var inDir = Path.GetDirectoryName(normalizedInput)?.Replace('\\', '/') ?? string.Empty;
            var parentDir = Path.GetDirectoryName(inDir)?.Replace('\\', '/') ?? string.Empty;
            runId = Path.GetFileName(parentDir);

            if (string.IsNullOrWhiteSpace(runId))
            {
                runId = Path.GetFileName(inDir);
            }
        }

        ValidateSubpathSegment(runId, "сессии анализа");

        var inputSubpath = $"{runId}/in";
        var outputSubpath = $"{runId}/out";

        ValidateSubpath(inputSubpath);
        ValidateSubpath(outputSubpath);

        return (inputSubpath, outputSubpath);
    }

    private static void ValidateSubpathSegment(string segment, string contextName)
    {
        if (string.IsNullOrWhiteSpace(segment) ||
            segment == "." ||
            segment == ".." ||
            segment.Contains("..", StringComparison.Ordinal) ||
            segment.StartsWith('/') ||
            segment.Contains('\\') ||
            !ValidSegmentRegex().IsMatch(segment))
        {
            throw new ArgumentException($"Недопустимый идентификатор {contextName} для тома обмена: '{segment}'.", nameof(segment));
        }
    }

    private static void ValidateSubpath(string subpath)
    {
        var segments = subpath.Split('/');
        foreach (var segment in segments)
        {
            ValidateSubpathSegment(segment, "пути");
        }
    }

    private static void ValidateOptions(ContainerRunOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Image) ||
            !ValidImageRegex().IsMatch(options.Image))
        {
            throw new ArgumentException("Недопустимое имя или digest Docker-образа.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.CpuLimit) ||
            !ValidCpuRegex().IsMatch(options.CpuLimit))
        {
            throw new ArgumentException("Недопустимый формат ограничения CPU.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.MemoryLimit) ||
            !ValidMemoryRegex().IsMatch(options.MemoryLimit))
        {
            throw new ArgumentException("Недопустимый формат ограничения памяти.", nameof(options));
        }

        if (options.PidsLimit <= 0 || options.PidsLimit > 4096)
        {
            throw new ArgumentException("Недопустимое ограничение PID.", nameof(options));
        }

        if (!string.IsNullOrWhiteSpace(options.ExchangeVolume) &&
            !ValidVolumeRegex().IsMatch(options.ExchangeVolume))
        {
            throw new ArgumentException("Недопустимое имя Docker-тома обмена данными.", nameof(options));
        }
    }

    private static string TruncateLog(string log)
    {
        if (string.IsNullOrEmpty(log)) return string.Empty;
        if (log.Length <= MaxLogChars) return log;

        return log[..MaxLogChars] + "\n...[TRUNCATED]";
    }

    public static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        System.Diagnostics.Process process;
        try
        {
            process = new System.Diagnostics.Process { StartInfo = psi };
            process.Start();
        }
        catch (Exception ex)
        {
            return (127, string.Empty, $"Failed to start process '{fileName}': {ex.Message}");
        }

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                return (process.ExitCode, stdout, stderr);
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

                throw;
            }
        }
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_.\-:/@]+$")]
    private static partial Regex ValidImageRegex();

    [GeneratedRegex(@"^[0-9]+(\.[0-9]+)?$")]
    private static partial Regex ValidCpuRegex();

    [GeneratedRegex(@"^[0-9]+[kmgKMG]?$")]
    private static partial Regex ValidMemoryRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex ValidVolumeRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9_][a-zA-Z0-9_.-]*$")]
    private static partial Regex ValidSegmentRegex();
}
