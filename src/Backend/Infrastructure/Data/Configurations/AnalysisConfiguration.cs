using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Npgsql.EntityFrameworkCore.PostgreSQL;

namespace Infrastructure.Data.Configurations;

public class AnalysisConfiguration : IEntityTypeConfiguration<Analysis>
{
    public void Configure(EntityTypeBuilder<Analysis> builder)
    {
        builder.HasKey(analysis => analysis.Id);

        builder.Property(analysis => analysis.Id)
            .UseIdentityByDefaultColumn();

        builder.Property(analysis => analysis.Status)
            .IsRequired();

        builder.Property(analysis => analysis.StartedAt)
            .IsRequired();

        builder.Property(analysis => analysis.CompletedAt)
            .IsRequired(false);

        builder.HasOne(analysis => analysis.Commit)
            .WithMany(commit => commit.Analyses)
            .HasForeignKey(analysis => analysis.CommitId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(analysis => analysis.Results)
            .WithOne(result => result.Analysis)
            .HasForeignKey(result => result.AnalysisId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}