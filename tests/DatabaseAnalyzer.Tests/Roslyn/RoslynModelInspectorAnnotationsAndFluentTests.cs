using DatabaseAnalyzer.Roslyn;

namespace DatabaseAnalyzer.Tests.Roslyn;

public static class SecurityExecutionBomb
{
    public static bool ProcessStartExecuted { get; set; }
}

public sealed class RoslynModelInspectorAnnotationsAndFluentTests
{
    private readonly RoslynModelInspector _inspector = new();

    #region Data Annotations Tests (1-9)

    [Fact]
    public void DataAnnotation_Table_SetsCustomTableNameAndSchema()
    {
        var source = """
            using System.ComponentModel.DataAnnotations.Schema;
            using Microsoft.EntityFrameworkCore;

            namespace App.Models;

            public class AppDbContext : DbContext
            {
                public DbSet<User> Users { get; set; }
            }

            [Table("tbl_users", Schema = "custom_auth")]
            public class User
            {
                public int Id { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var context = Assert.Single(result.Contexts);
        var user = Assert.Single(context.Entities);

        Assert.Equal("User", user.EntityName);
        Assert.Equal("tbl_users", user.TableName);
        Assert.Equal("custom_auth", user.SchemaName);

        var metadata = user.ToDatabaseEntityMetadata();
        Assert.Equal("tbl_users", metadata.TableName);
        Assert.Equal("custom_auth", metadata.SchemaName);
    }

    [Fact]
    public void DataAnnotation_Key_SetsPrimaryKey()
    {
        var source = """
            using System.ComponentModel.DataAnnotations;
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                public DbSet<Product> Products { get; set; }
            }

            public class Product
            {
                [Key]
                public string SkuCode { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var product = Assert.Single(result.Contexts.Single().Entities);

        var key = Assert.Single(product.Keys);
        Assert.True(key.IsPrimaryKey);
        Assert.Equal(["SkuCode"], key.PropertyNames);

        var skuProp = Assert.Single(product.Properties, p => p.PropertyName == "SkuCode");
        Assert.True(skuProp.IsPrimaryKey);
    }

    [Fact]
    public void DataAnnotation_Required_SetsIsNullableFalse()
    {
        var source = """
            using System.ComponentModel.DataAnnotations;
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                public DbSet<Article> Articles { get; set; }
            }

            public class Article
            {
                public int Id { get; set; }

                [Required]
                public string Title { get; set; } = null!;

                public string? Subtitle { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var article = Assert.Single(result.Contexts.Single().Entities);

        var titleProp = Assert.Single(article.Properties, p => p.PropertyName == "Title");
        Assert.False(titleProp.IsNullable);

        var subProp = Assert.Single(article.Properties, p => p.PropertyName == "Subtitle");
        Assert.True(subProp.IsNullable);
    }

    [Fact]
    public void DataAnnotation_ForeignKey_ExtractsForeignKeyMetadata()
    {
        var source = """
            using System.ComponentModel.DataAnnotations.Schema;
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                public DbSet<Order> Orders { get; set; }
            }

            public class Order
            {
                public int Id { get; set; }

                public int CustomerId { get; set; }

                [ForeignKey("CustomerId")]
                public Customer Customer { get; set; } = null!;
            }

            public class Customer
            {
                public int Id { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var order = Assert.Single(result.Contexts.Single().Entities);

        var fk = Assert.Single(order.ForeignKeys);
        Assert.Equal("Customer", fk.PrincipalEntityName);
        Assert.Equal(["CustomerId"], fk.PropertyNames);
        Assert.Equal("Customer", fk.NavigationToPrincipal);
    }

    [Fact]
    public void DataAnnotation_Index_ExtractsIndexMetadata()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                public DbSet<Member> Members { get; set; }
            }

            [Index(nameof(Email))]
            public class Member
            {
                public int Id { get; set; }
                public string Email { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var member = Assert.Single(result.Contexts.Single().Entities);

        var index = Assert.Single(member.Indexes);
        Assert.Equal(["Email"], index.PropertyNames);
        Assert.False(index.IsUnique);
    }

