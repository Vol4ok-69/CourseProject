using DatabaseAnalyzer.Providers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatabaseAnalyzer.Tests.Providers;

public sealed class EfCoreModelInspectorTests
{
    [Fact]
    public async Task InspectAsync_ShouldReturnEfCoreModel()
    {
        var projectPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "Fixtures",
                "SampleEfProject",
                "SampleEfProject.csproj"));

        var resolution = new DatabaseProjectResolution(
            projectPath,
            projectPath);

        var inspector = new EfCoreModelInspector();

        var model = await inspector.InspectAsync(resolution);

        Assert.NotNull(model);

        var user = model.FindEntityType(
            "DatabaseAnalyzer.Tests.Fixtures.SampleEfProject.User");

        var order = model.FindEntityType(
            "DatabaseAnalyzer.Tests.Fixtures.SampleEfProject.Order");

        Assert.NotNull(user);
        Assert.NotNull(order);

        Assert.Equal("Users", user.GetTableName());
        Assert.Equal("Orders", order.GetTableName());

        var foreignKey = Assert.Single(order.GetForeignKeys());

        Assert.Equal(
            "DatabaseAnalyzer.Tests.Fixtures.SampleEfProject.User",
            foreignKey.PrincipalEntityType.Name);

        Assert.Equal(
            "UserId",
            foreignKey.Properties.Single().Name);
    }
}
