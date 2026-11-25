using System.ComponentModel.DataAnnotations;
using Application.DTOs;
using Application.Interfaces.Repositories;
using Application.Services;
using Domain.Entities;
using Moq;
using Xunit;

namespace Backend.Tests.Unit;

public sealed class CommitServiceTests
{
    [Fact]
    public void CreateCommitDto_MissingCommitDate_FailsValidation()
    {
        var dto = new CreateCommitDto
        {
            CommitHash = "abcdef1234567",
            CommitDate = null
        };

        var results = ValidateDto(dto);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(CreateCommitDto.CommitDate)));
    }

    [Fact]
    public void CreateCommitDto_UnspecifiedCommitDateKind_FailsValidation()
    {
        var dto = new CreateCommitDto
        {
            CommitHash = "abcdef1234567",
            CommitDate = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Unspecified)
        };

        var results = ValidateDto(dto);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(CreateCommitDto.CommitDate)));
    }

    [Fact]
    public async Task CreateAsync_LocalCommitDate_NormalizesToUtcBeforeSaving()
    {
        var commitRepo = new Mock<ICommitRepository>();
        var projectRepo = new Mock<IProjectRepository>();
        projectRepo.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 1, Name = "P", RepoUrl = "https://github.com/a/b", OwnerId = 1 });

        Commit? savedCommit = null;
        commitRepo.Setup(x => x.AddAsync(It.IsAny<Commit>(), It.IsAny<CancellationToken>()))
            .Callback<Commit, CancellationToken>((c, _) => savedCommit = c)
            .Returns(Task.CompletedTask);

        var service = new CommitService(commitRepo.Object, projectRepo.Object);
        var localDate = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);

        var result = await service.CreateAsync(1, new CreateCommitDto
        {
            CommitHash = "abcdef1234567",
            CommitDate = localDate
        });

        Assert.NotNull(result);
        Assert.NotNull(savedCommit);
        Assert.Equal(DateTimeKind.Utc, savedCommit.CommitDate.Kind);
        Assert.Equal(localDate.ToUniversalTime(), savedCommit.CommitDate);
    }

    [Theory]
    [InlineData("abcdef1234567", "abcdef1234567")]
    [InlineData("ABCDEF1234567", "abcdef1234567")]
    [InlineData("AbCdEf1234567", "abcdef1234567")]
    public async Task CreateAsync_NormalizesCommitHashToLowercaseBeforeLookupAndSave(string inputHash, string expectedHash)
    {
        var commitRepo = new Mock<ICommitRepository>();
        var projectRepo = new Mock<IProjectRepository>();
        projectRepo.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = 1, Name = "P", RepoUrl = "https://github.com/a/b", OwnerId = 1 });

        Commit? savedCommit = null;
        commitRepo.Setup(x => x.AddAsync(It.IsAny<Commit>(), It.IsAny<CancellationToken>()))
            .Callback<Commit, CancellationToken>((c, _) => savedCommit = c)
            .Returns(Task.CompletedTask);

        var service = new CommitService(commitRepo.Object, projectRepo.Object);

        var result = await service.CreateAsync(1, new CreateCommitDto
        {
            CommitHash = inputHash,
            CommitDate = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
        });

        Assert.NotNull(result);
        Assert.NotNull(savedCommit);
        Assert.Equal(expectedHash, savedCommit.CommitHash);
        commitRepo.Verify(x => x.GetByHashAsync(1, expectedHash, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static List<ValidationResult> ValidateDto(object dto)
    {
        var context = new ValidationContext(dto);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(dto, context, results, validateAllProperties: true);
        return results;
    }
}
