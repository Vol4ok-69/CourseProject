using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Npgsql.EntityFrameworkCore.PostgreSQL;

namespace Infrastructure.Data.Configurations;

public class CommitConfiguration : IEntityTypeConfiguration<Commit>
{
    public void Configure(EntityTypeBuilder<Commit> builder)
    {
        builder.HasKey(commit => commit.Id);

        builder.Property(commit => commit.Id)
            .UseIdentityByDefaultColumn();

        builder.Property(commit => commit.CommitHash)
            .IsRequired();

        builder.Property(commit => commit.CommitDate)
            .IsRequired();

        builder.HasOne(commit => commit.Project)
            .WithMany(project => project.Commits)
            .HasForeignKey(commit => commit.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(commit => commit.Analyses)
            .WithOne(analysis => analysis.Commit)
            .HasForeignKey(analysis => analysis.CommitId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}