    [Fact]
    public void DataAnnotation_Index_IsUnique_ExtractsUniqueIndex()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                public DbSet<Account> Accounts { get; set; }
            }

            [Index(nameof(Username), IsUnique = true)]
            public class Account
            {
                public int Id { get; set; }
                public string Username { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var account = Assert.Single(result.Contexts.Single().Entities);

        var index = Assert.Single(account.Indexes);
        Assert.Equal(["Username"], index.PropertyNames);
        Assert.True(index.IsUnique);
    }

    [Fact]
    public void DataAnnotation_Index_MultipleProperties_ExtractsCompositeIndex()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                public DbSet<Person> People { get; set; }
            }

            [Index(nameof(LastName), nameof(FirstName), Name = "IX_Person_FullName", IsUnique = true)]
            public class Person
            {
                public int Id { get; set; }
                public string FirstName { get; set; } = null!;
                public string LastName { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var person = Assert.Single(result.Contexts.Single().Entities);

        var index = Assert.Single(person.Indexes);
        Assert.Equal(["LastName", "FirstName"], index.PropertyNames);
        Assert.Equal("IX_Person_FullName", index.Name);
        Assert.True(index.IsUnique);
    }

    [Fact]
    public void DataAnnotation_FullyQualifiedAttributes_DiscoveredWithoutUsing()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                public DbSet<Document> Documents { get; set; }
            }

            [System.ComponentModel.DataAnnotations.Schema.Table("docs", Schema = "archive")]
            [Microsoft.EntityFrameworkCore.Index(nameof(DocNumber), IsUnique = true)]
            public class Document
            {
                [System.ComponentModel.DataAnnotations.Key]
                public int DocId { get; set; }

                public string DocNumber { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var doc = Assert.Single(result.Contexts.Single().Entities);

        Assert.Equal("docs", doc.TableName);
        Assert.Equal("archive", doc.SchemaName);

        var pk = Assert.Single(doc.Keys);
        Assert.Equal(["DocId"], pk.PropertyNames);
        Assert.True(pk.IsPrimaryKey);

        var index = Assert.Single(doc.Indexes);
        Assert.Equal(["DocNumber"], index.PropertyNames);
        Assert.True(index.IsUnique);
    }

