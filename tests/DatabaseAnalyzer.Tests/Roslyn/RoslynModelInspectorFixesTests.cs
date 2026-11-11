using Application.Analyzers;
using DatabaseAnalyzer;
using DatabaseAnalyzer.Metadata;
using DatabaseAnalyzer.Providers;
using DatabaseAnalyzer.Roslyn;
using Moq;

namespace DatabaseAnalyzer.Tests.Roslyn;

public sealed class RoslynModelInspectorFixesTests
{
    private readonly RoslynModelInspector _inspector = new();

    #region DEF-001: Fluent API Priority over Data Annotations

    [Fact]
    public void DEF001_FluentApi_ToTable_Overrides_DataAnnotation_Table()
    {
        var source = """
            using System.ComponentModel.DataAnnotations.Schema;
            using Microsoft.EntityFrameworkCore;

            namespace App.Models;

            public class AppDbContext : DbContext
            {
                public DbSet<User> Users { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<User>().ToTable("t_users_fluent");
                }
            }

            [Table("t_users_annotation")]
            public class User
            {
                public int Id { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        var context = Assert.Single(result.Contexts);
        var user = Assert.Single(context.Entities);

        Assert.Equal("t_users_fluent", user.TableName);
    }

    [Fact]
    public void DEF001_FluentApi_ToTableWithSchema_Overrides_DataAnnotation_TableWithSchema()
    {
        var source = """
            using System.ComponentModel.DataAnnotations.Schema;
            using Microsoft.EntityFrameworkCore;

            namespace App.Models;

            public class AppDbContext : DbContext
            {
                public DbSet<User> Users { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<User>().ToTable("t_users_fluent", "fluent_schema");
                }
            }

            [Table("t_users_annotation", Schema = "annotation_schema")]
            public class User
            {
                public int Id { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        var context = Assert.Single(result.Contexts);
        var user = Assert.Single(context.Entities);

        Assert.Equal("t_users_fluent", user.TableName);
        Assert.Equal("fluent_schema", user.SchemaName);
    }

    [Fact]
    public void DEF001_FluentApi_HasKey_Overrides_DataAnnotation_Key()
    {
        var source = """
            using System.ComponentModel.DataAnnotations;
            using Microsoft.EntityFrameworkCore;

            namespace App.Models;

            public class AppDbContext : DbContext
            {
                public DbSet<Document> Documents { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Document>().HasKey(d => d.DocumentId);
                }
            }

            public class Document
            {
                [Key]
                public string Code { get; set; } = null!;

                public int DocumentId { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        var context = Assert.Single(result.Contexts);
        var doc = Assert.Single(context.Entities);

        var primaryKey = Assert.Single(doc.Keys, k => k.IsPrimaryKey);
        Assert.Equal(["DocumentId"], primaryKey.PropertyNames);
    }

    [Fact]
    public void DEF001_DataAnnotation_Table_Applies_WhenFluentApiAbsent()
    {
        var source = """
            using System.ComponentModel.DataAnnotations.Schema;
            using Microsoft.EntityFrameworkCore;

            namespace App.Models;

            public class AppDbContext : DbContext
            {
                public DbSet<Product> Products { get; set; }
            }

            [Table("products_table", Schema = "catalog")]
            public class Product
            {
                public int Id { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        var context = Assert.Single(result.Contexts);
        var product = Assert.Single(context.Entities);

        Assert.Equal("products_table", product.TableName);
        Assert.Equal("catalog", product.SchemaName);
    }

    #endregion

    #region DEF-002: Class Name Collisions Across Namespaces

    [Fact]
    public void DEF002_Disambiguates_Entity_From_Dto_With_Same_Name()
    {
        var contextSource = """
            using Microsoft.EntityFrameworkCore;
            using Domain.Entities;

            namespace Infrastructure.Data;

            public class AppDbContext : DbContext
            {
                public DbSet<User> Users { get; set; }
            }
            """;

        var domainSource = """
            using System.ComponentModel.DataAnnotations.Schema;

            namespace Domain.Entities;

            [Table("domain_users")]
            public class User
            {
                public int Id { get; set; }
                public string UserName { get; set; } = null!;
            }
            """;

        var dtoSource = """
            using System.ComponentModel.DataAnnotations.Schema;

            namespace Api.Dtos;

            [Table("dto_users_must_not_match")]
            public class User
            {
                public string Token { get; set; } = null!;
                public string ClientIp { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSources([
            (contextSource, "AppDbContext.cs"),
            (domainSource, "DomainUser.cs"),
            (dtoSource, "DtoUser.cs")
        ]);

        var context = Assert.Single(result.Contexts);
        var user = Assert.Single(context.Entities);

        Assert.Equal("domain_users", user.TableName);
        Assert.Contains(user.Properties, p => p.PropertyName == "UserName");
        Assert.DoesNotContain(user.Properties, p => p.PropertyName == "Token");
        Assert.DoesNotContain(user.Properties, p => p.PropertyName == "ClientIp");
    }

    [Fact]
    public void DEF002_Merges_Partial_Classes_In_Same_Namespace()
    {
        var contextSource = """
            using Microsoft.EntityFrameworkCore;
            using Domain.Entities;

            namespace Infrastructure.Data;

            public class AppDbContext : DbContext
            {
                public DbSet<Account> Accounts { get; set; }
            }
            """;

        var partial1Source = """
            namespace Domain.Entities;

            public partial class Account
            {
                public int Id { get; set; }
            }
            """;

        var partial2Source = """
            namespace Domain.Entities;

            public partial class Account
            {
                public string AccountNumber { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSources([
            (contextSource, "AppDbContext.cs"),
            (partial1Source, "Account.Part1.cs"),
            (partial2Source, "Account.Part2.cs")
        ]);

        var context = Assert.Single(result.Contexts);
        var account = Assert.Single(context.Entities);

        Assert.Contains(account.Properties, p => p.PropertyName == "Id");
        Assert.Contains(account.Properties, p => p.PropertyName == "AccountNumber");
    }

    [Fact]
    public void DEF002_Emits_Warning_When_Multiple_Namespaces_Imported()
    {
        var contextSource = """
            using Microsoft.EntityFrameworkCore;
            using ModuleA;
            using ModuleB;

            namespace App;

            public class AppDbContext : DbContext
            {
                public DbSet<Item> Items { get; set; }
            }
            """;

        var itemASource = """
            namespace ModuleA;
            public class Item { public int Id { get; set; } public string NameA { get; set; } = null!; }
            """;

        var itemBSSource = """
            namespace ModuleB;
            public class Item { public int Id { get; set; } public string NameB { get; set; } = null!; }
            """;

        var result = _inspector.InspectSources([
            (contextSource, "AppDbContext.cs"),
            (itemASource, "ItemA.cs"),
            (itemBSSource, "ItemB.cs")
        ]);

        Assert.Contains(result.Warnings, w => w.Contains("нескольких импортированных пространствах имён"));
        var context = Assert.Single(result.Contexts);
        var item = Assert.Single(context.Entities);

        // При неоднозначности неподтверждённые метаданные свойств не извлекаются
        Assert.Empty(item.Properties);
    }

    [Fact]
    public void DEF002_Multiple_Candidates_Without_Import_Emits_Warning_And_No_Metadata()
    {
        var contextSource = """
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class AppDbContext : DbContext
            {
                public DbSet<Item> Items { get; set; }
            }
            """;

        var itemASource = """
            namespace ModuleA;
            public class Item { public int Id { get; set; } public string PropA { get; set; } = null!; }
            """;

        var itemBSource = """
            namespace ModuleB;
            public class Item { public int Id { get; set; } public string PropB { get; set; } = null!; }
            """;

        var result = _inspector.InspectSources([
            (contextSource, "AppDbContext.cs"),
            (itemASource, "ItemA.cs"),
            (itemBSource, "ItemB.cs")
        ]);

        Assert.Contains(result.Warnings, w => w.Contains("ни одно из них не импортировано"));
        var context = Assert.Single(result.Contexts);
        var item = Assert.Single(context.Entities);

        // При неоднозначности неподтверждённые метаданные свойств не извлекаются
        Assert.Empty(item.Properties);
    }

    [Fact]
    public void DEF002_Single_Candidate_Resolved_Via_Global_Using()
    {
        var globalUsingsSource = """
            global using Microsoft.EntityFrameworkCore;
            global using Domain.Entities;
            """;

        var contextSource = """
            namespace Infrastructure.Data;

            public class AppDbContext : DbContext
            {
                public DbSet<User> Users { get; set; }
            }
            """;

        var domainSource = """
            namespace Domain.Entities;

            public class User
            {
                public int Id { get; set; }
                public string Login { get; set; } = null!;
            }
            """;

        var dtoSource = """
            namespace Api.Dtos;

            public class User
            {
                public string Token { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSources([
            (globalUsingsSource, "GlobalUsings.cs"),
            (contextSource, "AppDbContext.cs"),
            (domainSource, "DomainUser.cs"),
            (dtoSource, "DtoUser.cs")
        ]);

        var context = Assert.Single(result.Contexts);
        var user = Assert.Single(context.Entities);

        Assert.Contains(user.Properties, p => p.PropertyName == "Login");
        Assert.DoesNotContain(user.Properties, p => p.PropertyName == "Token");
    }

    [Fact]
    public void DEF002_Type_Alias_Local_Resolves_Entity_Unambiguously()
    {
        var contextSource = """
            using Microsoft.EntityFrameworkCore;
            using User = Domain.Entities.User;

            namespace Infrastructure.Data;

            public class AppDbContext : DbContext
            {
                public DbSet<User> Users { get; set; }
            }
            """;

        var domainSource = """
            namespace Domain.Entities;

            public class User
            {
                public int Id { get; set; }
                public string DomainKey { get; set; } = null!;
            }
            """;

        var dtoSource = """
            namespace Api.Dtos;

            public class User
            {
                public string DtoKey { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSources([
            (contextSource, "AppDbContext.cs"),
            (domainSource, "DomainUser.cs"),
            (dtoSource, "DtoUser.cs")
        ]);

        var context = Assert.Single(result.Contexts);
        var user = Assert.Single(context.Entities);

        Assert.Contains(user.Properties, p => p.PropertyName == "DomainKey");
        Assert.DoesNotContain(user.Properties, p => p.PropertyName == "DtoKey");
    }

    [Fact]
    public void DEF002_Type_Alias_Global_Resolves_Entity_Unambiguously()
    {
        var globalUsingsSource = """
            global using Microsoft.EntityFrameworkCore;
            global using User = Domain.Entities.User;
            """;

        var contextSource = """
            namespace Infrastructure.Data;

            public class AppDbContext : DbContext
            {
                public DbSet<User> Users { get; set; }
            }
            """;

        var domainSource = """
            namespace Domain.Entities;

            public class User
            {
                public int Id { get; set; }
                public string GlobalAliasedProp { get; set; } = null!;
            }
            """;

        var otherSource = """
            namespace Other.Entities;

            public class User
            {
                public string OtherProp { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSources([
            (globalUsingsSource, "GlobalUsings.cs"),
            (contextSource, "AppDbContext.cs"),
            (domainSource, "DomainUser.cs"),
            (otherSource, "OtherUser.cs")
        ]);

        var context = Assert.Single(result.Contexts);
        var user = Assert.Single(context.Entities);

        Assert.Contains(user.Properties, p => p.PropertyName == "GlobalAliasedProp");
        Assert.DoesNotContain(user.Properties, p => p.PropertyName == "OtherProp");
    }

    #endregion

    #region DEF-003: IEntityTypeConfiguration<T> Application

    [Fact]
    public void DEF003_Explicit_ApplyConfiguration_Is_Applied()
    {
        var contextSource = """
            using Microsoft.EntityFrameworkCore;
            using Configurations;

            namespace App;

            public class AppDbContext : DbContext
            {
                public DbSet<Order> Orders { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfiguration(new OrderConfiguration());
                }
            }

            public class Order
            {
                public int Id { get; set; }
            }
            """;

        var configSource = """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;
            using App;

            namespace Configurations;

            public class OrderConfiguration : IEntityTypeConfiguration<Order>
            {
                public void Configure(EntityTypeBuilder<Order> builder)
                {
                    builder.ToTable("configured_orders");
                }
            }
            """;

        var result = _inspector.InspectSources([
            (contextSource, "AppDbContext.cs"),
            (configSource, "OrderConfiguration.cs")
        ]);

        var context = Assert.Single(result.Contexts);
        var order = Assert.Single(context.Entities);

        Assert.Equal("configured_orders", order.TableName);
    }

    [Fact]
    public void DEF003_Two_DbContexts_With_Different_Configurations()
    {
        var contextSource = """
            using Microsoft.EntityFrameworkCore;
            using Configurations;

            namespace App;

            public class ClientDbContext : DbContext
            {
                public DbSet<Customer> Customers { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfiguration(new ClientCustomerConfiguration());
                }
            }

            public class AdminDbContext : DbContext
            {
                public DbSet<Customer> Customers { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfiguration(new AdminCustomerConfiguration());
                }
            }

            public class Customer
            {
                public int Id { get; set; }
            }
            """;

        var configSource = """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;
            using App;

            namespace Configurations;

            public class ClientCustomerConfiguration : IEntityTypeConfiguration<Customer>
            {
                public void Configure(EntityTypeBuilder<Customer> builder)
                {
                    builder.ToTable("client_customers");
                }
            }

            public class AdminCustomerConfiguration : IEntityTypeConfiguration<Customer>
            {
                public void Configure(EntityTypeBuilder<Customer> builder)
                {
                    builder.ToTable("admin_customers");
                }
            }
            """;

        var result = _inspector.InspectSources([
            (contextSource, "Contexts.cs"),
            (configSource, "Configurations.cs")
        ]);

        var clientContext = result.Contexts.Single(c => c.Name == "ClientDbContext");
        var adminContext = result.Contexts.Single(c => c.Name == "AdminDbContext");

        Assert.Equal("client_customers", clientContext.Entities.Single().TableName);
        Assert.Equal("admin_customers", adminContext.Entities.Single().TableName);
    }

    [Fact]
    public void DEF003_Unapplied_Configuration_Is_Not_Applied()
    {
        var contextSource = """
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class AppDbContext : DbContext
            {
                public DbSet<Article> Articles { get; set; }
            }

            public class Article
            {
                public int Id { get; set; }
            }
            """;

        var configSource = """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;
            using App;

            namespace Configurations;

            public class ArticleConfiguration : IEntityTypeConfiguration<Article>
            {
                public void Configure(EntityTypeBuilder<Article> builder)
                {
                    builder.ToTable("unapplied_articles_table");
                }
            }
            """;

        var result = _inspector.InspectSources([
            (contextSource, "AppDbContext.cs"),
            (configSource, "ArticleConfiguration.cs")
        ]);

        var context = Assert.Single(result.Contexts);
        var article = Assert.Single(context.Entities);

        // Конфигурация не должна примениться
        Assert.NotEqual("unapplied_articles_table", article.TableName);
    }

    [Fact]
    public void DEF003_ApplyConfigurationsFromAssembly_Emits_Warning()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class AppDbContext : DbContext
            {
                public DbSet<User> Users { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
                }
            }

            public class User
            {
                public int Id { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Contains(result.Warnings, w => w.Contains("ApplyConfigurationsFromAssembly"));
    }

    #endregion

    #region DEF-004: Base Class Inheritance

    [Fact]
    public void DEF004_Inherits_Id_Property_And_Generates_Convention_PrimaryKey()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class AppDbContext : DbContext
            {
                public DbSet<Employee> Employees { get; set; }
            }

            public abstract class BaseEntity
            {
                public int Id { get; set; }
            }

            public class Employee : BaseEntity
            {
                public string Name { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSource(source);

        var context = Assert.Single(result.Contexts);
        var employee = Assert.Single(context.Entities);

        Assert.Contains(employee.Properties, p => p.PropertyName == "Id");
        Assert.Contains(employee.Properties, p => p.PropertyName == "Name");

        var pk = Assert.Single(employee.Keys, k => k.IsPrimaryKey);
        Assert.Equal(["Id"], pk.PropertyNames);
    }

    [Fact]
    public void DEF004_Inherits_Key_Attribute_From_BaseClass()
    {
        var source = """
            using System.ComponentModel.DataAnnotations;
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class AppDbContext : DbContext
            {
                public DbSet<Order> Orders { get; set; }
            }

            public abstract class EntityWithCustomKey
            {
                [Key]
                public Guid Identifier { get; set; }
            }

            public class Order : EntityWithCustomKey
            {
                public decimal Amount { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        var context = Assert.Single(result.Contexts);
        var order = Assert.Single(context.Entities);

        var pk = Assert.Single(order.Keys, k => k.IsPrimaryKey);
        Assert.Equal(["Identifier"], pk.PropertyNames);
    }

    [Fact]
    public void DEF004_Inherits_Required_Attribute_From_BaseClass()
    {
        var source = """
            using System.ComponentModel.DataAnnotations;
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class AppDbContext : DbContext
            {
                public DbSet<User> Users { get; set; }
            }

            public abstract class AuditableEntity
            {
                public int Id { get; set; }

                [Required]
                public string CreatedBy { get; set; } = null!;
            }

            public class User : AuditableEntity
            {
                public string Email { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSource(source);

        var context = Assert.Single(result.Contexts);
        var user = Assert.Single(context.Entities);

        var createdByProp = user.Properties.Single(p => p.PropertyName == "CreatedBy");
        Assert.False(createdByProp.IsNullable);
    }

    [Fact]
    public void DEF004_Inherits_Through_Multiple_Levels()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class AppDbContext : DbContext
            {
                public DbSet<Manager> Managers { get; set; }
            }

            public abstract class RootEntity
            {
                public int Id { get; set; }
            }

            public abstract class PersonEntity : RootEntity
            {
                public string FullName { get; set; } = null!;
            }

            public class Manager : PersonEntity
            {
                public string Department { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSource(source);

        var context = Assert.Single(result.Contexts);
        var manager = Assert.Single(context.Entities);

        Assert.Contains(manager.Properties, p => p.PropertyName == "Id");
        Assert.Contains(manager.Properties, p => p.PropertyName == "FullName");
        Assert.Contains(manager.Properties, p => p.PropertyName == "Department");
    }

    [Fact]
    public void DEF004_Emits_Warning_For_Unknown_Base_Class()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class AppDbContext : DbContext
            {
                public DbSet<CustomEntity> Entities { get; set; }
            }

            public class CustomEntity : ExternalNonExistentBase
            {
                public int Id { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Contains(result.Warnings, w => w.Contains("ExternalNonExistentBase"));
    }

    [Fact]
    public void DEF004_Interface_Without_I_Prefix_Does_Not_Emit_Warning()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public interface CustomMarkerInterface
            {
            }

            public class AppDbContext : DbContext
            {
                public DbSet<User> Users { get; set; }
            }

            public class User : CustomMarkerInterface
            {
                public int Id { get; set; }
                public string Name { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.DoesNotContain(result.Warnings, w => w.Contains("CustomMarkerInterface"));
        var context = Assert.Single(result.Contexts);
        var user = Assert.Single(context.Entities);
        Assert.Contains(user.Properties, p => p.PropertyName == "Id");
        Assert.Contains(user.Properties, p => p.PropertyName == "Name");
    }

    [Fact]
    public void DEF004_Handles_Cyclic_Inheritance_Gracefully()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class AppDbContext : DbContext
            {
                public DbSet<CycleA> Items { get; set; }
            }

            public class CycleA : CycleB
            {
                public int Id { get; set; }
            }

            public class CycleB : CycleA
            {
                public string Name { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSource(source);

        var context = Assert.Single(result.Contexts);
        var item = Assert.Single(context.Entities);
        Assert.NotNull(item);
    }

    #endregion

    #region DEF-005: EF Core ForeignKeyIndexConvention and Rule DB04

    [Fact]
    public async Task DEF005_ForeignKey_Automatically_Gets_Convention_Index_And_DB04_Passes()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class AppDbContext : DbContext
            {
                public DbSet<Order> Orders { get; set; }
                public DbSet<Customer> Customers { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>()
                        .HasOne(o => o.Customer)
                        .WithMany(c => c.Orders)
                        .HasForeignKey(o => o.CustomerId);
                }
            }

            public class Order
            {
                public int Id { get; set; }
                public int CustomerId { get; set; }
                public Customer Customer { get; set; } = null!;
            }

            public class Customer
            {
                public int Id { get; set; }
                public List<Order> Orders { get; set; } = [];
            }
            """;

        var result = _inspector.InspectSource(source);

        var context = Assert.Single(result.Contexts);
        var order = context.Entities.Single(e => e.EntityName == "Order");

        // Проверяем, что для CustomerId создан индекс соглашения
        var index = Assert.Single(order.Indexes);
        Assert.Equal(["CustomerId"], index.PropertyNames);
        Assert.Equal("IX_Order_CustomerId", index.Name);

        // Проверяем совместимость с правилом DB04 через DatabaseAnalyzer
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();
        metadataProvider.Setup(p => p.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                    {
                        EntityName = order.EntityName,
                        TableName = order.TableName ?? order.EntityName,
                        Keys = order.Keys,
                        ForeignKeys = order.ForeignKeys,
                        Indexes = order.Indexes
                    }
                ]
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var findings = await analyzer.AnalyzeAsync(new AnalyzerContext { RepositoryPath = "test" });

        // DB04 не должно выдать предупреждение, так как индекс соглашения присутствует
        Assert.DoesNotContain(findings, f => f.Rule == "DB04");
    }

    [Fact]
    public void DEF005_Explicit_Index_On_ForeignKey_Prevents_Duplicate_Convention_Index()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class AppDbContext : DbContext
            {
                public DbSet<Order> Orders { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>()
                        .HasOne<Customer>()
                        .WithMany()
                        .HasForeignKey(o => o.CustomerId);

                    modelBuilder.Entity<Order>()
                        .HasIndex(o => o.CustomerId)
                        .HasDatabaseName("IX_Custom_CustomerId");
                }
            }

            public class Order
            {
                public int Id { get; set; }
                public int CustomerId { get; set; }
            }

            public class Customer
            {
                public int Id { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        var context = Assert.Single(result.Contexts);
        var order = context.Entities.Single(e => e.EntityName == "Order");

        // Должен остаться только один индекс с явным именем
        var index = Assert.Single(order.Indexes);
        Assert.Equal("IX_Custom_CustomerId", index.Name);
    }

    [Fact]
    public void DEF005_Composite_ForeignKey_Creates_Composite_Convention_Index()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class AppDbContext : DbContext
            {
                public DbSet<Child> Children { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Child>()
                        .HasOne<Parent>()
                        .WithMany()
                        .HasForeignKey(c => new { c.TenantId, c.ParentId });
                }
            }

            public class Child
            {
                public int Id { get; set; }
                public int TenantId { get; set; }
                public int ParentId { get; set; }
            }

            public class Parent
            {
                public int Id { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        var context = Assert.Single(result.Contexts);
        var child = context.Entities.Single(e => e.EntityName == "Child");

        var index = Assert.Single(child.Indexes);
        Assert.Equal(["TenantId", "ParentId"], index.PropertyNames);
        Assert.Equal("IX_Child_TenantId_ParentId", index.Name);
    }

    #endregion

    #region DEF-006: Many-to-Many and SkipNavigations

    [Fact]
    public async Task DEF006_Implicit_ManyToMany_Creates_SkipNavigations_And_Triggers_DB03()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class AppDbContext : DbContext
            {
                public DbSet<Post> Posts { get; set; }
                public DbSet<Tag> Tags { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Post>()
                        .HasMany(p => p.Tags)
                        .WithMany(t => t.Posts);
                }
            }

            public class Post
            {
                public int Id { get; set; }
                public List<Tag> Tags { get; set; } = [];
            }

            public class Tag
            {
                public int Id { get; set; }
                public List<Post> Posts { get; set; } = [];
            }
            """;

        var result = _inspector.InspectSource(source);

        var context = Assert.Single(result.Contexts);
        var post = context.Entities.Single(e => e.EntityName == "Post");
        var tag = context.Entities.Single(e => e.EntityName == "Tag");

        var postSkipNav = Assert.Single(post.SkipNavigations);
        Assert.True(postSkipNav.IsImplicitJoinEntity);
        Assert.Equal("Tag", postSkipNav.TargetEntityName);

        var tagSkipNav = Assert.Single(tag.SkipNavigations);
        Assert.True(tagSkipNav.IsImplicitJoinEntity);
        Assert.Equal("Post", tagSkipNav.TargetEntityName);

        // Проверяем совместимость с правилом DB03
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();
        metadataProvider.Setup(p => p.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                    {
                        EntityName = post.EntityName,
                        TableName = "Posts",
                        Keys = post.Keys,
                        SkipNavigations = post.SkipNavigations
                    },
                    new DatabaseEntityMetadata
                    {
                        EntityName = tag.EntityName,
                        TableName = "Tags",
                        Keys = tag.Keys,
                        SkipNavigations = tag.SkipNavigations
                    }
                ]
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var findings = await analyzer.AnalyzeAsync(new AnalyzerContext { RepositoryPath = "test" });

        // DB03 должно сработать ровно один раз для связи (дедупликация)
        var db03Findings = findings.Where(f => f.Rule == "DB03").ToList();
        Assert.Single(db03Findings);
    }

    [Fact]
    public async Task DEF006_Explicit_ManyToMany_With_UsingEntity_Does_Not_Trigger_DB03()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class AppDbContext : DbContext
            {
                public DbSet<Post> Posts { get; set; }
                public DbSet<Tag> Tags { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Post>()
                        .HasMany(p => p.Tags)
                        .WithMany(t => t.Posts)
                        .UsingEntity<PostTag>();
                }
            }

            public class Post
            {
                public int Id { get; set; }
                public List<Tag> Tags { get; set; } = [];
            }

            public class Tag
            {
                public int Id { get; set; }
                public List<Post> Posts { get; set; } = [];
            }

            public class PostTag
            {
                public int PostId { get; set; }
                public int TagId { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        var context = Assert.Single(result.Contexts);
        var post = context.Entities.Single(e => e.EntityName == "Post");
        var postSkipNav = Assert.Single(post.SkipNavigations);

        Assert.False(postSkipNav.IsImplicitJoinEntity);

        // Проверяем, что DB03 не срабатывает для явной join entity
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();
        metadataProvider.Setup(p => p.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                    {
                        EntityName = post.EntityName,
                        TableName = "Posts",
                        Keys = post.Keys,
                        SkipNavigations = post.SkipNavigations
                    }
                ]
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var findings = await analyzer.AnalyzeAsync(new AnalyzerContext { RepositoryPath = "test" });

        Assert.DoesNotContain(findings, f => f.Rule == "DB03");
    }

    #endregion

    #region SEC-01, SEC-02, SEC-03: Security & Path Normalization

    [Fact]
    public void SEC01_File_Exceeding_2MB_Is_Skipped_With_Warning()
    {
        // Генерируем текст > 2 МБ (2 * 1024 * 1024 + 10 байт)
        var largeText = new string(' ', 2 * 1024 * 1024 + 10);

        var result = _inspector.InspectSources([
            (largeText, "LargeFile.cs")
        ]);

        Assert.Contains(result.Warnings, w => w.Contains("превышает максимально допустимый размер"));
        Assert.Empty(result.Contexts);
    }

    [Fact]
    public async Task SEC01_Cancellation_Token_Is_Respected()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await _inspector.InspectFilesAsync(["NonExistent.cs"], cts.Token);
        });
    }

    [Fact]
    public void SEC03_Warnings_Do_Not_Contain_Server_Absolute_Paths()
    {
        var sourceWithErrors = "invalid csharp code {{{{";
        var absolutePath = @"C:\inetpub\wwwroot\App\Models\BadFile.cs";
        var baseDir = @"C:\inetpub\wwwroot\App";

        var result = _inspector.InspectSources([(sourceWithErrors, absolutePath)], baseDirectory: baseDir);

        var warning = Assert.Single(result.Warnings);
        Assert.DoesNotContain(@"C:\inetpub\wwwroot\App", warning);
        Assert.Contains("Models/BadFile.cs", warning);
    }

    #endregion
}
