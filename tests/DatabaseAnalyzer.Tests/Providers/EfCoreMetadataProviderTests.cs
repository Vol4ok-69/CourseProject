using Application.Analyzers;
using DatabaseAnalyzer.Mapping;
using DatabaseAnalyzer.Metadata;
using DatabaseAnalyzer.Providers;
using Microsoft.EntityFrameworkCore.Metadata;
using Moq;

namespace DatabaseAnalyzer.Tests.Providers;

public sealed class EfCoreMetadataProviderTests
{
    [Fact]
    public async Task GetMetadataAsync_ShouldResolveProject()
    {
        var resolver = new Mock<IDatabaseProjectResolver>();
        var inspector = new Mock<IEfCoreModelInspector>();
        var mapper = new EfCoreMetadataMapper();

        var projectPath = Path.Combine(
            Path.GetTempPath(),
            "TestProject.csproj");

        resolver
            .Setup(x => x.ResolveAsync(
                It.IsAny<AnalyzerContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(projectPath);

        var provider = new EfCoreMetadataProvider(
            resolver.Object,
            inspector.Object,
            mapper);

        var context = new AnalyzerContext
        {
            RepositoryPath = Path.GetTempPath(),
            CommitHash = "test"
        };

        inspector
            .Setup(x => x.InspectAsync(
                projectPath,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException());

        await Assert.ThrowsAsync<NotSupportedException>(
            () => provider.GetMetadataAsync(context));

        resolver.Verify(
            x => x.ResolveAsync(
                context,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetMetadataAsync_ShouldPassResolvedProjectToInspector()
    {
        var resolver = new Mock<IDatabaseProjectResolver>();
        var inspector = new Mock<IEfCoreModelInspector>();
        var mapper = new EfCoreMetadataMapper();

        var projectPath = Path.Combine(
            Path.GetTempPath(),
            "TestProject.csproj");

        resolver
            .Setup(x => x.ResolveAsync(
                It.IsAny<AnalyzerContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(projectPath);

        inspector
            .Setup(x => x.InspectAsync(
                projectPath,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException());

        var provider = new EfCoreMetadataProvider(
            resolver.Object,
            inspector.Object,
            mapper);

        var context = new AnalyzerContext
        {
            RepositoryPath = Path.GetTempPath(),
            CommitHash = "test"
        };

        await Assert.ThrowsAsync<NotSupportedException>(
            () => provider.GetMetadataAsync(context));

        inspector.Verify(
            x => x.InspectAsync(
                projectPath,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetMetadataAsync_WhenProjectCannotBeResolved_ReturnsEmptyMetadata()
    {
        var resolver = new Mock<IDatabaseProjectResolver>();
        var inspector = new Mock<IEfCoreModelInspector>();
        var mapper = new EfCoreMetadataMapper();

        resolver
            .Setup(x => x.ResolveAsync(
                It.IsAny<AnalyzerContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var provider = new EfCoreMetadataProvider(
            resolver.Object,
            inspector.Object,
            mapper);

        var context = new AnalyzerContext
        {
            RepositoryPath = Path.GetTempPath(),
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

    [Fact]
    public async Task GetMetadataAsync_WhenResolverFails_PropagatesException()
    {
        var resolver = new Mock<IDatabaseProjectResolver>();
        var inspector = new Mock<IEfCoreModelInspector>();
        var mapper = new EfCoreMetadataMapper();

        resolver
            .Setup(x => x.ResolveAsync(
                It.IsAny<AnalyzerContext>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Resolver failed."));

        var provider = new EfCoreMetadataProvider(
            resolver.Object,
            inspector.Object,
            mapper);

        var context = new AnalyzerContext
        {
            RepositoryPath = Path.GetTempPath(),
            CommitHash = "test"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetMetadataAsync(context));

        inspector.Verify(
            x => x.InspectAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetMetadataAsync_ShouldPassCancellationTokenToResolver()
    {
        var resolver = new Mock<IDatabaseProjectResolver>();
        var inspector = new Mock<IEfCoreModelInspector>();
        var mapper = new EfCoreMetadataMapper();

        var cancellationToken = new CancellationToken();

        resolver
            .Setup(x => x.ResolveAsync(
                It.IsAny<AnalyzerContext>(),
                cancellationToken))
            .ReturnsAsync((string?)null);

        var provider = new EfCoreMetadataProvider(
            resolver.Object,
            inspector.Object,
            mapper);

        var context = new AnalyzerContext
        {
            RepositoryPath = Path.GetTempPath(),
            CommitHash = "test"
        };

        await provider.GetMetadataAsync(
            context,
            cancellationToken);

        resolver.Verify(
            x => x.ResolveAsync(
                context,
                cancellationToken),
            Times.Once);
    }
}