    [Fact]
    public void DataAnnotation_UnknownAttributeWithMatchingShortName_IsIgnoredAndWarned()
    {
        // Неизвестный [Index] из библиотеки сторонних атрибутов без using Microsoft.EntityFrameworkCore
        var source = """
            using CustomIndexingLibrary;

            public class AppDbContext : Microsoft.EntityFrameworkCore.DbContext
            {
                public Microsoft.EntityFrameworkCore.DbSet<Item> Items { get; set; }
            }

            [Index("Field")]
            public class Item
            {
                public int Id { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        // Должно быть сформировано предупреждение о пропуске атрибута
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("не подтверждена принадлежность"));

        var item = Assert.Single(result.Contexts.Single().Entities);
        Assert.Empty(item.Indexes);
    }

    #endregion

    #region Fluent API Tests (10-28)

    [Fact]
    public void FluentApi_EntityWithoutDbSet_IsDiscovered()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Category>().ToTable("Categories");
                }
            }

            public class Category
            {
                public int Id { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var context = Assert.Single(result.Contexts);
        var category = Assert.Single(context.Entities);

        Assert.Equal("Category", category.EntityName);
        Assert.Equal("Categories", category.TableName);
    }

    [Fact]
    public void FluentApi_HasKey_SingleProperty()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                public DbSet<Employee> Employees { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Employee>().HasKey(e => e.EmployeeId);
                }
            }

            public class Employee
            {
                public int EmployeeId { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var employee = Assert.Single(result.Contexts.Single().Entities);

        var key = Assert.Single(employee.Keys);
        Assert.True(key.IsPrimaryKey);
        Assert.Equal(["EmployeeId"], key.PropertyNames);
    }

    [Fact]
    public void FluentApi_HasKey_Composite()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                public DbSet<OrderItem> Items { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<OrderItem>()
                        .HasKey(oi => new { oi.OrderId, oi.ProductId });
                }
            }

            public class OrderItem
            {
                public int OrderId { get; set; }
                public int ProductId { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var item = Assert.Single(result.Contexts.Single().Entities);

        var key = Assert.Single(item.Keys);
        Assert.True(key.IsPrimaryKey);
        Assert.Equal(["OrderId", "ProductId"], key.PropertyNames);
    }

    [Fact]
    public void FluentApi_HasIndex_SingleProperty()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                public DbSet<Customer> Customers { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Customer>().HasIndex(c => c.Email);
                }
            }

            public class Customer
            {
                public int Id { get; set; }
                public string Email { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var customer = Assert.Single(result.Contexts.Single().Entities);

        var index = Assert.Single(customer.Indexes);
        Assert.Equal(["Email"], index.PropertyNames);
        Assert.False(index.IsUnique);
    }

    [Fact]
    public void FluentApi_HasIndex_Composite()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                public DbSet<Book> Books { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Book>()
                        .HasIndex(b => new { b.AuthorId, b.PublishedYear });
                }
            }

            public class Book
            {
                public int Id { get; set; }
                public int AuthorId { get; set; }
                public int PublishedYear { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var book = Assert.Single(result.Contexts.Single().Entities);

        var index = Assert.Single(book.Indexes);
        Assert.Equal(["AuthorId", "PublishedYear"], index.PropertyNames);
    }

    [Fact]
    public void FluentApi_IsUnique_SetsUniqueFlag()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                public DbSet<User> Users { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<User>()
                        .HasIndex(u => u.Username)
                        .IsUnique();
                }
            }

            public class User
            {
                public int Id { get; set; }
                public string Username { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var user = Assert.Single(result.Contexts.Single().Entities);

        var index = Assert.Single(user.Indexes);
        Assert.Equal(["Username"], index.PropertyNames);
        Assert.True(index.IsUnique);
    }

    [Fact]
    public void FluentApi_HasDatabaseName_SetsCustomName()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                public DbSet<User> Users { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<User>()
                        .HasIndex(u => u.Email)
                        .HasDatabaseName("IX_Custom_User_Email");
                }
            }

            public class User
            {
                public int Id { get; set; }
                public string Email { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var user = Assert.Single(result.Contexts.Single().Entities);

        var index = Assert.Single(user.Indexes);
        Assert.Equal("IX_Custom_User_Email", index.Name);
    }

    [Fact]
    public void FluentApi_HasFilter_SetsFilterPredicate()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                public DbSet<User> Users { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<User>()
                        .HasIndex(u => u.Email)
                        .HasFilter("\"IsDeleted\" = 0");
                }
            }

            public class User
            {
                public int Id { get; set; }
                public string Email { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var user = Assert.Single(result.Contexts.Single().Entities);

        var index = Assert.Single(user.Indexes);
        Assert.Equal("\"IsDeleted\" = 0", index.Filter);
    }

    [Fact]
    public void FluentApi_ToTable_SetsCustomTableNameAndSchema()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                public DbSet<Invoice> Invoices { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Invoice>().ToTable("t_invoices", "fin");
                }
            }

            public class Invoice
            {
                public int Id { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var invoice = Assert.Single(result.Contexts.Single().Entities);

        Assert.Equal("t_invoices", invoice.TableName);
        Assert.Equal("fin", invoice.SchemaName);
    }

    [Fact]
    public void FluentApi_Relationship_OneToMany()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                public DbSet<User> Users { get; set; }
                public DbSet<Order> Orders { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>()
                        .HasOne(o => o.User)
                        .WithMany(u => u.Orders)
                        .HasForeignKey(o => o.UserId);
                }
            }

            public class User
            {
                public int Id { get; set; }
                public List<Order> Orders { get; set; } = [];
            }

            public class Order
            {
                public int Id { get; set; }
                public int UserId { get; set; }
                public User User { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var order = Assert.Single(result.Contexts.Single().Entities, e => e.EntityName == "Order");

        var fk = Assert.Single(order.ForeignKeys);
        Assert.Equal("User", fk.PrincipalEntityName);
        Assert.Equal(["UserId"], fk.PropertyNames);
        Assert.Equal("User", fk.NavigationToPrincipal);
        Assert.Equal("Orders", fk.NavigationToDependent);
    }

    [Fact]
    public void FluentApi_Relationship_OneToOne()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                public DbSet<User> Users { get; set; }
                public DbSet<Profile> Profiles { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Profile>()
                        .HasOne(p => p.User)
                        .WithOne(u => u.Profile)
                        .HasForeignKey<Profile>(p => p.UserId);
                }
            }

            public class User
            {
                public int Id { get; set; }
                public Profile? Profile { get; set; }
            }

            public class Profile
            {
                public int Id { get; set; }
                public int UserId { get; set; }
                public User User { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var profile = Assert.Single(result.Contexts.Single().Entities, e => e.EntityName == "Profile");

        var fk = Assert.Single(profile.ForeignKeys);
        Assert.Equal("User", fk.PrincipalEntityName);
        Assert.Equal(["UserId"], fk.PropertyNames);
        Assert.Equal("User", fk.NavigationToPrincipal);
        Assert.Equal("Profile", fk.NavigationToDependent);
    }

    [Fact]
    public void FluentApi_MultilineFormattingAndChaining_ParsedCorrectly()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                public DbSet<Transaction> Transactions { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Transaction>()
                        .ToTable("tx_records", "ledger")
                        .HasKey(t => new { t.TenantId, t.TxId });

                    modelBuilder.Entity<Transaction>()
                        .HasIndex(t => t.Timestamp)
                        .IsUnique()
                        .HasDatabaseName("IX_Ledger_Timestamp")
                        .HasFilter("\"Status\" = 1");
                }
            }

            public class Transaction
            {
                public int TenantId { get; set; }
                public long TxId { get; set; }
                public long Timestamp { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var tx = Assert.Single(result.Contexts.Single().Entities);

        Assert.Equal("tx_records", tx.TableName);
        Assert.Equal("ledger", tx.SchemaName);

        var key = Assert.Single(tx.Keys);
        Assert.Equal(["TenantId", "TxId"], key.PropertyNames);

        var index = Assert.Single(tx.Indexes);
        Assert.Equal("IX_Ledger_Timestamp", index.Name);
        Assert.Equal(["Timestamp"], index.PropertyNames);
        Assert.True(index.IsUnique);
        Assert.Equal("\"Status\" = 1", index.Filter);
    }

    [Fact]
    public void FluentApi_MultipleEntitiesInSingleContext_AllConfigured()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                public DbSet<User> Users { get; set; }
                public DbSet<Role> Roles { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<User>(b =>
                    {
                        b.ToTable("Users");
                        b.HasKey(u => u.Id);
                        b.HasIndex(u => u.Email).IsUnique();
                    });

                    modelBuilder.Entity<Role>(b =>
                    {
                        b.ToTable("Roles");
                        b.HasKey(r => r.RoleId);
                    });
                }
            }

            public class User
            {
                public int Id { get; set; }
                public string Email { get; set; } = null!;
            }

            public class Role
            {
                public int RoleId { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var context = Assert.Single(result.Contexts);

        var user = Assert.Single(context.Entities, e => e.EntityName == "User");
        Assert.Equal("Users", user.TableName);
        Assert.Equal(["Id"], Assert.Single(user.Keys).PropertyNames);
        Assert.True(Assert.Single(user.Indexes).IsUnique);

        var role = Assert.Single(context.Entities, e => e.EntityName == "Role");
        Assert.Equal("Roles", role.TableName);
        Assert.Equal(["RoleId"], Assert.Single(role.Keys).PropertyNames);
    }

    [Fact]
    public void FluentApi_MultipleContexts_ConfigurationsNotMixed()
    {
        var salesSource = """
            using Microsoft.EntityFrameworkCore;

            namespace Sales;

            public class SalesContext : DbContext
            {
                public DbSet<Order> Orders { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>().ToTable("sales_orders").HasKey(o => o.OrderId);
                }
            }

            public class Order
            {
                public int OrderId { get; set; }
            }
            """;

        var finSource = """
            using Microsoft.EntityFrameworkCore;

            namespace Finance;

            public class FinanceContext : DbContext
            {
                public DbSet<Invoice> Invoices { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Invoice>().ToTable("fin_invoices").HasKey(i => i.InvoiceNumber);
                }
            }

            public class Invoice
            {
                public string InvoiceNumber { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSources([
            (salesSource, "SalesContext.cs"),
            (finSource, "FinanceContext.cs")
        ]);

        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Contexts.Count);

        var sales = Assert.Single(result.Contexts, c => c.Name == "SalesContext");
        var salesOrder = Assert.Single(sales.Entities);
        Assert.Equal("sales_orders", salesOrder.TableName);
        Assert.Equal(["OrderId"], Assert.Single(salesOrder.Keys).PropertyNames);

        var fin = Assert.Single(result.Contexts, c => c.Name == "FinanceContext");
        var finInvoice = Assert.Single(fin.Entities);
        Assert.Equal("fin_invoices", finInvoice.TableName);
        Assert.Equal(["InvoiceNumber"], Assert.Single(finInvoice.Keys).PropertyNames);
    }

    [Fact]
    public void FluentApi_LocalConfigurationMethod_TraversedAndConfigured()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                public DbSet<Product> Products { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    ConfigureProducts(modelBuilder);
                }

                private void ConfigureProducts(ModelBuilder mb)
                {
                    mb.Entity<Product>().ToTable("catalog_products").HasKey(p => p.Code);
                }
            }

