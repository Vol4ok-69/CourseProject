using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.Tests;

public sealed class AppDbContextQueueModelTests
{
    [Fact]
    public void AppDbContext_Model_ContainsQueuePropertiesAndIndexes()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var context = new AppDbContext(options);
        var model = context.Model;

        var entity = model.FindEntityType(typeof(Analysis));
        Assert.NotNull(entity);

        // CreatedAt (non-nullable)
        var createdAtProp = entity.FindProperty(nameof(Analysis.CreatedAt));
        Assert.NotNull(createdAtProp);
        Assert.False(createdAtProp.IsNullable);

        // StartedAt (nullable)
        var startedAtProp = entity.FindProperty(nameof(Analysis.StartedAt));
        Assert.NotNull(startedAtProp);
        Assert.True(startedAtProp.IsNullable);

        // CompletedAt (nullable)
        var completedAtProp = entity.FindProperty(nameof(Analysis.CompletedAt));
        Assert.NotNull(completedAtProp);
        Assert.True(completedAtProp.IsNullable);

        // LeaseExpiration (nullable)
        var leaseExpirationProp = entity.FindProperty(nameof(Analysis.LeaseExpiration));
        Assert.NotNull(leaseExpirationProp);
        Assert.True(leaseExpirationProp.IsNullable);

        // ExecutionToken (nullable)
        var executionTokenProp = entity.FindProperty(nameof(Analysis.ExecutionToken));
        Assert.NotNull(executionTokenProp);
        Assert.True(executionTokenProp.IsNullable);

        // Indexes
        var indexes = entity.GetIndexes().ToList();

        // 1. IX_Analyses_Status_CreatedAt
        var statusCreatedAtIndex = indexes.SingleOrDefault(i =>
            i.Properties.Select(p => p.Name).SequenceEqual([nameof(Analysis.Status), nameof(Analysis.CreatedAt)]));
        Assert.NotNull(statusCreatedAtIndex);
        Assert.Equal("IX_Analyses_Status_CreatedAt", statusCreatedAtIndex.GetDatabaseName());

        // 2. IX_Analyses_CommitId_CreatedAt
        var commitIdCreatedAtIndex = indexes.SingleOrDefault(i =>
            i.Properties.Select(p => p.Name).SequenceEqual([nameof(Analysis.CommitId), nameof(Analysis.CreatedAt)]));
        Assert.NotNull(commitIdCreatedAtIndex);
        Assert.Equal("IX_Analyses_CommitId_CreatedAt", commitIdCreatedAtIndex.GetDatabaseName());

        // Проверяем отсутствие дублирующего индекса только на CommitId
        var duplicateCommitIdIndex = indexes.SingleOrDefault(i =>
            i.Properties.Select(p => p.Name).SequenceEqual([nameof(Analysis.CommitId)]));
        Assert.Null(duplicateCommitIdIndex);
    }
}
