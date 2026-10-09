using Application.Analyzers;
using DatabaseAnalyzer;
using DatabaseAnalyzer.Metadata;
using DatabaseAnalyzer.Providers;
using Domain.Enums;
using Moq;

namespace DatabaseAnalyzer.Tests;

public sealed class DatabaseAnalyzerTests
{
    [Fact]
    public async Task Type_ShouldReturnDatabase()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();
        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);

        Assert.Equal(AnalyzerType.Database, analyzer.Type);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldCallMetadataProvider()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();

        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(
                It.IsAny<AnalyzerContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata());

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);

        var context = new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        };

        var result = await analyzer.AnalyzeAsync(context);

        metadataProvider.Verify(
            provider => provider.GetMetadataAsync(
                context,
                It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.Empty(result);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldPropagateMetadataProviderException()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();

        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(
                It.IsAny<AnalyzerContext>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test error"));

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);

        var context = new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => analyzer.AnalyzeAsync(context));

        Assert.Equal("Test error", exception.Message);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldReportEntityWithoutPrimaryKey()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();

        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(
                It.IsAny<AnalyzerContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                {
                    EntityName = "ReportRow",
                    TableName = "ReportRows",
                    Keys = []
                }
                ]
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);

        var context = new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        };

        var findings = await analyzer.AnalyzeAsync(context);

        var finding = Assert.Single(findings);
        Assert.Equal("DB01", finding.Rule);
        Assert.Equal("ReportRows", finding.FilePath);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("ReportRow", finding.Message);
        Assert.False(string.IsNullOrWhiteSpace(finding.Recommendation));
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldNotReportEntityWithPrimaryKey()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();

        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(
                It.IsAny<AnalyzerContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                {
                    EntityName = "User",
                    TableName = "Users",
                    Keys =
                    [
                        new DatabaseKeyMetadata
                        {
                            Name = "PK_Users",
                            PropertyNames = ["UserId"],
                            IsPrimaryKey = true
                        }
                    ]
                }
                ]
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);

        var context = new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        };

        var findings = await analyzer.AnalyzeAsync(context);

        Assert.Empty(findings);
    }
}
