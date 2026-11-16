using System.Text.Json;
using NetScope.DatabaseAnalyzer.Runner.Contracts;
using NetScope.DatabaseAnalyzer.Runner.Rules;

namespace NetScope.DatabaseAnalyzer.Runner;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? inputPath = null;
        string? outputPath = null;
        var testDelayMs = 0;

        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                inputPath = args[++i];
            }
            else if (string.Equals(args[i], "--output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                outputPath = args[++i];
            }
            else if (string.Equals(args[i], "--test-delay-ms", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                int.TryParse(args[++i], out testDelayMs);
            }
        }

        if (string.IsNullOrWhiteSpace(inputPath) || string.IsNullOrWhiteSpace(outputPath))
        {
            await Console.Error.WriteLineAsync("Usage: NetScope.DatabaseAnalyzer.Runner --input <path> --output <path> [--test-delay-ms <ms>]");
            return 2; // Invalid arguments
        }

        try
        {
            if (!File.Exists(inputPath))
            {
                await Console.Error.WriteLineAsync($"Input file not found: {inputPath}");
                return 2;
            }

            var inputJson = await File.ReadAllTextAsync(inputPath);
            var input = JsonSerializer.Deserialize(inputJson, RunnerJsonContext.Default.AnalysisInputContract);

            if (input == null)
            {
                await Console.Error.WriteLineAsync("Failed to deserialize input document.");
                return 2;
            }

            // Check schema version major
            var majorVersion = input.SchemaVersion.Split('.')[0];
            if (majorVersion != "1")
            {
                await Console.Error.WriteLineAsync($"Unsupported schema version: {input.SchemaVersion}. Expected major version 1.");
                return 3; // Schema version mismatch
            }

            if (testDelayMs > 0)
            {
                await Task.Delay(testDelayMs);
            }

            var engine = new DatabaseRulesEngine();
            var output = engine.Evaluate(input);

            // If baseline capability is missing, exit code 2
            if (output.Status == "Failed" && output.Errors.Any(e => e.Code == "MISSING_BASELINE_CAPABILITY"))
            {
                await Console.Error.WriteLineAsync("Missing baseline capability 'CoreRelational'.");
                await WriteOutputAtomicAsync(outputPath, output);
                return 2;
            }

            await WriteOutputAtomicAsync(outputPath, output);

            return 0; // Success or PartialSuccess
        }
        catch (JsonException ex)
        {
            await Console.Error.WriteLineAsync($"JSON error: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Internal error: {ex.Message}");
            return 1;
        }
    }

    private static async Task WriteOutputAtomicAsync(string outputPath, AnalysisOutputContract output)
    {
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var tempPath = outputPath + ".tmp";
        var outputJson = JsonSerializer.Serialize(output, RunnerJsonContext.Default.AnalysisOutputContract);

        await File.WriteAllTextAsync(tempPath, outputJson);
        File.Move(tempPath, outputPath, overwrite: true);
    }
}
