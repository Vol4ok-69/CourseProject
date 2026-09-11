using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Npgsql.EntityFrameworkCore.PostgreSQL;

namespace Infrastructure.Data.Configurations;

public class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.HasKey(project => project.Id);

        builder.Property(project => project.Id)
            .UseIdentityByDefaultColumn();

        builder.Property(project => project.Name)
            .IsRequired();

        builder.Property(project => project.RepoUrl)
            .IsRequired();

        builder.HasOne(project => project.Owner)
            .WithMany(user => user.Projects)
            .HasForeignKey(project => project.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(project => project.Commits)
            .WithOne(commit => commit.Project)
            .HasForeignKey(commit => commit.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}