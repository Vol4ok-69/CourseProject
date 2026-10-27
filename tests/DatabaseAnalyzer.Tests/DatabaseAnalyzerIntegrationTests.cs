using Application.Analyzers;
using DatabaseAnalyzer.Mapping;
using DatabaseAnalyzer.Providers;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Moq;
using Xunit;

namespace DatabaseAnalyzer.Tests;

public sealed class DatabaseAnalyzerIntegrationTests
{
    [Fact]
    public async Task FullPipeline_WithSampleEfProject_BuildsInspectsMapsAndAnalyzes()
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

        var resolution = new DatabaseProjectResolution(projectPath, projectPath);

        var resolver = new Mock<IDatabaseProjectResolver>();
        resolver
            .Setup(x => x.ResolveAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolution);

        var inspector = new EfCoreModelInspector();
        var mapper = new EfCoreMetadataMapper();
        var provider = new EfCoreMetadataProvider(resolver.Object, inspector, mapper);
        var analyzer = new DatabaseAnalyzer(provider);

        var context = new AnalyzerContext
        {
            RepositoryPath = Path.GetDirectoryName(projectPath)!,
            CommitHash = "test-commit"
        };

        // 1. Проверяем корректность извлечения метаданных реального проекта через EfCoreMetadataProvider
        var metadata = await provider.GetMetadataAsync(context);
        Assert.NotNull(metadata);
        Assert.Equal(2, metadata.Entities.Count);

        var userEntity = Assert.Single(metadata.Entities, e => e.TableName == "Users");
        var orderEntity = Assert.Single(metadata.Entities, e => e.TableName == "Orders");

        Assert.Single(userEntity.Keys, k => k.IsPrimaryKey);
        Assert.Single(orderEntity.Keys, k => k.IsPrimaryKey);
        Assert.Single(orderEntity.ForeignKeys);

        // 2. SampleEfProject является корректно настроенным проектом: все сущности имеют PK, навигации заданы,
        // а EF Core по конвенции создал индекс для внешнего ключа. Нарушений быть не должно.
        var findings = await analyzer.AnalyzeAsync(context);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task FullPipeline_DB01_KeylessEntity_ProducesDB01Finding()
    {
        var options = new DbContextOptionsBuilder<Db01Context>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        using var db = new Db01Context(options);
        var provider = CreateProvider(db.Model);
        var analyzer = new DatabaseAnalyzer(provider);

        var context = new AnalyzerContext
        {
            RepositoryPath = Path.GetTempPath(),
            CommitHash = "test"
        };

        var findings = await analyzer.AnalyzeAsync(context);

        var finding = Assert.Single(findings);
        Assert.Equal("DB01", finding.Rule);
        Assert.Equal("KeylessEntities", finding.FilePath);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("не имеет первичного ключа", finding.Message);
    }

    [Fact]
    public async Task FullPipeline_DB02_ForeignKeyWithoutNavigationToPrincipal_ProducesDB02Finding()
    {
        var options = new DbContextOptionsBuilder<Db02Context>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        using var db = new Db02Context(options);
        var provider = CreateProvider(db.Model);
        var analyzer = new DatabaseAnalyzer(provider);

        var context = new AnalyzerContext
        {
            RepositoryPath = Path.GetTempPath(),
            CommitHash = "test"
        };

        var findings = await analyzer.AnalyzeAsync(context);

        var finding = Assert.Single(findings);
        Assert.Equal("DB02", finding.Rule);
        Assert.Equal("Employees", finding.FilePath);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("не имеет навигационного свойства к главной сущности", finding.Message);
    }

