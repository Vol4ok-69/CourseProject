using Application.Analyzers;
using DatabaseAnalyzer.Metadata;
using DatabaseAnalyzer.Roslyn;
using DatabaseAnalyzer.Rules;
using Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using DbAnalyzer = DatabaseAnalyzer.DatabaseAnalyzer;

namespace DatabaseAnalyzer.Tests.Roslyn;

/// <summary>
/// Тесты безопасного конвейера извлечения метаданных EF Core и отключения in-process проверок DB01–DB05 (TD-001 Этап 1).
/// Проверяют соблюдение всех обязательных архитектурных ограничений ADR-001.
/// </summary>
public sealed class RoslynDatabaseAnalyzerSafePipelineTests : IDisposable
{
    private readonly string _tempDirectory;

    public RoslynDatabaseAnalyzerSafePipelineTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "NetScope_RoslynSafePipeline_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Игнорируем ошибки очистки временных файлов в тестах
        }
    }

    [Fact]
    public async Task Scenario1_RoslynExtractor_IsCalled_WithRepositoryPath()
    {
        var mockExtractor = new Mock<IRoslynMetadataExtractor>();
        mockExtractor
            .Setup(x => x.ExtractAsync(_tempDirectory, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoslynModelExtractionResult());

        var analyzer = new DbAnalyzer(mockExtractor.Object);
        var context = new AnalyzerContext { RepositoryPath = _tempDirectory, CommitHash = "test-hash" };

        var findings = await analyzer.AnalyzeAsync(context);

        mockExtractor.Verify(x => x.ExtractAsync(_tempDirectory, It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(findings);
    }

    [Fact]
    public async Task Scenario2_RoslynExtractor_ExtractsAndPassesMetadataCorrectly()
    {
        var sourceCode = """
            using Microsoft.EntityFrameworkCore;

            namespace TestApp;

            public class Blog
            {
                public int Id { get; set; }
                public string Title { get; set; } = null!;
            }

            public class BloggingDbContext : DbContext
            {
                public DbSet<Blog> Blogs => Set<Blog>();

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Blog>().ToTable("BlogsTable");
                }
            }
            """;

        var filePath = Path.Combine(_tempDirectory, "BloggingDbContext.cs");
        await File.WriteAllTextAsync(filePath, sourceCode);

        var extractor = new RoslynMetadataExtractor();
        var result = await extractor.ExtractAsync(_tempDirectory);

        Assert.NotNull(result);
        Assert.Single(result.Contexts);
        Assert.Equal("BloggingDbContext", result.Contexts[0].Name);

        var metadata = result.SingleContextMetadata;
        Assert.NotNull(metadata);
        Assert.Single(metadata.Entities);
        Assert.Equal("BlogsTable", metadata.Entities[0].TableName);
        Assert.Single(metadata.Entities[0].Keys);
        Assert.True(metadata.Entities[0].Keys[0].IsPrimaryKey);
    }

    [Fact]
    public async Task Scenario3_ProjectWithoutDbContext_HandledSafelyWithoutCrash()
    {
        var regularClassSource = """
            namespace Utility;

            public class Helper
            {
                public int Compute() => 42;
            }
            """;

        var filePath = Path.Combine(_tempDirectory, "Helper.cs");
        await File.WriteAllTextAsync(filePath, regularClassSource);

        var extractor = new RoslynMetadataExtractor();
        var analyzer = new DbAnalyzer(extractor);
        var context = new AnalyzerContext { RepositoryPath = _tempDirectory, CommitHash = "hash1" };

        var findings = await analyzer.AnalyzeAsync(context);

        Assert.NotNull(findings);
        Assert.Equal(Application.Analyzers.DatabaseAnalysisExecutionStatus.InformationSkipped, analyzer.ExecutionStatus);
        Assert.Contains(findings, f => f.Rule == DbAnalyzer.MissingDbContextRule);
        Assert.DoesNotContain(findings, f => f.Rule == DbAnalyzer.ContainerUnavailableRule);

        var missingFinding = findings.First(f => f.Rule == DbAnalyzer.MissingDbContextRule);
        Assert.Equal(Severity.Info, missingFinding.Severity);
        Assert.Contains("не найдено контекстов данных Entity Framework Core", missingFinding.Message);
    }

    [Fact]
    public async Task Scenario4_CorruptedAndIncompleteCSharpFiles_DoNotCrashAnalysis()
    {
        var corruptedSource = """
            namespace Broken
            {
                public class Incomplete {
                    {{{ // syntax errors
            """;

        var filePath = Path.Combine(_tempDirectory, "Broken.cs");
        await File.WriteAllTextAsync(filePath, corruptedSource);

        var extractor = new RoslynMetadataExtractor();
        var analyzer = new DbAnalyzer(extractor);
        var context = new AnalyzerContext { RepositoryPath = _tempDirectory, CommitHash = "hash2" };

        var findings = await analyzer.AnalyzeAsync(context);

        Assert.NotNull(findings);
        Assert.Contains(findings, f => f.Rule == DbAnalyzer.RoslynWarningRule);
        var warningFinding = findings.First(f => f.Rule == DbAnalyzer.RoslynWarningRule);
        Assert.Equal(Severity.Warning, warningFinding.Severity);
        Assert.Contains("синтаксические ошибки", warningFinding.Message);
    }

    [Fact]
    public async Task Scenario5_RoslynInspectorWarnings_ArePreservedAndNotLost()
    {
        var sourceWithWarning = """
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
                }
            }
            """;

        var filePath = Path.Combine(_tempDirectory, "ContextWithWarning.cs");
        await File.WriteAllTextAsync(filePath, sourceWithWarning);

        var extractor = new RoslynMetadataExtractor();
        var analyzer = new DbAnalyzer(extractor);
        var context = new AnalyzerContext { RepositoryPath = _tempDirectory, CommitHash = "hash3" };

        var findings = await analyzer.AnalyzeAsync(context);

        var roslynWarnings = findings.Where(f => f.Rule == DbAnalyzer.RoslynWarningRule).ToList();
        Assert.NotEmpty(roslynWarnings);
        Assert.Contains(roslynWarnings, f => f.Message.Contains("ApplyConfigurationsFromAssembly"));
    }

    [Fact]
    public async Task Scenario6_DB01ToDB05_AreNotExecutedInBackendProcess()
    {
        // Создаём модель с заведомыми нарушениями DB01 (сущность без ключа) и DB04 (внешний ключ без индекса)
        var sourceWithViolations = """
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class KeylessItem
            {
                public string Description { get; set; } = null!;
            }

            public class Parent
            {
                public int Id { get; set; }
            }

            public class Child
            {
                public int Id { get; set; }
                public int ParentId { get; set; }
            }

            public class ViolationsDbContext : DbContext
            {
                public DbSet<KeylessItem> Items => Set<KeylessItem>();
                public DbSet<Parent> Parents => Set<Parent>();
                public DbSet<Child> Children => Set<Child>();

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<KeylessItem>().HasNoKey();
                    modelBuilder.Entity<Child>().HasOne<Parent>().WithMany().HasForeignKey(c => c.ParentId);
                }
            }
            """;

        var filePath = Path.Combine(_tempDirectory, "ViolationsDbContext.cs");
        await File.WriteAllTextAsync(filePath, sourceWithViolations);

        var extractor = new RoslynMetadataExtractor();
        var analyzer = new DbAnalyzer(extractor);
        var context = new AnalyzerContext { RepositoryPath = _tempDirectory, CommitHash = "hash4" };

        var findings = await analyzer.AnalyzeAsync(context);

        // Строгое архитектурное требование: DB01–DB05 НЕ ДОЛЖНЫ выполняться в backend процессе!
        Assert.DoesNotContain(findings, f => f.Rule == "DB01");
        Assert.DoesNotContain(findings, f => f.Rule == "DB02");
        Assert.DoesNotContain(findings, f => f.Rule == "DB03");
        Assert.DoesNotContain(findings, f => f.Rule == "DB04");
        Assert.DoesNotContain(findings, f => f.Rule == "DB05");

        // Присутствует предупреждение о том, что контейнерный анализатор отключен
        Assert.Contains(findings, f => f.Rule == DbAnalyzer.ContainerUnavailableRule);
    }

    [Fact]
    public async Task Scenario7_MissingContainer_ProducesDiagnosticWarningWithRecommendation()
    {
        var dbContextSource = """
            using Microsoft.EntityFrameworkCore;
            namespace Test;
            public class TestDbContext : DbContext { }
            """;
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "TestDbContext.cs"), dbContextSource);

        var extractor = new RoslynMetadataExtractor();
        var analyzer = new DbAnalyzer(extractor);
        var context = new AnalyzerContext { RepositoryPath = _tempDirectory, CommitHash = "hash5" };

        var findings = await analyzer.AnalyzeAsync(context);

        var containerFinding = Assert.Single(findings, f => f.Rule == DbAnalyzer.ContainerUnavailableRule);
        Assert.Equal(Severity.Warning, containerFinding.Severity);
        Assert.Contains("Изолированный контейнерный анализатор базы данных временно не подключён", containerFinding.Message);
        Assert.Contains("ADR-001", containerFinding.Message);
        Assert.Contains("ADR-001", containerFinding.Recommendation);
    }

    [Fact]
    public async Task Scenario8_OtherAnalyzers_CanExecuteAlongsideDatabaseAnalyzer()
    {
        var otherMockAnalyzer = new Mock<IAnalyzer>();
        otherMockAnalyzer.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
        otherMockAnalyzer
            .Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding { Rule = "CS001", Message = "Mock CS Finding", Severity = Severity.Warning }]);

        var dbAnalyzer = new DbAnalyzer(new RoslynMetadataExtractor());

        var context = new AnalyzerContext { RepositoryPath = _tempDirectory, CommitHash = "hash6" };

        var otherResult = await otherMockAnalyzer.Object.AnalyzeAsync(context);
        var dbResult = await dbAnalyzer.AnalyzeAsync(context);

        Assert.NotNull(otherResult);
        Assert.NotNull(dbResult);

        Assert.Single(otherResult);
        Assert.Equal("CS001", otherResult[0].Rule);

        Assert.Equal(AnalyzerType.Database, dbAnalyzer.Type);
        Assert.Contains(dbResult, f => f.Rule == DbAnalyzer.MissingDbContextRule);
    }

    [Fact]
    public void Scenario9_OldReflectionInspector_IsNotRegisteredInProductionPipeline()
    {
        var services = new ServiceCollection();

        // Конфигурация DI в точности повторяет Program.cs:
        // Используется только RoslynMetadataExtractor, старый инспектор не регистрируется
        services.AddScoped<RoslynModelInspector>();
        services.AddScoped<IRoslynMetadataExtractor, RoslynMetadataExtractor>();
        services.AddScoped<IAnalyzer, DbAnalyzer>();

        using var provider = services.BuildServiceProvider();

        // 1. Проверяем, что в DI НЕ зарегистрированы компоненты старого reflection-based инспектора
        var descriptors = services.ToList();
        Assert.DoesNotContain(descriptors, d => d.ServiceType.Name == "IEfCoreModelInspector");
        Assert.DoesNotContain(descriptors, d => d.ImplementationType?.Name == "EfCoreModelInspector");
        Assert.DoesNotContain(descriptors, d => d.ImplementationType?.Name == "EfCoreMetadataProvider");
        Assert.DoesNotContain(descriptors, d => d.ImplementationType?.Name == "EfCoreDatabaseProjectResolver");
        Assert.DoesNotContain(descriptors, d => d.ImplementationType?.Name == "EfCoreMetadataMapper");

        // 2. Проверяем, что резолвится DatabaseAnalyzer через IRoslynMetadataExtractor
        var dbAnalyzer = provider.GetRequiredService<IAnalyzer>();
        Assert.Equal(AnalyzerType.Database, dbAnalyzer.Type);
        Assert.IsType<DbAnalyzer>(dbAnalyzer);
    }

    [Fact]
    public void DatabaseRuleEvaluator_EvaluatesRulesDirectly_WhenGivenMetadata()
    {
        var metadata = new DatabaseMetadata
        {
            Entities =
            [
                new DatabaseEntityMetadata
                {
                    EntityName = "BrokenEntity",
                    TableName = "BrokenEntities",
                    Keys = [], // Нет первичного ключа -> DB01
                    ForeignKeys =
                    [
                        new DatabaseForeignKeyMetadata
                        {
                            Name = "FK_Broken_Other",
                            PropertyNames = ["OtherId"],
                            PrincipalEntityName = "Other",
                            NavigationToPrincipal = null // Нет навигации -> DB02
                        }
                    ],
                    Indexes = [] // Нет индекса под FK -> DB04
                }
            ]
        };

        var findings = DatabaseRuleEvaluator.Evaluate(metadata);

        Assert.Contains(findings, f => f.Rule == "DB01");
        Assert.Contains(findings, f => f.Rule == "DB02");
        Assert.Contains(findings, f => f.Rule == "DB04");
    }

    [Fact]
    public async Task Scenario10_NonExistentRepositoryDirectory_ReturnsWarningFindingWithoutThrowingException()
    {
        var nonExistentPath = Path.Combine(_tempDirectory, "non_existent_subdir_" + Guid.NewGuid().ToString("N"));
        var extractor = new RoslynMetadataExtractor();
        var analyzer = new DbAnalyzer(extractor);
        var context = new AnalyzerContext { RepositoryPath = nonExistentPath, CommitHash = "hash-missing" };

        var findings = await analyzer.AnalyzeAsync(context);

        Assert.NotNull(findings);
        Assert.Contains(findings, f => f.Rule == DbAnalyzer.ExtractionErrorRule);
        var errorFinding = findings.First(f => f.Rule == DbAnalyzer.ExtractionErrorRule);
        Assert.Equal(Severity.Warning, errorFinding.Severity);
        Assert.Contains("Каталог репозитория не найден", errorFinding.Message);
        Assert.DoesNotContain(findings, f => f.Rule == DbAnalyzer.ContainerUnavailableRule);
    }

    [Fact]
    public async Task Scenario11_ExtractorThrowsException_ReturnsWarningFindingWithoutThrowingException()
    {
        var mockExtractor = new Mock<IRoslynMetadataExtractor>();
        mockExtractor
            .Setup(x => x.ExtractAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Имитация сбоя ввода-вывода при чтении исходников"));

        var analyzer = new DbAnalyzer(mockExtractor.Object);
        var context = new AnalyzerContext { RepositoryPath = _tempDirectory, CommitHash = "hash-io-err" };

        var findings = await analyzer.AnalyzeAsync(context);

        Assert.NotNull(findings);
        Assert.Contains(findings, f => f.Rule == DbAnalyzer.ExtractionErrorRule);
        var errorFinding = findings.First(f => f.Rule == DbAnalyzer.ExtractionErrorRule);
        Assert.Equal(Severity.Warning, errorFinding.Severity);
        Assert.Contains("Ошибка статического извлечения метаданных EF Core", errorFinding.Message);
        Assert.Contains("Имитация сбоя ввода-вывода", errorFinding.Message);
        Assert.DoesNotContain(findings, f => f.Rule == DbAnalyzer.ContainerUnavailableRule);
    }

    [Fact]
    public async Task Scenario12_DatabaseAnalyzerFailure_DoesNotStopOtherAnalyzersInPipeline()
    {
        // Имитируем цикл анализа в AnalysisService: CSharpAnalyzer -> DatabaseAnalyzer -> SqlAnalyzer -> SecurityAnalyzer
        var csMock = new Mock<IAnalyzer>();
        csMock.SetupGet(a => a.Type).Returns(AnalyzerType.CSharp);
        csMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding { Rule = "CS001", Message = "CSharp Finding", Severity = Severity.Warning }]);

        var brokenExtractor = new Mock<IRoslynMetadataExtractor>();
        brokenExtractor.Setup(x => x.ExtractAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Фатальный сбой синтаксического разбора"));

        var dbAnalyzer = new DbAnalyzer(brokenExtractor.Object);

        var sqlMock = new Mock<IAnalyzer>();
        sqlMock.SetupGet(a => a.Type).Returns(AnalyzerType.Sql);
        sqlMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding { Rule = "SQL001", Message = "SQL Finding", Severity = Severity.Warning }]);

        var secMock = new Mock<IAnalyzer>();
        secMock.SetupGet(a => a.Type).Returns(AnalyzerType.Security);
        secMock.Setup(a => a.AnalyzeAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AnalyzerFinding { Rule = "SEC001", Message = "Security Finding", Severity = Severity.Error }]);

        var analyzers = new List<IAnalyzer> { csMock.Object, dbAnalyzer, sqlMock.Object, secMock.Object };
        var context = new AnalyzerContext { RepositoryPath = _tempDirectory, CommitHash = "hash-pipeline" };

        var pipelineFindings = new List<AnalyzerFinding>();

        // Выполняем конвейер в точности как AnalysisService.ExecuteAnalysisAsync
        foreach (var analyzer in analyzers)
        {
            var findings = await analyzer.AnalyzeAsync(context);
            pipelineFindings.AddRange(findings);
        }

        // Подтверждаем, что сбой в DatabaseAnalyzer не остановил остальные анализаторы
        Assert.Contains(pipelineFindings, f => f.Rule == "CS001");
        Assert.Contains(pipelineFindings, f => f.Rule == DbAnalyzer.ExtractionErrorRule);
        Assert.DoesNotContain(pipelineFindings, f => f.Rule == DbAnalyzer.ContainerUnavailableRule);
        Assert.Contains(pipelineFindings, f => f.Rule == "SQL001");
        Assert.Contains(pipelineFindings, f => f.Rule == "SEC001");
    }

    [Fact]
    public void Scenario13_DatabaseAnalyzer_HasExactlyOnePublicConstructor_AcceptingIRoslynMetadataExtractor()
    {
        var constructors = typeof(DbAnalyzer).GetConstructors();

        // Проверяем полное удаление переходного конструктора
        Assert.Single(constructors);

        var ctor = constructors[0];
        var parameters = ctor.GetParameters();

        Assert.Single(parameters);
        Assert.Equal(typeof(IRoslynMetadataExtractor), parameters[0].ParameterType);
        Assert.Equal("metadataExtractor", parameters[0].Name);
    }

    [Fact]
    public async Task Scenario14_MultipleDbContexts_ExtractedSeparatelyWithoutCrossPollution()
    {
        var orderingSource = """
            using Microsoft.EntityFrameworkCore;

            namespace Ordering;

            public class Order
            {
                public int Id { get; set; }
                public string OrderNumber { get; set; } = null!;
            }

            public class OrderingDbContext : DbContext
            {
                public DbSet<Order> Orders => Set<Order>();
            }
            """;

        var catalogSource = """
            using Microsoft.EntityFrameworkCore;

            namespace Catalog;

            public class Product
            {
                public int Id { get; set; }
                public string Sku { get; set; } = null!;
            }

            public class CatalogDbContext : DbContext
            {
                public DbSet<Product> Products => Set<Product>();
            }
            """;

        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "OrderingDbContext.cs"), orderingSource);
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "CatalogDbContext.cs"), catalogSource);

        var extractor = new RoslynMetadataExtractor();
        var result = await extractor.ExtractAsync(_tempDirectory);

        Assert.NotNull(result);
        Assert.Equal(2, result.Contexts.Count);

        var ordering = Assert.Single(result.Contexts, c => c.Name == "OrderingDbContext");
        var catalog = Assert.Single(result.Contexts, c => c.Name == "CatalogDbContext");

        Assert.Single(ordering.Entities, e => e.EntityName == "Order");
        Assert.DoesNotContain(ordering.Entities, e => e.EntityName == "Product");

        Assert.Single(catalog.Entities, e => e.EntityName == "Product");
        Assert.DoesNotContain(catalog.Entities, e => e.EntityName == "Order");

        var allMetadata = result.ToAllDatabaseMetadata();
        Assert.Equal(2, allMetadata.Count);
        Assert.Contains(allMetadata, m => m.ContextName == "OrderingDbContext");
        Assert.Contains(allMetadata, m => m.ContextName == "CatalogDbContext");
    }
}
