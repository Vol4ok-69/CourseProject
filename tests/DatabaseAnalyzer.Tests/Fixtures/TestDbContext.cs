using Microsoft.EntityFrameworkCore;

namespace DatabaseAnalyzer.Tests.Fixtures;

public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<TestUser> Users => Set<TestUser>();
    public DbSet<TestOrder> Orders => Set<TestOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TestUser>(entity =>
        {
            entity.ToTable("Users");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id)
                .ValueGeneratedOnAdd();

            entity.Property(x => x.Name)
                .HasMaxLength(100)
                .IsRequired();
        });

        modelBuilder.Entity<TestOrder>(entity =>
        {
            entity.ToTable("Orders");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id)
                .ValueGeneratedOnAdd();

            entity.Property(x => x.UserId)
                .IsRequired();

            entity.HasOne(x => x.User)
                .WithMany(x => x.Orders)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class TestUser
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public ICollection<TestOrder> Orders { get; set; } = [];
}

public sealed class TestOrder
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public TestUser User { get; set; } = null!;
}
