using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DatabaseAnalyzer.Process;

public sealed partial class DockerCliProcessAdapter : IContainerProcessAdapter
{
    private const int MaxLogChars = 64 * 1024; // 64 KB max stdout/stderr

    public async Task<ProcessExecutionResult> RunAsync(
        ContainerRunOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Валидация входных параметров для предотвращения инъекции аргументов
        ValidateOptions(options);

        var inputFullPath = Path.GetFullPath(options.InputFilePath);
        var outputFullPath = Path.GetFullPath(options.OutputFilePath);
        var outputDir = Path.GetDirectoryName(outputFullPath) ?? Path.GetTempPath();

        if (!Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        var delayArg = options.TestDelayMs > 0 ? $" --test-delay-ms {options.TestDelayMs}" : string.Empty;

        // 1. docker create с жесткими ограничениями безопасности и изоляции
        var createArgs = $"create --network none --read-only --user 10001:10001 " +
                         $"--security-opt no-new-privileges --cap-drop ALL " +
                         $"--cpus {options.CpuLimit} --memory {options.MemoryLimit} --pids-limit {options.PidsLimit} " +
                         $"-v \"{inputFullPath}:/exchange/in/input.json:ro\" " +
                         $"-v \"{outputDir}:/exchange/out:rw\" " +
                         $"{options.Image} --input /exchange/in/input.json --output /exchange/out/{Path.GetFileName(outputFullPath)}{delayArg}";

        var createResult = await RunCommandAsync("docker", createArgs, TimeSpan.FromSeconds(30), cancellationToken);
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
            var startResult = await RunCommandAsync("docker", $"start {containerId}", TimeSpan.FromSeconds(30), cancellationToken);
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
                var waitResult = await RunCommandAsync("docker", $"wait {containerId}", options.Timeout, linkedCts.Token);
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
                var inspectResult = await RunCommandAsync("docker", $"inspect --format \"{{{{.State.OOMKilled}}}}\" {containerId}", TimeSpan.FromSeconds(10), CancellationToken.None);
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
                var logsResult = await RunCommandAsync("docker", $"logs --tail 200 {containerId}", TimeSpan.FromSeconds(10), CancellationToken.None);
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
                    await RunCommandAsync("docker", $"kill {containerId}", TimeSpan.FromSeconds(10), CancellationToken.None);
                }
                await RunCommandAsync("docker", $"rm -f {containerId}", TimeSpan.FromSeconds(15), CancellationToken.None);
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

    private static void ValidateOptions(ContainerRunOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Image) ||
            !Regex.IsMatch(options.Image, @"^[a-zA-Z0-9_.\-:/@]+$"))
        {
            throw new ArgumentException("Недопустимое имя или digest Docker-образа.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.CpuLimit) ||
            !Regex.IsMatch(options.CpuLimit, @"^[0-9]+(\.[0-9]+)?$"))
        {
            throw new ArgumentException("Недопустимый формат ограничения CPU.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.MemoryLimit) ||
            !Regex.IsMatch(options.MemoryLimit, @"^[0-9]+[kmgKMG]?$"))
        {
            throw new ArgumentException("Недопустимый формат ограничения памяти.", nameof(options));
        }

        if (options.PidsLimit <= 0 || options.PidsLimit > 4096)
        {
            throw new ArgumentException("Недопустимое ограничение PID.", nameof(options));
        }
    }

    private static string TruncateLog(string log)
    {
        if (string.IsNullOrEmpty(log)) return string.Empty;
        if (log.Length <= MaxLogChars) return log;

        return log[..MaxLogChars] + "\n...[TRUNCATED]";
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunCommandAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
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
