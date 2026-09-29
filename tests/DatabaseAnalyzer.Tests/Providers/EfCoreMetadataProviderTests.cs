using Application.Analyzers;
using DatabaseAnalyzer.Providers;

namespace DatabaseAnalyzer.Tests.Providers;

public sealed class EfCoreMetadataProviderTests
{
    [Fact]
    public async Task GetMetadataAsync_WhenRepositoryPathDoesNotExist_ThrowsDirectoryNotFoundException()
    {
        var provider = new EfCoreMetadataProvider();

        var context = new AnalyzerContext
        {
            RepositoryPath = Path.Combine(
                Path.GetTempPath(),
                Guid.NewGuid().ToString()),
            CommitHash = "test"
        };

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => provider.GetMetadataAsync(context));
    }

    [Fact]
    public async Task GetMetadataAsync_WhenRepositoryHasNoProjects_ReturnsEmptyMetadata()
    {
        var repositoryPath = Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString());

        Directory.CreateDirectory(repositoryPath);

        try
        {
            var provider = new EfCoreMetadataProvider();

            var context = new AnalyzerContext
            {
                RepositoryPath = repositoryPath,
                CommitHash = "test"
            };

            var metadata = await provider.GetMetadataAsync(context);

            Assert.Empty(metadata.Entities);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task GetMetadataAsync_WhenRepositoryContainsProject_ReturnsMetadata()
    {
        var repositoryPath = Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString());

        Directory.CreateDirectory(repositoryPath);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(repositoryPath, "TestProject.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                </Project>
                """);

            var provider = new EfCoreMetadataProvider();

            var context = new AnalyzerContext
            {
                RepositoryPath = repositoryPath,
                CommitHash = "test"
            };

            var metadata = await provider.GetMetadataAsync(context);

            Assert.NotNull(metadata);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }
}