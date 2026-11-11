using DatabaseAnalyzer.Roslyn;

namespace DatabaseAnalyzer.Tests.Roslyn;

public static class StaticConstructorExecutionTracker
{
    public static bool WasExecuted { get; set; }
}

public sealed class RoslynModelInspectorTests
{
    private readonly RoslynModelInspector _inspector = new();

    [Fact]
    public void InspectSource_SimpleDbContext_DiscoversContextAndEntities()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            namespace TestApp.Data;

            public class AppDbContext : DbContext
            {
                public DbSet<User> Users { get; set; }
            }

            public class User
            {
                public int Id { get; set; }
                public string Name { get; set; } = string.Empty;
            }
            """;

        var result = _inspector.InspectSource(source, "AppDbContext.cs");

        Assert.Empty(result.Warnings);
        var context = Assert.Single(result.Contexts);
        Assert.Equal("AppDbContext", context.Name);
        Assert.Equal("TestApp.Data", context.Namespace);
        Assert.Equal("AppDbContext.cs", context.FilePath);

        var entity = Assert.Single(context.Entities);
        Assert.Equal("User", entity.EntityName);
        Assert.Equal("Users", entity.DbSetPropertyName);

        var metadata = context.ToDatabaseMetadata();
        Assert.Equal("AppDbContext", metadata.ContextName);
        var entityMeta = Assert.Single(metadata.Entities);
        Assert.Equal("User", entityMeta.EntityName);
        Assert.Equal("Users", entityMeta.TableName);
    }

    [Fact]
    public void InspectSource_FullyQualifiedBaseType_DiscoversContext()
    {
        var source = """
            namespace Enterprise.Database;

            public class EnterpriseContext : Microsoft.EntityFrameworkCore.DbContext
            {
                public Microsoft.EntityFrameworkCore.DbSet<Account> Accounts { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source, "EnterpriseContext.cs");

        Assert.Empty(result.Warnings);
        var context = Assert.Single(result.Contexts);
        Assert.Equal("EnterpriseContext", context.Name);

        var entity = Assert.Single(context.Entities);
        Assert.Equal("Account", entity.EntityName);
        Assert.Equal("Accounts", entity.DbSetPropertyName);
    }

