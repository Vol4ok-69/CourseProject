using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Npgsql.EntityFrameworkCore.PostgreSQL;

namespace Infrastructure.Data.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasKey(user => user.Id);

        builder.Property(user => user.Id)
            .UseIdentityByDefaultColumn();

        builder.Property(user => user.Username)
            .IsRequired();

        builder.HasIndex(user => user.Username)
            .IsUnique();

        builder.Property(user => user.PasswordHash)
            .IsRequired();

        builder.Property(user => user.EncryptedGitHubToken)
            .IsRequired(false);

        builder.Property(user => user.GitHubUsername)
            .HasMaxLength(255)
            .IsRequired(false);

        builder.Property(user => user.GitHubTokenCreatedAt)
            .IsRequired(false);

        builder.HasOne(user => user.Role)
            .WithMany(role => role.Users)
            .HasForeignKey(user => user.RoleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(user => user.Projects)
            .WithOne(project => project.Owner)
            .HasForeignKey(project => project.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
