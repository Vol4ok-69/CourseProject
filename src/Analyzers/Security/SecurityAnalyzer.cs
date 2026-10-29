using Application.Analyzers;
using Domain.Enums;
using Microsoft.CodeAnalysis.CSharp;
using SecurityAnalyzer.Rules;

namespace SecurityAnalyzer;

public sealed class SecurityAnalyzer : IAnalyzer
{
    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        ".idea",
        ".vscode",
        "bin",
        "obj",
        "packages",
        "node_modules",
        "TestResults",
        "artifacts"
    };

    private readonly ISecurityRule[] _csharpRules =
    [
        new Sec001HardcodedSecretsRule(),
        new Sec003InsecureCorsRule(),
        new Sec004WeakCryptoRule(),
        new Sec005DisabledCertificateValidationRule()
    ];

    private readonly IConfigSecurityRule _configRule = new Sec002PlaintextConfigSecretsRule();

    public AnalyzerType Type => AnalyzerType.Security;

    public async Task<IReadOnlyList<AnalyzerFinding>> AnalyzeAsync(
        AnalyzerContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.RepositoryPath))
        {
            throw new ArgumentException("Repository path must not be empty.", nameof(context));
        }

        var repositoryPath = Path.GetFullPath(context.RepositoryPath);

        if (!Directory.Exists(repositoryPath))
        {
            throw new DirectoryNotFoundException($"Repository path not found: {repositoryPath}");
        }

        var findings = new List<AnalyzerFinding>();

        // 1. Анализ C#-файлов
        foreach (var file in EnumerateFiles(repositoryPath, "*.cs", cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var sourceCode = await File.ReadAllTextAsync(file, cancellationToken);
                var relativePath = Path.GetRelativePath(repositoryPath, file);

                var tree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: cancellationToken);
                var root = await tree.GetRootAsync(cancellationToken);

                foreach (var rule in _csharpRules)
                {
                    findings.AddRange(rule.Analyze(root, relativePath));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Не останавливаем анализ всего репозитория из-за сбоя разбора отдельного файла
            }
        }

        // 2. Анализ конфигурационных файлов appsettings*.json
        foreach (var file in EnumerateFiles(repositoryPath, "appsettings*.json", cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var jsonContent = await File.ReadAllTextAsync(file, cancellationToken);
                var relativePath = Path.GetRelativePath(repositoryPath, file);

                findings.AddRange(_configRule.Analyze(jsonContent, relativePath));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Не останавливаем анализ всего репозитория из-за сбоя разбора отдельного файла
            }
        }

        return findings;
    }

    private static IEnumerable<string> EnumerateFiles(string directory, string searchPattern, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, searchPattern, SearchOption.TopDirectoryOnly);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }

        string[] subdirectories;
        try
        {
            subdirectories = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var subdirectory in subdirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directoryName = Path.GetFileName(subdirectory);
            if (IgnoredDirectoryNames.Contains(directoryName))
            {
                continue;
            }

            try
            {
                var attributes = File.GetAttributes(subdirectory);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var file in EnumerateFiles(subdirectory, searchPattern, cancellationToken))
            {
                yield return file;
            }
        }
    }
}
