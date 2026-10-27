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


    [Fact]
    public void Map_ShouldMarkConventionManyToManyAsImplicit()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<ImplicitManyToManyDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new ImplicitManyToManyDbContext(options);
        var mapper = new EfCoreMetadataMapper();

        var result = mapper.Map(context.Model);

        var developer = Assert.Single(
            result.Entities,
            entity => entity.EntityName == typeof(TestDeveloper).FullName);

        var skip = Assert.Single(developer.SkipNavigations);
        Assert.Equal(nameof(TestDeveloper.Skills), skip.NavigationName);
        Assert.True(skip.IsImplicitJoinEntity);
    }

    [Fact]
    public void Map_ShouldNotMarkExplicitClassJoinEntityAsImplicit()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<ExplicitClassJoinDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new ExplicitClassJoinDbContext(options);
        var mapper = new EfCoreMetadataMapper();

        var result = mapper.Map(context.Model);

        var developer = Assert.Single(
            result.Entities,
            entity => entity.EntityName == typeof(TestDeveloper).FullName);

        var skip = Assert.Single(developer.SkipNavigations);
        Assert.Equal(nameof(TestDeveloper.Skills), skip.NavigationName);
        Assert.False(skip.IsImplicitJoinEntity);
    }

    [Fact]
    public void Map_ShouldMarkDictionaryJoinEntityAsImplicitRegardlessOfName()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<ExplicitDictionaryJoinDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new ExplicitDictionaryJoinDbContext(options);
        var mapper = new EfCoreMetadataMapper();

        var result = mapper.Map(context.Model);

        var developer = Assert.Single(
            result.Entities,
            entity => entity.EntityName == typeof(TestDeveloper).FullName);

        var skip = Assert.Single(developer.SkipNavigations);
        Assert.Equal(nameof(TestDeveloper.Skills), skip.NavigationName);
        Assert.True(skip.IsImplicitJoinEntity);
    }

    [Fact]
    public void Map_ShouldMapUnidirectionalManyToManyWithoutInverseNavigation()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<UnidirectionalManyToManyDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new UnidirectionalManyToManyDbContext(options);
        var mapper = new EfCoreMetadataMapper();

        var result = mapper.Map(context.Model);

        var article = Assert.Single(
            result.Entities,
            entity => entity.EntityName == typeof(TestArticle).FullName);

        var tag = Assert.Single(
            result.Entities,
            entity => entity.EntityName == typeof(TestTag).FullName);

        var skip = Assert.Single(article.SkipNavigations);
        Assert.Equal(nameof(TestArticle.Tags), skip.NavigationName);
        Assert.True(skip.IsImplicitJoinEntity);
        Assert.Null(skip.InverseNavigationName);
        Assert.Empty(tag.SkipNavigations);
    }

    [Fact]
    public void Map_ExplicitShadowInverseSkipNavigationMatchingTargetEntityShortName_TreatedAsUnidirectional()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<ExplicitNamedShadowInverseDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new ExplicitNamedShadowInverseDbContext(options);
        var mapper = new EfCoreMetadataMapper();

        var result = mapper.Map(context.Model);

        var article = Assert.Single(
            result.Entities,
            entity => entity.EntityName == typeof(TestArticle).FullName);

        var tag = Assert.Single(
            result.Entities,
            entity => entity.EntityName == typeof(TestTag).FullName);

        var skip = Assert.Single(article.SkipNavigations);
        Assert.Equal(nameof(TestArticle.Tags), skip.NavigationName);

        // В EF Core вызов .WithMany("TestArticle") и .WithMany() генерируют идентичную RuntimeSkipNavigation
        // с IsShadowProperty() = true и Name = ShortName(). Метаданные IModel времени выполнения не содержат признака,
        // позволяющего отличить ручной вызов .WithMany("Target") от конвенционального .WithMany().
        // Маппер считает такую связь однонаправленной (InverseNavigationName = null), а синтетическую теневую навигацию на Tag не включает.
        Assert.Null(skip.InverseNavigationName);
        Assert.Empty(tag.SkipNavigations);
    }

    [Fact]
    public void Map_ExplicitCustomShadowInverseSkipNavigation_IsPreserved()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<ExplicitCustomShadowInverseDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new ExplicitCustomShadowInverseDbContext(options);
        var mapper = new EfCoreMetadataMapper();

        var result = mapper.Map(context.Model);

        var article = Assert.Single(
            result.Entities,
            entity => entity.EntityName == typeof(TestArticle).FullName);

        var tag = Assert.Single(
            result.Entities,
            entity => entity.EntityName == typeof(TestTag).FullName);

        var skip = Assert.Single(article.SkipNavigations);
        Assert.Equal("CustomArticles", skip.InverseNavigationName);

        var tagSkip = Assert.Single(tag.SkipNavigations);
        Assert.Equal("CustomArticles", tagSkip.NavigationName);
    }

    [Fact]
    public void Map_ShouldMapMultipleManyToManyRelationshipsBetweenSameEntities()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<MultipleManyToManyDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new MultipleManyToManyDbContext(options);
        var mapper = new EfCoreMetadataMapper();

        var result = mapper.Map(context.Model);

        var person = Assert.Single(
            result.Entities,
            entity => entity.EntityName == typeof(TestPerson).FullName);

        Assert.Equal(4, person.SkipNavigations.Count);
        Assert.All(person.SkipNavigations, s => Assert.True(s.IsImplicitJoinEntity));
        Assert.Contains(person.SkipNavigations, s => s.NavigationName == nameof(TestPerson.Friends));
        Assert.Contains(person.SkipNavigations, s => s.NavigationName == nameof(TestPerson.FriendOf));
        Assert.Contains(person.SkipNavigations, s => s.NavigationName == nameof(TestPerson.Colleagues));
        Assert.Contains(person.SkipNavigations, s => s.NavigationName == nameof(TestPerson.ColleagueOf));
    }

    private sealed class ImplicitManyToManyDbContext(DbContextOptions<ImplicitManyToManyDbContext> options)
        : DbContext(options)
    {
        public DbSet<TestDeveloper> Developers => Set<TestDeveloper>();
        public DbSet<TestSkill> Skills => Set<TestSkill>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestDeveloper>()
                .ToTable("Developers")
                .HasMany(x => x.Skills)
                .WithMany(x => x.Developers);

            modelBuilder.Entity<TestSkill>().ToTable("Skills");
        }
    }

    private sealed class ExplicitClassJoinDbContext(DbContextOptions<ExplicitClassJoinDbContext> options)
        : DbContext(options)
    {
        public DbSet<TestDeveloper> Developers => Set<TestDeveloper>();
        public DbSet<TestSkill> Skills => Set<TestSkill>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestDeveloper>()
                .ToTable("Developers")
                .HasMany(x => x.Skills)
                .WithMany(x => x.Developers)
                .UsingEntity<TestDeveloperSkillJoinEntity>(
                    l => l.HasOne(x => x.Skill).WithMany().HasForeignKey(x => x.SkillId),
                    r => r.HasOne(x => x.Developer).WithMany().HasForeignKey(x => x.DeveloperId));

            modelBuilder.Entity<TestSkill>().ToTable("Skills");
        }
    }

    private sealed class TestDeveloperSkillJoinEntity
    {
        public int DeveloperId { get; set; }
        public TestDeveloper Developer { get; set; } = null!;
        public int SkillId { get; set; }
        public TestSkill Skill { get; set; } = null!;
    }

    private sealed class ExplicitDictionaryJoinDbContext(DbContextOptions<ExplicitDictionaryJoinDbContext> options)
        : DbContext(options)
    {
        public DbSet<TestDeveloper> Developers => Set<TestDeveloper>();
        public DbSet<TestSkill> Skills => Set<TestSkill>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.SharedTypeEntity<Dictionary<string, object>>("CustomDeveloperSkills", b =>
            {
                b.IndexerProperty<int>("DevelopersId");
                b.IndexerProperty<int>("SkillsId");
                b.HasKey("DevelopersId", "SkillsId");
            });

            modelBuilder.Entity<TestDeveloper>()
                .ToTable("Developers")
                .HasMany(x => x.Skills)
                .WithMany(x => x.Developers)
                .UsingEntity<Dictionary<string, object>>(
                    "CustomDeveloperSkills",
                    l => l.HasOne<TestSkill>().WithMany().HasForeignKey("SkillsId"),
                    r => r.HasOne<TestDeveloper>().WithMany().HasForeignKey("DevelopersId"));

            modelBuilder.Entity<TestSkill>().ToTable("Skills");
        }
    }

    private sealed class UnidirectionalManyToManyDbContext(DbContextOptions<UnidirectionalManyToManyDbContext> options)
        : DbContext(options)
    {
        public DbSet<TestArticle> Articles => Set<TestArticle>();
        public DbSet<TestTag> Tags => Set<TestTag>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestArticle>()
                .ToTable("Articles")
                .HasMany(x => x.Tags)
                .WithMany();

            modelBuilder.Entity<TestTag>().ToTable("Tags");
        }
    }

    private sealed class ExplicitNamedShadowInverseDbContext(DbContextOptions<ExplicitNamedShadowInverseDbContext> options)
        : DbContext(options)
    {
        public DbSet<TestArticle> Articles => Set<TestArticle>();
        public DbSet<TestTag> Tags => Set<TestTag>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestArticle>()
                .ToTable("Articles")
                .HasMany(x => x.Tags)
                .WithMany(nameof(TestArticle));

            modelBuilder.Entity<TestTag>().ToTable("Tags");
        }
    }

    private sealed class ExplicitCustomShadowInverseDbContext(DbContextOptions<ExplicitCustomShadowInverseDbContext> options)
        : DbContext(options)
    {
        public DbSet<TestArticle> Articles => Set<TestArticle>();
        public DbSet<TestTag> Tags => Set<TestTag>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestArticle>()
                .ToTable("Articles")
                .HasMany(x => x.Tags)
                .WithMany("CustomArticles");

            modelBuilder.Entity<TestTag>().ToTable("Tags");
        }
    }

    private sealed class MultipleManyToManyDbContext(DbContextOptions<MultipleManyToManyDbContext> options)
        : DbContext(options)
    {
        public DbSet<TestPerson> Persons => Set<TestPerson>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestPerson>(entity =>
            {
                entity.ToTable("Persons");
                entity.HasMany(x => x.Friends).WithMany(x => x.FriendOf);
                entity.HasMany(x => x.Colleagues).WithMany(x => x.ColleagueOf);
            });
        }
    }

    private sealed class TestDeveloper
    {
        public int Id { get; set; }
        public ICollection<TestSkill> Skills { get; set; } = [];
    }

    private sealed class TestSkill
    {
        public int Id { get; set; }
        public ICollection<TestDeveloper> Developers { get; set; } = [];
    }

    private sealed class TestArticle
    {
        public int Id { get; set; }
        public ICollection<TestTag> Tags { get; set; } = [];
    }

    private sealed class TestTag
    {
        public int Id { get; set; }
    }

    private sealed class TestPerson
    {
        public int Id { get; set; }
        public ICollection<TestPerson> Friends { get; set; } = [];
        public ICollection<TestPerson> FriendOf { get; set; } = [];
        public ICollection<TestPerson> Colleagues { get; set; } = [];
        public ICollection<TestPerson> ColleagueOf { get; set; } = [];
    }
}