    [Fact]
    public async Task FullPipeline_DB03_ImplicitManyToMany_ExtractsSkipNavigationsViaRealProviderAndProducesDB03()
    {
        var options = new DbContextOptionsBuilder<Db03Context>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        using var db = new Db03Context(options);
        var provider = CreateProvider(db.Model);

        var context = new AnalyzerContext
        {
            RepositoryPath = Path.GetTempPath(),
            CommitHash = "test"
        };

        // 1. Проверяем корректность извлечения метаданных реальным EfCoreMetadataProvider
        var metadata = await provider.GetMetadataAsync(context);

        var studentEntity = Assert.Single(metadata.Entities, e => e.TableName == "Students");
        var courseEntity = Assert.Single(metadata.Entities, e => e.TableName == "Courses");

        var studentSkipNav = Assert.Single(studentEntity.SkipNavigations);
        Assert.Equal(nameof(Student.Courses), studentSkipNav.NavigationName);
        Assert.Equal(typeof(Course).FullName, studentSkipNav.TargetEntityName);
        Assert.True(studentSkipNav.IsImplicitJoinEntity);
        Assert.Equal(nameof(Course.Students), studentSkipNav.InverseNavigationName);

        var courseSkipNav = Assert.Single(courseEntity.SkipNavigations);
        Assert.Equal(nameof(Course.Students), courseSkipNav.NavigationName);
        Assert.Equal(typeof(Student).FullName, courseSkipNav.TargetEntityName);
        Assert.True(courseSkipNav.IsImplicitJoinEntity);
        Assert.Equal(nameof(Student.Courses), courseSkipNav.InverseNavigationName);

        // 2. Проверяем формирование единственного finding DB03 анализатором базы данных
        var analyzer = new DatabaseAnalyzer(provider);
        var findings = await analyzer.AnalyzeAsync(context);

        var db03Findings = findings.Where(f => f.Rule == "DB03").ToList();
        var finding = Assert.Single(db03Findings);

        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("связь многие-ко-многим", finding.Message);
        Assert.Contains("CourseStudent", finding.Message);
    }

    [Fact]
    public async Task FullPipeline_DB04_ForeignKeyWithoutIndex_ProducesDB04Finding()
    {
        var options = new DbContextOptionsBuilder<Db04Context>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        using var db = new Db04Context(options);
        var provider = CreateProvider(db.Model);
        var analyzer = new DatabaseAnalyzer(provider);

        var context = new AnalyzerContext
        {
            RepositoryPath = Path.GetTempPath(),
            CommitHash = "test"
        };

        var findings = await analyzer.AnalyzeAsync(context);

        var finding = Assert.Single(findings);
        Assert.Equal("DB04", finding.Rule);
        Assert.Equal("Books", finding.FilePath);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("не имеет подходящего индекса", finding.Message);
    }

    [Fact]
    public async Task FullPipeline_DB05_DuplicateIndexes_ProducesDB05Finding()
    {
        var options = new DbContextOptionsBuilder<Db05Context>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        using var db = new Db05Context(options);
        var provider = CreateProvider(db.Model);
        var analyzer = new DatabaseAnalyzer(provider);

        var context = new AnalyzerContext
        {
            RepositoryPath = Path.GetTempPath(),
            CommitHash = "test"
        };

        var findings = await analyzer.AnalyzeAsync(context);

        var finding = Assert.Single(findings);
        Assert.Equal("DB05", finding.Rule);
        Assert.Equal("Products", finding.FilePath);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("дублируются по столбцам", finding.Message);
    }

    [Fact]
    public async Task FullPipeline_FullyCompliantModel_ProducesNoFindings()
    {
        var options = new DbContextOptionsBuilder<CompliantContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        using var db = new CompliantContext(options);
        var provider = CreateProvider(db.Model);
        var analyzer = new DatabaseAnalyzer(provider);

        var context = new AnalyzerContext
        {
            RepositoryPath = Path.GetTempPath(),
            CommitHash = "test"
        };

        var findings = await analyzer.AnalyzeAsync(context);

        Assert.Empty(findings);
    }

    private static EfCoreMetadataProvider CreateProvider(IModel model)
    {
        var resolver = new Mock<IDatabaseProjectResolver>();
        var inspector = new Mock<IEfCoreModelInspector>();

        var resolution = new DatabaseProjectResolution("TestContext.csproj", "TestStartup.csproj");

        resolver
            .Setup(x => x.ResolveAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolution);

        inspector
            .Setup(x => x.InspectAsync(resolution, It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);

        return new EfCoreMetadataProvider(
            resolver.Object,
            inspector.Object,
            new EfCoreMetadataMapper());
    }

    private sealed class KeylessEntity
    {
        public string Summary { get; set; } = null!;
    }

    private sealed class Db01Context(DbContextOptions<Db01Context> options) : DbContext(options)
    {
        public DbSet<KeylessEntity> KeylessEntities => Set<KeylessEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<KeylessEntity>(entity =>
            {
                entity.ToTable("KeylessEntities");
                entity.HasNoKey();
                entity.Property(x => x.Summary).HasMaxLength(200);
            });
        }
    }

    private sealed class Department
    {
        public int Id { get; set; }
        public string Name { get; set; } = null!;
    }

