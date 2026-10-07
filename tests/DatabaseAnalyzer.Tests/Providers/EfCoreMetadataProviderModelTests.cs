using DatabaseAnalyzer.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace DatabaseAnalyzer.Tests.Providers;

public sealed class EfCoreMetadataProviderModelTests
{
    [Fact]
    public void TestDbContext_ContainsExpectedEfModel()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new TestDbContext(options);

        var model = db.Model;

        var user = model.FindEntityType(typeof(TestUser));
        var order = model.FindEntityType(typeof(TestOrder));

        Assert.NotNull(user);
        Assert.NotNull(order);

        Assert.Equal("Users", user.GetTableName());
        Assert.Equal("Orders", order.GetTableName());

        Assert.NotNull(user.FindPrimaryKey());
        Assert.NotNull(order.FindPrimaryKey());

        var foreignKey = order.GetForeignKeys().Single();

        Assert.Equal(typeof(TestUser), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
        Assert.Single(foreignKey.Properties);
        Assert.Equal(nameof(TestOrder.UserId), foreignKey.Properties.Single().Name);
    }
}
