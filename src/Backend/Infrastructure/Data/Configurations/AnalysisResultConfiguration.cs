using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Npgsql.EntityFrameworkCore.PostgreSQL;

namespace Infrastructure.Data.Configurations;

public class AnalysisResultConfiguration : IEntityTypeConfiguration<AnalysisResult>
{
    public void Configure(EntityTypeBuilder<AnalysisResult> builder)
    {
        builder.HasKey(result => result.Id);

        builder.Property(result => result.Id)
            .UseIdentityByDefaultColumn();

        builder.Property(result => result.FilePath)
            .IsRequired();

        builder.Property(result => result.LineNumber)
            .IsRequired();

        builder.Property(result => result.Rule)
            .IsRequired();

        builder.Property(result => result.Message)
            .IsRequired();

        builder.Property(result => result.Severity)
            .IsRequired();

        builder.Property(result => result.Recommendation)
            .IsRequired(false);

        builder.Property(result => result.AnalyzerType)
            .IsRequired();

        builder.HasOne(result => result.Analysis)
            .WithMany(analysis => analysis.Results)
            .HasForeignKey(result => result.AnalysisId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}