            public class Product
            {
                public string Code { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Warnings);
        var product = Assert.Single(result.Contexts.Single().Entities);

        Assert.Equal("catalog_products", product.TableName);
        Assert.Equal(["Code"], Assert.Single(product.Keys).PropertyNames);
    }

    [Fact]
    public void FluentApi_CyclicLocalMethods_DetectedAndWarnedWithoutHanging()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                public DbSet<Node> Nodes { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    MethodA(modelBuilder);
                }

                private void MethodA(ModelBuilder mb)
                {
                    MethodB(mb);
                }

                private void MethodB(ModelBuilder mb)
                {
                    MethodA(mb);
                }
            }

            public class Node
            {
                public int Id { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        // Обнаружен циклический вызов -> зафиксировано предупреждение, зацикливания нет
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("циклический вызов"));
    }

    [Fact]
    public void FluentApi_MaxDepthExceeded_StopsAndWarns()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            public class AppDbContext : DbContext
            {
                public DbSet<LevelItem> Items { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    Level1(modelBuilder);
                }

                private void Level1(ModelBuilder mb) => Level2(mb);
                private void Level2(ModelBuilder mb) => Level3(mb);
                private void Level3(ModelBuilder mb) => Level4(mb);
                private void Level4(ModelBuilder mb)
                {
                    mb.Entity<LevelItem>().ToTable("deep_items");
                }
            }

