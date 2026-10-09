using Application.Analyzers;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DatabaseAnalyzer.Providers;

public sealed class EfCoreDatabaseProjectResolver : IDatabaseProjectResolver
{
    private static readonly Regex DbContextDeclarationRegex = new(
        @":\s*(?:global::)?(?:[\w]+\.)*DbContext\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> IgnoredDirectoryNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".idea", "bin", "obj",
            "TestResults", "node_modules", "packages"
        };

    public Task<DatabaseProjectResolution?> ResolveAsync(
        AnalyzerContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var repositoryPath = Path.GetFullPath(context.RepositoryPath);

        if (!Directory.Exists(repositoryPath))
        {
            throw new DirectoryNotFoundException(
                $"Repository path was not found: {repositoryPath}");
        }

        var projectFiles = EnumerateProjectFiles(repositoryPath)
            .Where(path => !IsTestProject(path, repositoryPath))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        cancellationToken.ThrowIfCancellationRequested();



        string? explicitlySelectedProject = null;

        if (!string.IsNullOrWhiteSpace(context.ProjectPath))
        {
            explicitlySelectedProject = Path.GetFullPath(
                Path.Combine(repositoryPath, context.ProjectPath));

            if (!projectFiles.Contains(
                    explicitlySelectedProject,
                    StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Specified project was not found among production projects: " +
                    $"{context.ProjectPath}");
            }
        }

        if (projectFiles.Count == 0)
        {
            return Task.FromResult<DatabaseProjectResolution?>(null);
        }

        var contextProjects = projectFiles
            .Where(path => ContainsDbContextDeclaration(path, cancellationToken))
            .ToList();

        string contextProject;

        if (explicitlySelectedProject is not null &&
            contextProjects.Contains(
                explicitlySelectedProject,
                StringComparer.OrdinalIgnoreCase))
        {
            contextProject = explicitlySelectedProject;
        }
        else if (explicitlySelectedProject is not null)
        {
            var referencedContexts = contextProjects
                .Where(path => ReferencesProject(
                    explicitlySelectedProject,
                    path))
                .ToList();

            if (referencedContexts.Count == 1)
            {
                contextProject = referencedContexts[0];
            }
            else
            {
                throw new InvalidOperationException(
                    $"The explicitly selected project '{context.ProjectPath}' " +
                    "does not declare a DbContext and does not reference exactly " +
                    "one project that declares it.");
            }
        }
        else if (contextProjects.Count == 1)
        {
            contextProject = contextProjects[0];
        }
        else if (contextProjects.Count == 0)
        {
            return Task.FromResult<DatabaseProjectResolution?>(null);
        }
        else
        {
            throw CreateAmbiguousProjectException(
                "Multiple production projects declaring DbContext were found",
                contextProjects,
                repositoryPath);
        }

        var startupCandidates = projectFiles
            .Where(path => !string.Equals(
                path,
                contextProject,
                StringComparison.OrdinalIgnoreCase))
            .Where(path => ReferencesProject(path, contextProject))
            .ToList();

        string startupProject;

        if (explicitlySelectedProject is not null &&
            !string.Equals(
                explicitlySelectedProject,
                contextProject,
                StringComparison.OrdinalIgnoreCase))
        {
            startupProject = explicitlySelectedProject;

            if (!ReferencesProject(startupProject, contextProject))
            {
                throw new InvalidOperationException(
                    $"The selected startup project '{context.ProjectPath}' " +
                    $"does not reference the context project " +
                    $"'{Path.GetRelativePath(repositoryPath, contextProject)}'.");
            }
        }
        else if (startupCandidates.Count == 1)
        {
            startupProject = startupCandidates[0];
        }
        else if (startupCandidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No startup project referencing the context project was found: " +
                $"{Path.GetRelativePath(repositoryPath, contextProject)}");
        }
        else
        {
            throw CreateAmbiguousProjectException(
                "Multiple startup projects referencing the context project were found",
                startupCandidates,
                repositoryPath);
        }

        return Task.FromResult<DatabaseProjectResolution?>(
            new DatabaseProjectResolution(contextProject, startupProject));
    }

    private static IEnumerable<string> EnumerateProjectFiles(string repositoryPath)
    {
        foreach (var file in Directory.EnumerateFiles(repositoryPath, "*.csproj"))
        {
            yield return Path.GetFullPath(file);
        }

        foreach (var directory in Directory.EnumerateDirectories(repositoryPath))
        {
            if (IgnoredDirectoryNames.Contains(Path.GetFileName(directory)))
            {
                continue;
            }

            foreach (var file in EnumerateProjectFiles(directory))
            {
                yield return file;
            }
        }
    }

    private static bool IsTestProject(string projectPath, string repositoryPath)
    {
        var projectName = Path.GetFileNameWithoutExtension(projectPath);

        if (projectName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
            projectName.EndsWith(".Test", StringComparison.OrdinalIgnoreCase) ||
            projectName.Equals("Tests", StringComparison.OrdinalIgnoreCase) ||
            projectName.Equals("Test", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var relativePath = Path.GetRelativePath(repositoryPath, projectPath);

        return relativePath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment =>
                segment.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("test", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsDbContextDeclaration(
        string projectPath,
        CancellationToken cancellationToken)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath);

        if (projectDirectory is null)
        {
            return false;
        }

        return EnumerateSourceFiles(projectDirectory)
            .Any(file =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return DbContextDeclarationRegex.IsMatch(File.ReadAllText(file));
            });
    }

    private static IEnumerable<string> EnumerateSourceFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            yield return file;
        }

        foreach (var subdirectory in Directory.EnumerateDirectories(directory))
        {
            if (IgnoredDirectoryNames.Contains(Path.GetFileName(subdirectory)))
            {
                continue;
            }

            // Не заходим в каталоги вложенных проектов.
            if (Directory.EnumerateFiles(subdirectory, "*.csproj").Any())
            {
                continue;
            }

            foreach (var file in EnumerateSourceFiles(subdirectory))
            {
                yield return file;
            }
        }
    }

    private static bool ReferencesProject(string sourceProject, string targetProject)
    {
        var projectDirectory = Path.GetDirectoryName(sourceProject);

        if (projectDirectory is null)
        {
            return false;
        }

        var document = XDocument.Load(sourceProject);

        foreach (var reference in document.Descendants()
                     .Where(element => element.Name.LocalName == "ProjectReference"))
        {
            var include = reference.Attribute("Include")?.Value;

            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            var referencedProject = Path.GetFullPath(
                Path.Combine(projectDirectory, include));

            if (string.Equals(
                    referencedProject,
                    targetProject,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static InvalidOperationException CreateAmbiguousProjectException(
        string message,
        IReadOnlyCollection<string> candidates,
        string repositoryPath)
    {
        var candidatePaths = string.Join(
            Environment.NewLine,
            candidates.Select(path => Path.GetRelativePath(repositoryPath, path)));

        return new InvalidOperationException(
            $"{message}:{Environment.NewLine}{candidatePaths}{Environment.NewLine}" +
            "Specify ProjectPath to select the project.");
    }
}
