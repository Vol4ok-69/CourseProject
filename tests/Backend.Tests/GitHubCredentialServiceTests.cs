using Application.Exceptions;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Domain.Entities;
using Infrastructure.GitHub;
using Infrastructure.Security;
using Moq;
using System.Security.Cryptography;
using Xunit;

namespace Backend.Tests;

public sealed class GitHubCredentialServiceTests
{
    private static readonly byte[] TestEncryptionKey = new byte[32]
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32
    };

    private readonly ITokenEncryptionService _encryptionService = new AesGcmTokenEncryptionService(TestEncryptionKey);

    [Fact]
    public async Task SetUserTokenAsync_ValidPat_VerifiesTokenBeforeSaving_EncryptsAndSaves()
    {
        const string PlainPat = "ghp_valid_test_token_1234567890abcdef";
        const string GitHubLogin = "test_dev_user";
        var user = new User { Id = 1, Username = "ivan" };

        var userRepoMock = new Mock<IUserRepository>();
        userRepoMock.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        var gitHubMock = new Mock<IGitHubService>();
        gitHubMock.Setup(g => g.VerifyTokenAndGetUsernameAsync(PlainPat, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GitHubLogin);

        var service = new GitHubCredentialService(userRepoMock.Object, _encryptionService, gitHubMock.Object);

        var result = await service.SetUserTokenAsync(1, PlainPat);

        Assert.NotNull(result);
        Assert.True(result.IsConnected);
        Assert.Equal(GitHubLogin, result.GitHubUsername);
        Assert.NotNull(result.ConnectedAt);

        // Проверяем, что в базу сохранен зашифрованный токен, а не открытый текст
        Assert.NotNull(user.EncryptedGitHubToken);
        Assert.NotEqual(PlainPat, user.EncryptedGitHubToken);
        Assert.Equal(GitHubLogin, user.GitHubUsername);
        Assert.NotNull(user.GitHubTokenCreatedAt);

        // Проверяем, что зашифрованный токен расшифровывается обратно в исходный
        var decrypted = _encryptionService.Decrypt(user.EncryptedGitHubToken);
        Assert.Equal(PlainPat, decrypted);

        userRepoMock.Verify(r => r.UpdateAsync(user, It.IsAny<CancellationToken>()), Times.Once);
        gitHubMock.Verify(g => g.VerifyTokenAndGetUsernameAsync(PlainPat, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetUserTokenAsync_InvalidPat_ThrowsTokenExpired_AndPreservesExistingToken()
    {
        const string InvalidPat = "ghp_revoked_or_invalid_token";
        const string OldEncryptedToken = "existing_encrypted_token_container";
        const string OldLogin = "old_user";
        var oldCreatedAt = DateTime.UtcNow.AddDays(-10);

        var user = new User
        {
            Id = 2,
            Username = "petr",
            EncryptedGitHubToken = OldEncryptedToken,
            GitHubUsername = OldLogin,
            GitHubTokenCreatedAt = oldCreatedAt
        };

        var userRepoMock = new Mock<IUserRepository>();
        userRepoMock.Setup(r => r.GetByIdAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        var gitHubMock = new Mock<IGitHubService>();
        gitHubMock.Setup(g => g.VerifyTokenAndGetUsernameAsync(InvalidPat, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.TokenExpired, "Персональный токен GitHub недействителен или отозван."));

        var service = new GitHubCredentialService(userRepoMock.Object, _encryptionService, gitHubMock.Object);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.SetUserTokenAsync(2, InvalidPat));

        Assert.Equal(GitHubErrorKind.TokenExpired, ex.Kind);

        // Проверяем, что старый токен остался нетронутым и обновление в репозитории не вызывалось
        Assert.Equal(OldEncryptedToken, user.EncryptedGitHubToken);
        Assert.Equal(OldLogin, user.GitHubUsername);
        Assert.Equal(oldCreatedAt, user.GitHubTokenCreatedAt);
        userRepoMock.Verify(r => r.UpdateAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(GitHubErrorKind.AccessDenied)]
    [InlineData(GitHubErrorKind.RateLimited)]
    [InlineData(GitHubErrorKind.ServiceUnavailable)]
    public async Task SetUserTokenAsync_GitHubErrors_PreserveExistingToken(GitHubErrorKind errorKind)
    {
        const string PlainPat = "ghp_some_token";
        var user = new User
        {
            Id = 3,
            Username = "alex",
            EncryptedGitHubToken = "old_token",
            GitHubUsername = "alex_gh",
            GitHubTokenCreatedAt = DateTime.UtcNow.AddDays(-5)
        };

        var userRepoMock = new Mock<IUserRepository>();
        userRepoMock.Setup(r => r.GetByIdAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        var gitHubMock = new Mock<IGitHubService>();
        gitHubMock.Setup(g => g.VerifyTokenAndGetUsernameAsync(PlainPat, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(errorKind, "GitHub error"));

        var service = new GitHubCredentialService(userRepoMock.Object, _encryptionService, gitHubMock.Object);

        await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.SetUserTokenAsync(3, PlainPat));

        Assert.Equal("old_token", user.EncryptedGitHubToken);
        userRepoMock.Verify(r => r.UpdateAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetIntegrationStatusAsync_ConnectedUser_ReturnsStatus_WithoutCallingGitHubService()
    {
        var createdAt = DateTime.UtcNow.AddDays(-2);
        var user = new User
        {
            Id = 4,
            Username = "anna",
            EncryptedGitHubToken = _encryptionService.Encrypt("ghp_user_token"),
            GitHubUsername = "anna_github",
            GitHubTokenCreatedAt = createdAt
        };

        var userRepoMock = new Mock<IUserRepository>();
        userRepoMock.Setup(r => r.GetByIdAsync(4, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        var gitHubMock = new Mock<IGitHubService>();
        var service = new GitHubCredentialService(userRepoMock.Object, _encryptionService, gitHubMock.Object);

        var status = await service.GetIntegrationStatusAsync(4);

        Assert.NotNull(status);
        Assert.True(status.IsConnected);
        Assert.Equal("anna_github", status.GitHubUsername);
        Assert.Equal(createdAt, status.ConnectedAt);

        // При получении статуса обращение к GitHub API категорически запрещено
        gitHubMock.Verify(g => g.VerifyTokenAndGetUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        gitHubMock.Verify(g => g.GetCommitsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetIntegrationStatusAsync_DisconnectedUser_ReturnsDisconnected()
    {
        var user = new User
        {
            Id = 5,
            Username = "sergey",
            EncryptedGitHubToken = null,
            GitHubUsername = null,
            GitHubTokenCreatedAt = null
        };

        var userRepoMock = new Mock<IUserRepository>();
        userRepoMock.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        var gitHubMock = new Mock<IGitHubService>();
        var service = new GitHubCredentialService(userRepoMock.Object, _encryptionService, gitHubMock.Object);

        var status = await service.GetIntegrationStatusAsync(5);

        Assert.NotNull(status);
        Assert.False(status.IsConnected);
        Assert.Null(status.GitHubUsername);
        Assert.Null(status.ConnectedAt);
    }

    [Fact]
    public async Task RemoveUserTokenAsync_ClearsAllThreeFields_AndReturnsDisconnected()
    {
        var user = new User
        {
            Id = 6,
            Username = "dmitry",
            EncryptedGitHubToken = "encrypted_val",
            GitHubUsername = "dmitry_gh",
            GitHubTokenCreatedAt = DateTime.UtcNow
        };

        var userRepoMock = new Mock<IUserRepository>();
        userRepoMock.Setup(r => r.GetByIdAsync(6, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        var service = new GitHubCredentialService(userRepoMock.Object, _encryptionService, Mock.Of<IGitHubService>());

        var result = await service.RemoveUserTokenAsync(6);

        Assert.NotNull(result);
        Assert.False(result.IsConnected);
        Assert.Null(result.GitHubUsername);
        Assert.Null(result.ConnectedAt);

        // Все три поля должны быть сброшены в null
        Assert.Null(user.EncryptedGitHubToken);
        Assert.Null(user.GitHubUsername);
        Assert.Null(user.GitHubTokenCreatedAt);

        userRepoMock.Verify(r => r.UpdateAsync(user, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetUserTokenAsync_ReturnsDecryptedToken_ForSpecifiedUserOnly()
    {
        const string PlainPat = "ghp_internal_operation_token_abc";
        var encrypted = _encryptionService.Encrypt(PlainPat);

        var user = new User
        {
            Id = 7,
            Username = "elena",
            EncryptedGitHubToken = encrypted
        };

        var userRepoMock = new Mock<IUserRepository>();
        userRepoMock.Setup(r => r.GetByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        var service = new GitHubCredentialService(userRepoMock.Object, _encryptionService, Mock.Of<IGitHubService>());

        var token = await service.GetUserTokenAsync(7);

        Assert.Equal(PlainPat, token);
    }

    [Fact]
    public async Task GetUserTokenAsync_UserWithoutToken_ReturnsNull()
    {
        var user = new User { Id = 8, Username = "maria", EncryptedGitHubToken = null };

        var userRepoMock = new Mock<IUserRepository>();
        userRepoMock.Setup(r => r.GetByIdAsync(8, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        var service = new GitHubCredentialService(userRepoMock.Object, _encryptionService, Mock.Of<IGitHubService>());

        var token = await service.GetUserTokenAsync(8);

        Assert.Null(token);
    }

    [Fact]
    public async Task TokenIsolation_UserA_And_UserB_HaveDistinctTokens_AndCannotAccessEachOther()
    {
        const string PatA = "ghp_user_A_secret_pat_1111111111";
        const string PatB = "ghp_user_B_secret_pat_2222222222";

        var userA = new User { Id = 10, Username = "userA" };
        var userB = new User { Id = 20, Username = "userB" };

        var userRepoMock = new Mock<IUserRepository>();
        userRepoMock.Setup(r => r.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(userA);
        userRepoMock.Setup(r => r.GetByIdAsync(20, It.IsAny<CancellationToken>())).ReturnsAsync(userB);

        var gitHubMock = new Mock<IGitHubService>();
        gitHubMock.Setup(g => g.VerifyTokenAndGetUsernameAsync(PatA, It.IsAny<CancellationToken>())).ReturnsAsync("gh_user_A");
        gitHubMock.Setup(g => g.VerifyTokenAndGetUsernameAsync(PatB, It.IsAny<CancellationToken>())).ReturnsAsync("gh_user_B");

        var service = new GitHubCredentialService(userRepoMock.Object, _encryptionService, gitHubMock.Object);

        await service.SetUserTokenAsync(10, PatA);
        await service.SetUserTokenAsync(20, PatB);

        // Зашифрованные строки различны
        Assert.NotEqual(userA.EncryptedGitHubToken, userB.EncryptedGitHubToken);

        // Получение токена для пользователя A возвращает строго PatA
        var retrievedA = await service.GetUserTokenAsync(10);
        Assert.Equal(PatA, retrievedA);

        // Получение токена для пользователя B возвращает строго PatB
        var retrievedB = await service.GetUserTokenAsync(20);
        Assert.Equal(PatB, retrievedB);
    }

    [Fact]
    public async Task GetUserTokenAsync_CorruptEncryptedData_ThrowsInvalidOperationException_WithoutLeakingData()
    {
        var user = new User
        {
            Id = 30,
            Username = "corrupt_user",
            EncryptedGitHubToken = "invalid_base64_not_encrypted"
        };

        var userRepoMock = new Mock<IUserRepository>();
        userRepoMock.Setup(r => r.GetByIdAsync(30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        var service = new GitHubCredentialService(userRepoMock.Object, _encryptionService, Mock.Of<IGitHubService>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetUserTokenAsync(30));

        Assert.Contains("Не удалось расшифровать сохраненный токен GitHub", ex.Message);
        Assert.DoesNotContain("invalid_base64", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task SetUserTokenAsync_EmptyOrWhitespacePat_ThrowsArgumentException(string? invalidPat)
    {
        var service = new GitHubCredentialService(Mock.Of<IUserRepository>(), _encryptionService, Mock.Of<IGitHubService>());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SetUserTokenAsync(1, invalidPat!));
    }

    [Fact]
    public async Task SetUserTokenAsync_NonExistentUser_ReturnsNull()
    {
        var userRepoMock = new Mock<IUserRepository>();
        userRepoMock.Setup(r => r.GetByIdAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        var service = new GitHubCredentialService(userRepoMock.Object, _encryptionService, Mock.Of<IGitHubService>());

        var result = await service.SetUserTokenAsync(999, "ghp_some_token");

        Assert.Null(result);
    }

    [Fact]
    public async Task VerifyTokenAndGetUsernameAsync_ValidPat_ReturnsUsername()
    {
        const string PlainPat = "ghp_valid_test_token_1234567890abcdef";
        const string ExpectedLogin = "octocat";

        var gitHubMock = new Mock<IGitHubService>();
        gitHubMock.Setup(g => g.VerifyTokenAndGetUsernameAsync(PlainPat, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExpectedLogin);

        var service = new GitHubCredentialService(Mock.Of<IUserRepository>(), _encryptionService, gitHubMock.Object);

        var username = await service.VerifyTokenAndGetUsernameAsync(PlainPat);

        Assert.Equal(ExpectedLogin, username);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task VerifyTokenAndGetUsernameAsync_EmptyOrWhitespacePat_ThrowsArgumentException(string? invalidPat)
    {
        var service = new GitHubCredentialService(Mock.Of<IUserRepository>(), _encryptionService, Mock.Of<IGitHubService>());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.VerifyTokenAndGetUsernameAsync(invalidPat!));
    }

    [Fact]
    public async Task Concurrent_SetAndRemoveToken_MaintainsConsistencyOfFields()
    {
        var user = new User { Id = 50, Username = "concurrency_user" };
        var userRepoMock = new Mock<IUserRepository>();
        userRepoMock.Setup(r => r.GetByIdAsync(50, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var gitHubMock = new Mock<IGitHubService>();
        gitHubMock.Setup(g => g.VerifyTokenAndGetUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string token, CancellationToken _) => $"user_for_{token}");

        var service = new GitHubCredentialService(userRepoMock.Object, _encryptionService, gitHubMock.Object);

        var tasks = Enumerable.Range(1, 20).Select(async i =>
        {
            if (i % 2 == 0)
            {
                await service.SetUserTokenAsync(50, $"ghp_token_{i}");
            }
            else
            {
                await service.RemoveUserTokenAsync(50);
            }
        });

        await Task.WhenAll(tasks);

        if (user.EncryptedGitHubToken != null)
        {
            Assert.NotNull(user.GitHubUsername);
            Assert.NotNull(user.GitHubTokenCreatedAt);
            var decrypted = _encryptionService.Decrypt(user.EncryptedGitHubToken);
            Assert.StartsWith("ghp_token_", decrypted);
            Assert.Equal($"user_for_{decrypted}", user.GitHubUsername);
        }
        else
        {
            Assert.Null(user.GitHubUsername);
            Assert.Null(user.GitHubTokenCreatedAt);
        }
    }
}