    [Fact]
    public void InspectSource_GlobalAliasQualifiedBaseType_DiscoversContext()
    {
        var source = """
            namespace Enterprise.Database;

            public class GlobalContext : global::Microsoft.EntityFrameworkCore.DbContext
            {
                public global::Microsoft.EntityFrameworkCore.DbSet<Tenant> Tenants { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source, "GlobalContext.cs");

        Assert.Empty(result.Warnings);
        var context = Assert.Single(result.Contexts);
        Assert.Equal("GlobalContext", context.Name);

        var entity = Assert.Single(context.Entities);
        Assert.Equal("Tenant", entity.EntityName);
        Assert.Equal("Tenants", entity.DbSetPropertyName);
    }

    [Fact]
    public void InspectSource_MultipleDbSetsInSingleContext_DiscoversAllEntities()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            public class StoreContext : DbContext
            {
                public DbSet<Customer> Customers { get; set; }
                public virtual DbSet<Order> Orders { get; set; } = null!;
                public DbSet<Product>? Products => Set<Product>();
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var context = Assert.Single(result.Contexts);
        Assert.Equal("StoreContext", context.Name);
        Assert.Equal(3, context.Entities.Count);

        Assert.Contains(context.Entities, e => e.EntityName == "Customer" && e.DbSetPropertyName == "Customers");
        Assert.Contains(context.Entities, e => e.EntityName == "Order" && e.DbSetPropertyName == "Orders");
        Assert.Contains(context.Entities, e => e.EntityName == "Product" && e.DbSetPropertyName == "Products");
    }

    [Fact]
    public void InspectSources_MultipleDbContextsInSingleRepository_DiscoversBothWithoutMixing()
    {
        var orderingSource = """
            using Microsoft.EntityFrameworkCore;

            namespace Sales;

            public class OrderingDbContext : DbContext
            {
                public DbSet<Order> Orders { get; set; }
                public DbSet<OrderItem> Items { get; set; }
            }
            """;

        var billingSource = """
            using Microsoft.EntityFrameworkCore;

            namespace Billing;

            public class BillingDbContext : DbContext
            {
                public DbSet<Invoice> Invoices { get; set; }
                public DbSet<Payment> Payments { get; set; }
            }
            """;

        var result = _inspector.InspectSources([
            (orderingSource, "OrderingDbContext.cs"),
            (billingSource, "BillingDbContext.cs")
        ]);

        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Contexts.Count);

        var ordering = Assert.Single(result.Contexts, c => c.Name == "OrderingDbContext");
        Assert.Equal("Sales", ordering.Namespace);
        Assert.Equal(2, ordering.Entities.Count);
        Assert.Contains(ordering.Entities, e => e.EntityName == "Order" && e.DbSetPropertyName == "Orders");
        Assert.Contains(ordering.Entities, e => e.EntityName == "OrderItem" && e.DbSetPropertyName == "Items");
        Assert.DoesNotContain(ordering.Entities, e => e.EntityName == "Invoice");
        Assert.DoesNotContain(ordering.Entities, e => e.EntityName == "Payment");

        var billing = Assert.Single(result.Contexts, c => c.Name == "BillingDbContext");
        Assert.Equal("Billing", billing.Namespace);
        Assert.Equal(2, billing.Entities.Count);
        Assert.Contains(billing.Entities, e => e.EntityName == "Invoice" && e.DbSetPropertyName == "Invoices");
        Assert.Contains(billing.Entities, e => e.EntityName == "Payment" && e.DbSetPropertyName == "Payments");
        Assert.DoesNotContain(billing.Entities, e => e.EntityName == "Order");
        Assert.DoesNotContain(billing.Entities, e => e.EntityName == "OrderItem");
    }

    [Fact]
    public void InspectSources_PartialClassesAcrossFiles_MergesEntitiesIntoSingleContext()
    {
        var part1 = """
            using Microsoft.EntityFrameworkCore;

            namespace App.Data;

            public partial class AppDbContext : DbContext
            {
                public DbSet<User> Users { get; set; }
            }
            """;

        var part2 = """
            namespace App.Data;

            public partial class AppDbContext
            {
                public Microsoft.EntityFrameworkCore.DbSet<Role> Roles { get; set; }
            }
            """;

        var result = _inspector.InspectSources([
            (part1, "AppDbContext.cs"),
            (part2, "AppDbContext.Roles.cs")
        ]);

        Assert.Empty(result.Warnings);
        var context = Assert.Single(result.Contexts);
        Assert.Equal("AppDbContext", context.Name);
        Assert.Equal(2, context.Entities.Count);
        Assert.Contains(context.Entities, e => e.EntityName == "User" && e.DbSetPropertyName == "Users");
        Assert.Contains(context.Entities, e => e.EntityName == "Role" && e.DbSetPropertyName == "Roles");
    }

    [Fact]
    public async Task InspectDirectoryAsync_WithoutCsproj_SuccessfullyDiscoversContext()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "RoslynModelInspectorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var source = """
                using Microsoft.EntityFrameworkCore;

                namespace DirectFiles;

                public class FileBasedDbContext : DbContext
                {
                    public DbSet<Document> Documents { get; set; }
                }
                """;

            var filePath = Path.Combine(tempDir, "FileBasedDbContext.cs");
            await File.WriteAllTextAsync(filePath, source);

            // Обратите внимание: в директории tempDir НЕТ файлов .csproj.
            Assert.Empty(Directory.GetFiles(tempDir, "*.csproj"));

            var result = await _inspector.InspectDirectoryAsync(tempDir);

            var context = Assert.Single(result.Contexts);
            Assert.Equal("FileBasedDbContext", context.Name);
            var entity = Assert.Single(context.Entities);
            Assert.Equal("Document", entity.EntityName);
            Assert.Equal("Documents", entity.DbSetPropertyName);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void InspectSources_SyntaxErrorInOneFile_ContinuesAnalysisAndRecordsWarning()
    {
        var badSource = """
            using System;
            public class BrokenClass {
                this is totally invalid C# syntax !!!
            """;

        var validSource = """
            using Microsoft.EntityFrameworkCore;

            public class ResilientDbContext : DbContext
            {
                public DbSet<Product> Products { get; set; }
            }
            """;

        var result = _inspector.InspectSources([
            (badSource, "Broken.cs"),
            (validSource, "ResilientDbContext.cs")
        ]);

        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("Broken.cs"));

        var context = Assert.Single(result.Contexts);
        Assert.Equal("ResilientDbContext", context.Name);
        var entity = Assert.Single(context.Entities);
        Assert.Equal("Product", entity.EntityName);
    }

    [Fact]
    public void InspectSource_NonDbContextClass_IsIgnored()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            namespace Services;

            public class UserService
            {
                public DbSet<User> Users { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Contexts);
    }

    [Fact]
    public void InspectSource_ShortNameDbContextWithoutEfCoreUsing_IsNotDiscoveredAsDbContext()
    {
        // Класс наследует DbContext, но using Microsoft.EntityFrameworkCore отсутствует.
        // Без подтверждения происхождения через using или квалифицированное имя класс не должен распознаваться.
        var source = """
            namespace CustomFramework;

            public class CustomContext : DbContext
            {
                public DbSet<CustomEntity> Entities { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Contexts);
    }

    [Fact]
    public void InspectSource_DbContextAliasedToNonEfCoreType_IsNotDiscovered()
    {
        var source = """
            using DbContext = OtherNamespace.CustomBase;

            public class AliasedContext : DbContext
            {
                public DbSet<Item> Items { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Contexts);
    }

    [Fact]
    public void InspectSources_UsingAliasForEfCore_DiscoversContext()
    {
        var source = """
            using EF = Microsoft.EntityFrameworkCore;

            public class AliasedEfContext : EF.DbContext
            {
                public EF.DbSet<Member> Members { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var context = Assert.Single(result.Contexts);
        Assert.Equal("AliasedEfContext", context.Name);
        var entity = Assert.Single(context.Entities);
        Assert.Equal("Member", entity.EntityName);
        Assert.Equal("Members", entity.DbSetPropertyName);
    }

    [Fact]
    public void InspectSources_GlobalUsingInSeparateFile_EnablesShortDbContextInAllFiles()
    {
        var globalUsingsSource = """
            global using Microsoft.EntityFrameworkCore;
            """;

        var contextSource = """
            namespace App;

            public class GlobalEnabledContext : DbContext
            {
                public DbSet<Setting> Settings { get; set; }
            }
            """;

        var result = _inspector.InspectSources([
            (globalUsingsSource, "GlobalUsings.cs"),
            (contextSource, "GlobalEnabledContext.cs")
        ]);

        Assert.Empty(result.Warnings);
        var context = Assert.Single(result.Contexts);
        Assert.Equal("GlobalEnabledContext", context.Name);
        var entity = Assert.Single(context.Entities);
        Assert.Equal("Setting", entity.EntityName);
        Assert.Equal("Settings", entity.DbSetPropertyName);
    }

    [Fact]
    public void InspectSource_MaliciousCodeWithStaticConstructorBomb_DoesNotExecuteUntrustedCode()
    {
        StaticConstructorExecutionTracker.WasExecuted = false;

        var sentinelFile = Path.Combine(Path.GetTempPath(), "rce_test_sentinel_" + Guid.NewGuid().ToString("N") + ".tmp");

        if (File.Exists(sentinelFile))
        {
            File.Delete(sentinelFile);
        }

        try
        {
            // Исследуемый исходный код содержит вредоносный статический конструктор с побочным эффектом:
            // установкой флага в StaticConstructorExecutionTracker и созданием файла-метки.
            var maliciousSource = $$"""
                using Microsoft.EntityFrameworkCore;
                using System.IO;

                namespace MaliciousProject;

                public class ExploitDbContext : DbContext
                {
                    static ExploitDbContext()
                    {
                        DatabaseAnalyzer.Tests.Roslyn.StaticConstructorExecutionTracker.WasExecuted = true;
                        File.WriteAllText(@"{{sentinelFile}}", "EXPLOITED");
                    }

                    public DbSet<VulnerableEntity> Entities { get; set; }
                }
                """;

            var result = _inspector.InspectSource(maliciousSource, "ExploitDbContext.cs");

            // 1. Метаданные корректно извлечены статическим синтаксическим анализатором:
            var context = Assert.Single(result.Contexts);
            Assert.Equal("ExploitDbContext", context.Name);
            var entity = Assert.Single(context.Entities);
            Assert.Equal("VulnerableEntity", entity.EntityName);

            // 2. ДОКАЗАТЕЛЬСТВО БЕЗОПАСНОСТИ:
            // Статический конструктор НЕ выполнялся, так как сборка не загружалась в CLR runtime!
            Assert.False(StaticConstructorExecutionTracker.WasExecuted, "Статический конструктор пользовательского кода НЕ должен был исполняться!");
            Assert.False(File.Exists(sentinelFile), "Файл-метка не должен быть создан, так как код внутри исследуемого проекта не исполнялся!");
        }
        finally
        {
            if (File.Exists(sentinelFile))
            {
                File.Delete(sentinelFile);
            }
        }
    }
}
