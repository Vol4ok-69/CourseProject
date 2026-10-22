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
            Assert.Equal(nameof(TestOrder.User), foreignKey.NavigationToPrincipal);
            Assert.Equal(nameof(TestUser.Orders), foreignKey.NavigationToDependent);
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

    [Fact]
    public void Map_ShouldMapBidirectionalRelationshipAndNavigations()
    {
        using var context = CreateContext(out var connection);
        using (connection)
        {
            var mapper = new EfCoreMetadataMapper();

            var result = mapper.Map(context.Model);

            var order = Assert.Single(
                result.Entities,
                entity => entity.EntityName == typeof(TestOrder).FullName);

            var relationship = Assert.Single(order.Relationships);

            Assert.Equal("OneToMany", relationship.RelationshipType);
            Assert.Equal(nameof(TestOrder.User), relationship.NavigationToPrincipal);
            Assert.Equal(nameof(TestUser.Orders), relationship.NavigationToDependent);
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

    [Fact]
    public void Map_ShouldMapUnidirectionalRelationshipNavigations()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<UnidirectionalTestDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new UnidirectionalTestDbContext(options);
        var mapper = new EfCoreMetadataMapper();

        var result = mapper.Map(context.Model);

        var order = Assert.Single(
            result.Entities,
            entity => entity.EntityName == typeof(TestOrder).FullName);

        var foreignKey = Assert.Single(order.ForeignKeys);
        Assert.Equal(nameof(TestOrder.User), foreignKey.NavigationToPrincipal);
        Assert.Null(foreignKey.NavigationToDependent);

        var relationship = Assert.Single(order.Relationships);
        Assert.Equal(nameof(TestOrder.User), relationship.NavigationToPrincipal);
        Assert.Null(relationship.NavigationToDependent);
    }

    [Fact]
    public void Map_ShouldMapArbitraryNavigationNameWithoutNameHeuristics()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<CustomNavigationTestDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new CustomNavigationTestDbContext(options);
        var mapper = new EfCoreMetadataMapper();

        var result = mapper.Map(context.Model);

        var post = Assert.Single(
            result.Entities,
            entity => entity.EntityName == typeof(TestPost).FullName);

        var foreignKey = Assert.Single(post.ForeignKeys);
        Assert.Contains("UserId", foreignKey.PropertyNames);
        Assert.Equal(typeof(TestAuthor).FullName, foreignKey.PrincipalEntityName);
        Assert.Equal("Owner", foreignKey.NavigationToPrincipal);
        Assert.Null(foreignKey.NavigationToDependent);

        var relationship = Assert.Single(post.Relationships);
        Assert.Equal("Owner", relationship.NavigationToPrincipal);
        Assert.Null(relationship.NavigationToDependent);
    }

    private sealed class CustomNavigationTestDbContext(DbContextOptions<CustomNavigationTestDbContext> options)
        : DbContext(options)
    {
        public DbSet<TestAuthor> Authors => Set<TestAuthor>();
        public DbSet<TestPost> Posts => Set<TestPost>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestAuthor>(entity =>
            {
                entity.ToTable("Authors");
                entity.HasKey(x => x.Id);
            });

            modelBuilder.Entity<TestPost>(entity =>
            {
                entity.ToTable("Posts");
                entity.HasKey(x => x.Id);
                entity.HasOne(x => x.Owner)
                    .WithMany()
                    .HasForeignKey(x => x.UserId);
            });
        }
    }

    private sealed class TestAuthor
    {
        public int Id { get; set; }
        public string Name { get; set; } = null!;
    }

    private sealed class TestPost
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public TestAuthor Owner { get; set; } = null!;
    }

    private sealed class UnidirectionalTestDbContext(DbContextOptions<UnidirectionalTestDbContext> options)
        : DbContext(options)
    {
        public DbSet<TestUser> Users => Set<TestUser>();
        public DbSet<TestOrder> Orders => Set<TestOrder>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestUser>(entity =>
            {
                entity.ToTable("Users");
                entity.HasKey(x => x.Id);
                entity.Ignore(x => x.Orders);
            });

            modelBuilder.Entity<TestOrder>(entity =>
            {
                entity.ToTable("Orders");
                entity.HasKey(x => x.Id);
                entity.HasOne(x => x.User)
                    .WithMany()
                    .HasForeignKey(x => x.UserId);
            });
        }
    }
}
