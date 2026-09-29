using Application.Analyzers;
using DatabaseAnalyzer.Mapping;
using DatabaseAnalyzer.Providers;
using Moq;

namespace DatabaseAnalyzer.Tests.Providers;

public sealed class EfCoreMetadataProviderTests
{
    [Fact]
    public async Task GetMetadataAsync_WhenRepositoryPathDoesNotExist_ThrowsDirectoryNotFoundException()
    {
        var inspector = new Mock<IEfCoreModelInspector>();
        var mapper = new EfCoreMetadataMapper();

        var provider = new EfCoreMetadataProvider(
            inspector.Object,
            mapper);

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
            var inspector = new Mock<IEfCoreModelInspector>();
            var mapper = new EfCoreMetadataMapper();

            var provider = new EfCoreMetadataProvider(
                inspector.Object,
                mapper);

            var context = new AnalyzerContext
            {
                RepositoryPath = repositoryPath,
                CommitHash = "test"
            };

            var metadata = await provider.GetMetadataAsync(context);

            Assert.Empty(metadata.Entities);

            inspector.Verify(
                x => x.InspectAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task GetMetadataAsync_WhenRepositoryContainsProject_PassesProjectToInspector()
    {
        var repositoryPath = Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString());

        Directory.CreateDirectory(repositoryPath);

        try
        {
            var projectPath = Path.Combine(
                repositoryPath,
                "TestProject.csproj");

            await File.WriteAllTextAsync(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                </Project>
                """);

            var inspector = new Mock<IEfCoreModelInspector>();
            var mapper = new EfCoreMetadataMapper();

            inspector
                .Setup(x => x.InspectAsync(
                    projectPath,
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new NotSupportedException());

            var provider = new EfCoreMetadataProvider(
                inspector.Object,
                mapper);

            await Assert.ThrowsAsync<NotSupportedException>(
                () => provider.GetMetadataAsync(
                    new AnalyzerContext
                    {
                        RepositoryPath = repositoryPath,
                        CommitHash = "test"
                    }));

            inspector.Verify(
                x => x.InspectAsync(
                    projectPath,
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task GetMetadataAsync_WhenRepositoryContainsMultipleProjects_ThrowsInvalidOperationException()
    {
        var repositoryPath = Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString());

        Directory.CreateDirectory(repositoryPath);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(repositoryPath, "Project1.csproj"),
                "<Project />");

            await File.WriteAllTextAsync(
                Path.Combine(repositoryPath, "Project2.csproj"),
                "<Project />");

            var inspector = new Mock<IEfCoreModelInspector>();
            var mapper = new EfCoreMetadataMapper();

            var provider = new EfCoreMetadataProvider(
                inspector.Object,
                mapper);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.GetMetadataAsync(
                    new AnalyzerContext
                    {
                        RepositoryPath = repositoryPath,
                        CommitHash = "test"
                    }));

            inspector.Verify(
                x => x.InspectAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }
}