    private sealed class Employee
    {
        public int Id { get; set; }
        public int DepartmentId { get; set; }
    }

    private sealed class Db02Context(DbContextOptions<Db02Context> options) : DbContext(options)
    {
        public DbSet<Department> Departments => Set<Department>();
        public DbSet<Employee> Employees => Set<Employee>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Department>(entity =>
            {
                entity.ToTable("Departments");
                entity.HasKey(x => x.Id);
            });

            modelBuilder.Entity<Employee>(entity =>
            {
                entity.ToTable("Employees");
                entity.HasKey(x => x.Id);
                entity.HasOne<Department>()
                    .WithMany()
                    .HasForeignKey(x => x.DepartmentId);
                entity.HasIndex(x => x.DepartmentId);
            });
        }
    }

    private sealed class Student
    {
        public int Id { get; set; }
        public string Name { get; set; } = null!;
        public ICollection<Course> Courses { get; set; } = [];
    }

    private sealed class Course
    {
        public int Id { get; set; }
        public string Title { get; set; } = null!;
        public ICollection<Student> Students { get; set; } = [];
    }

    private sealed class Db03Context(DbContextOptions<Db03Context> options) : DbContext(options)
    {
        public DbSet<Student> Students => Set<Student>();
        public DbSet<Course> Courses => Set<Course>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Student>(entity =>
            {
                entity.ToTable("Students");
                entity.HasKey(x => x.Id);
                entity.HasMany(x => x.Courses)
                    .WithMany(x => x.Students);
            });

            modelBuilder.Entity<Course>(entity =>
            {
                entity.ToTable("Courses");
                entity.HasKey(x => x.Id);
            });
        }
    }

    private sealed class Author
    {
        public int Id { get; set; }
        public string Name { get; set; } = null!;
        public ICollection<Book> Books { get; set; } = [];
    }

    private sealed class Book
    {
        public int Id { get; set; }
        public int AuthorId { get; set; }
        public Author Author { get; set; } = null!;
    }

    private sealed class Db04Context(DbContextOptions<Db04Context> options) : DbContext(options)
    {
        public DbSet<Author> Authors => Set<Author>();
        public DbSet<Book> Books => Set<Book>();

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            // Отключаем автоматическое создание индексов для внешних ключей конвенцией EF Core,
            // чтобы смоделировать случай отсутствия индекса для правила DB04.
            configurationBuilder.Conventions.Remove(typeof(ForeignKeyIndexConvention));
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Author>(entity =>
            {
                entity.ToTable("Authors");
                entity.HasKey(x => x.Id);
            });

            modelBuilder.Entity<Book>(entity =>
            {
                entity.ToTable("Books");
                entity.HasKey(x => x.Id);
                entity.HasOne(x => x.Author)
                    .WithMany(x => x.Books)
                    .HasForeignKey(x => x.AuthorId);
            });
        }
    }

    private sealed class Product
    {
        public int Id { get; set; }
        public string Sku { get; set; } = null!;
    }

    private sealed class Db05Context(DbContextOptions<Db05Context> options) : DbContext(options)
    {
        public DbSet<Product> Products => Set<Product>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var productBuilder = modelBuilder.Entity<Product>();
            productBuilder.ToTable("Products");
            productBuilder.HasKey(x => x.Id);
            productBuilder.Property(x => x.Sku).HasMaxLength(50).IsRequired();

            // Настраиваем два явных индекса на один и тот же столбец через метаданные сущности
            var skuProperty = productBuilder.Metadata.FindProperty(nameof(Product.Sku))!;
            productBuilder.Metadata.AddIndex([skuProperty], "IX_Products_Sku_1");
            productBuilder.Metadata.AddIndex([skuProperty], "IX_Products_Sku_2");
        }
    }

    private sealed class CompliantContext(DbContextOptions<CompliantContext> options) : DbContext(options)
    {
        public DbSet<Author> Authors => Set<Author>();
        public DbSet<Book> Books => Set<Book>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Author>(entity =>
            {
                entity.ToTable("Authors");
                entity.HasKey(x => x.Id);
            });

            modelBuilder.Entity<Book>(entity =>
            {
                entity.ToTable("Books");
                entity.HasKey(x => x.Id);
                entity.HasOne(x => x.Author)
                    .WithMany(x => x.Books)
                    .HasForeignKey(x => x.AuthorId);
                entity.HasIndex(x => x.AuthorId);
            });
        }
    }
}