            public class LevelItem
            {
                public int Id { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        // Превышена глубина 3 -> зафиксировано предупреждение
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("Превышена максимальная глубина обхода"));
    }

    [Fact]
    public void FluentApi_UnknownMethodNamedHasKey_NotTreatedAsEfCore()
    {
        var source = """
            using System;

            public class Service
            {
                public void HasKey(string name)
                {
                    Console.WriteLine(name);
                }
            }

            public class Runner
            {
                public void Run(Service service)
                {
                    service.HasKey("TestKey");
                }
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Empty(result.Contexts);
    }

    [Fact]
    public void Resilience_SyntaxErrorInOneFile_DoesNotHaltOtherFiles()
    {
        var brokenSource = """
            public class BrokenSyntax {
                !! unclosed bracket error
            """;

        var validSource = """
            using Microsoft.EntityFrameworkCore;

            public class ValidContext : DbContext
            {
                public DbSet<Device> Devices { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Device>().ToTable("hardware_devices").HasKey(d => d.SerialNumber);
                }
            }

            public class Device
            {
                public string SerialNumber { get; set; } = null!;
            }
            """;

        var result = _inspector.InspectSources([
            (brokenSource, "Broken.cs"),
            (validSource, "Valid.cs")
        ]);

        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("Broken.cs"));

        var context = Assert.Single(result.Contexts);
        Assert.Equal("ValidContext", context.Name);

        var device = Assert.Single(context.Entities);
        Assert.Equal("hardware_devices", device.TableName);
        Assert.Equal(["SerialNumber"], Assert.Single(device.Keys).PropertyNames);
    }

    [Fact]
    public void FluentApi_IEntityTypeConfiguration_AppliedStatically()
    {
        var contextSource = """
            using Microsoft.EntityFrameworkCore;
            using Store.Configurations;

            namespace Store;

            public class StoreDbContext : DbContext
            {
                public DbSet<Customer> Customers { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfiguration(new CustomerConfiguration());
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

            namespace Store.Configurations;

            public class CustomerConfiguration : IEntityTypeConfiguration<Customer>
            {
                public void Configure(EntityTypeBuilder<Customer> builder)
                {
                    builder.ToTable("store_customers");
                    builder.HasKey(c => c.Id);
                    builder.HasIndex(c => c.Id).HasDatabaseName("IX_Customer_Id");
                }
            }
            """;

        var result = _inspector.InspectSources([
            (contextSource, "StoreDbContext.cs"),
            (configSource, "CustomerConfiguration.cs")
        ]);

        Assert.Empty(result.Warnings);
        var context = Assert.Single(result.Contexts);
        var customer = Assert.Single(context.Entities);

        Assert.Equal("store_customers", customer.TableName);
        Assert.Equal(["Id"], Assert.Single(customer.Keys).PropertyNames);
        Assert.Equal("IX_Customer_Id", Assert.Single(customer.Indexes).Name);
    }

    #endregion

    #region Security Tests (29-31)

    [Fact]
    public void Security_StaticConstructorWithMarkerFile_IsNotExecuted()
    {
        var sentinelFile = Path.Combine(Path.GetTempPath(), "rce_bomb_test_" + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            var maliciousSource = $$"""
                using Microsoft.EntityFrameworkCore;
                using System.IO;

                public class ExploitContext : DbContext
                {
                    static ExploitContext()
                    {
                        File.WriteAllText(@"{{sentinelFile}}", "EXPLOITED");
                    }

                    public DbSet<ExploitUser> Users { get; set; }

                    protected override void OnModelCreating(ModelBuilder modelBuilder)
                    {
                        modelBuilder.Entity<ExploitUser>().ToTable("users");
                    }
                }

                public class ExploitUser
                {
                    public int Id { get; set; }
                }
                """;

            var result = _inspector.InspectSource(maliciousSource);

            Assert.Single(result.Contexts);
            Assert.False(File.Exists(sentinelFile), "Файл-меткер НЕ должен быть создан, так как код внутри исследуемого проекта не исполнялся!");
        }
        finally
        {
            if (File.Exists(sentinelFile))
            {
                File.Delete(sentinelFile);
            }
        }
    }

    [Fact]
    public void Security_CodeWithProcessStart_IsNotExecuted()
    {
        SecurityExecutionBomb.ProcessStartExecuted = false;

        var source = """
            using Microsoft.EntityFrameworkCore;
            using System.Diagnostics;

            public class BombContext : DbContext
            {
                public DbSet<Victim> Victims { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    DatabaseAnalyzer.Tests.Roslyn.SecurityExecutionBomb.ProcessStartExecuted = true;
                    Process.Start("calc.exe");
                    modelBuilder.Entity<Victim>().ToTable("victims");
                }
            }

            public class Victim
            {
                public int Id { get; set; }
            }
            """;

        var result = _inspector.InspectSource(source);

        Assert.Single(result.Contexts);
        Assert.False(SecurityExecutionBomb.ProcessStartExecuted, "Метод OnModelCreating исследуемого кода НЕ должен вызываться в процессе хоста!");
    }

    [Fact]
    public void Security_AbsenceOfAssemblyLoadingMechanismsInParser()
    {
        // Проверяем, что парсер является чисто синтаксическим и не использует Assembly.LoadFrom
        var inspectorType = typeof(RoslynModelInspector);
        var methods = inspectorType.GetMethods();

        Assert.NotEmpty(methods);
        // Ни один открытый метод не возвращает Assembly или Type пользовательского кода
        Assert.DoesNotContain(methods, m => m.ReturnType.Name.Contains("Assembly"));
    }

    #endregion
}
