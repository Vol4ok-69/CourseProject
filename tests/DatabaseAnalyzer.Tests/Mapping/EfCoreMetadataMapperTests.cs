using DatabaseAnalyzer.Mapping;
using DatabaseAnalyzer.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatabaseAnalyzer.Tests.Mapping;

public sealed class EfCoreMetadataMapperTests
{
    [Fact]
    public void Map_ShouldMapEntities()
    {
        using var context = CreateContext(out var connection);
        using (connection)
        {
            var mapper = new EfCoreMetadataMapper();

            var result = mapper.Map(context.Model);

            Assert.NotNull(result);
            Assert.NotEmpty(result.Entities);

            var user = Assert.Single(
                result.Entities,
                entity => entity.EntityName == typeof(TestUser).FullName);

            Assert.Equal("Users", user.TableName);
        }
    }

    [Fact]
    public void Map_ShouldMapPropertiesAndPrimaryKey()
    {
        using var context = CreateContext(out var connection);
        using (connection)
        {
            var mapper = new EfCoreMetadataMapper();

            var result = mapper.Map(context.Model);

            var user = Assert.Single(
                result.Entities,
                entity => entity.EntityName == typeof(TestUser).FullName);

            var id = Assert.Single(
                user.Properties,
                property => property.PropertyName == "Id");

            Assert.Equal("Id", id.ColumnName);
            Assert.True(id.IsPrimaryKey);
        }
    }

    [Fact]
    public void Map_ShouldMapForeignKeys()
    {
        using var context = CreateContext(out var connection);
        using (connection)
        {
            var mapper = new EfCoreMetadataMapper();

            var result = mapper.Map(context.Model);

            var order = Assert.Single(
                result.Entities,
                entity => entity.EntityName == typeof(TestOrder).FullName);

            var foreignKey = Assert.Single(order.ForeignKeys);

            Assert.Equal(typeof(TestUser).FullName, foreignKey.PrincipalEntityName);
        }
    }

    [Fact]
    public void Map_ShouldMapIndexes()
    {
        using var context = CreateContext(out var connection);
        using (connection)
        {
            var mapper = new EfCoreMetadataMapper();

            var result = mapper.Map(context.Model);

            var order = Assert.Single(
                result.Entities,
                entity => entity.EntityName == typeof(TestOrder).FullName);

            var index = Assert.Single(
                order.Indexes,
                index => index.PropertyNames.Contains("UserId"));

            Assert.Equal("IX_Orders_UserId", index.Name);
            Assert.False(index.IsUnique);
        }
    }

    private static TestDbContext CreateContext(out Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(connection)
            .Options;

        return new TestDbContext(options);
    }
}