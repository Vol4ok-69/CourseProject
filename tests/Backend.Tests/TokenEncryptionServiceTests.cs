using Application.Interfaces;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Backend.Tests;

public sealed class TokenEncryptionServiceTests
{
    private static byte[] GenerateValidKey()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    [Fact]
    public void Encrypt_And_Decrypt_ReturnsOriginalPlainText()
    {
        var key = GenerateValidKey();
        var service = new AesGcmTokenEncryptionService(key);
        var originalToken = "github_pat_11AABBCCDDEEFFGGHHIIJJKKLLMMNNOOPPQQRRSSTTUUVVWWXXYYZZ";

        var encrypted = service.Encrypt(originalToken);
        var decrypted = service.Decrypt(encrypted);

        Assert.NotNull(encrypted);
        Assert.NotEqual(originalToken, encrypted);
        Assert.Equal(originalToken, decrypted);
    }

    [Fact]
    public void Encrypt_MultipleTimes_GeneratesDifferentNonceAndCiphertext()
    {
        var key = GenerateValidKey();
        var service = new AesGcmTokenEncryptionService(key);
        var token = "ghp_PersonalAccessTokenTest1234567890";

        var ciphertexts = new HashSet<string>();
        for (var i = 0; i < 10; i++)
        {
            var encrypted = service.Encrypt(token);
            ciphertexts.Add(encrypted);

            var decrypted = service.Decrypt(encrypted);
            Assert.Equal(token, decrypted);
        }

        // Каждый запуск шифрования должен генерировать уникальный случайный Nonce
        Assert.Equal(10, ciphertexts.Count);
    }

    [Fact]
    public void Constructor_WithNullKey_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new AesGcmTokenEncryptionService(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void Constructor_WithInvalidKeyLength_ThrowsArgumentException(int keyLength)
    {
        var invalidKey = new byte[keyLength];
        var ex = Assert.Throws<ArgumentException>(() => new AesGcmTokenEncryptionService(invalidKey));
        Assert.Contains("32 байта", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Encrypt_WithNullOrWhiteSpace_ThrowsArgumentException(string? invalidInput)
    {
        var service = new AesGcmTokenEncryptionService(GenerateValidKey());
        Assert.ThrowsAny<ArgumentException>(() => service.Encrypt(invalidInput!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Decrypt_WithNullOrWhiteSpace_ThrowsArgumentException(string? invalidInput)
    {
        var service = new AesGcmTokenEncryptionService(GenerateValidKey());
        Assert.ThrowsAny<ArgumentException>(() => service.Decrypt(invalidInput!));
    }

    [Fact]
    public void Decrypt_WithInvalidBase64_ThrowsInvalidOperationException()
    {
        var service = new AesGcmTokenEncryptionService(GenerateValidKey());
        var ex = Assert.Throws<InvalidOperationException>(() => service.Decrypt("not-valid-base64!@#$%^"));
        Assert.Contains("Base64", ex.Message);
    }

    [Fact]
    public void Decrypt_WithTruncatedHeader_ThrowsInvalidOperationException()
    {
        var service = new AesGcmTokenEncryptionService(GenerateValidKey());
        // Заголовок должен быть не менее 29 байт (1 байт версии + 12 байт nonce + 16 байт tag)
        var shortPayload = new byte[28];
        var base64 = Convert.ToBase64String(shortPayload);

        var ex = Assert.Throws<InvalidOperationException>(() => service.Decrypt(base64));
        Assert.Contains("меньше минимального размера заголовка", ex.Message);
    }

    [Fact]
    public void Decrypt_WithUnsupportedVersion_ThrowsNotSupportedException()
    {
        var key = GenerateValidKey();
        var service = new AesGcmTokenEncryptionService(key);
        var encrypted = service.Encrypt("test_token");

        var payload = Convert.FromBase64String(encrypted);
        // Меняем байт версии на неподдерживаемый (например, 2)
        payload[0] = 2;
        var modifiedBase64 = Convert.ToBase64String(payload);

        var ex = Assert.Throws<NotSupportedException>(() => service.Decrypt(modifiedBase64));
        Assert.Contains("Неподдерживаемая версия формата", ex.Message);
    }

    [Fact]
    public void Decrypt_WithCorruptedCiphertext_ThrowsInvalidOperationException()
    {
        var key = GenerateValidKey();
        var service = new AesGcmTokenEncryptionService(key);
        var encrypted = service.Encrypt("my_secret_token_123");

        var payload = Convert.FromBase64String(encrypted);
        // Портим последний байт (зашифрованный текст)
        payload[^1] ^= 0xFF;
        var corruptedBase64 = Convert.ToBase64String(payload);

        var ex = Assert.Throws<InvalidOperationException>(() => service.Decrypt(corruptedBase64));
        Assert.Contains("данные повреждены или аутентификационный тег не совпадает", ex.Message);
    }

    [Fact]
    public void Decrypt_WithCorruptedTag_ThrowsInvalidOperationException()
    {
        var key = GenerateValidKey();
        var service = new AesGcmTokenEncryptionService(key);
        var encrypted = service.Encrypt("my_secret_token_123");

        var payload = Convert.FromBase64String(encrypted);
        // Тег находится в диапазоне байтов [13..28], портим байт 15
        payload[15] ^= 0xAA;
        var corruptedBase64 = Convert.ToBase64String(payload);

        var ex = Assert.Throws<InvalidOperationException>(() => service.Decrypt(corruptedBase64));
        Assert.Contains("данные повреждены или аутентификационный тег не совпадает", ex.Message);
    }

    [Fact]
    public void Decrypt_WithCorruptedNonce_ThrowsInvalidOperationException()
    {
        var key = GenerateValidKey();
        var service = new AesGcmTokenEncryptionService(key);
        var encrypted = service.Encrypt("my_secret_token_123");

        var payload = Convert.FromBase64String(encrypted);
        // Nonce находится в диапазоне байтов [1..12], портим байт 2
        payload[2] ^= 0x55;
        var corruptedBase64 = Convert.ToBase64String(payload);

        var ex = Assert.Throws<InvalidOperationException>(() => service.Decrypt(corruptedBase64));
        Assert.Contains("данные повреждены или аутентификационный тег не совпадает", ex.Message);
    }

    [Fact]
    public void Decrypt_WithDifferentKey_ThrowsInvalidOperationException()
    {
        var keyA = GenerateValidKey();
        var keyB = GenerateValidKey();
        var serviceA = new AesGcmTokenEncryptionService(keyA);
        var serviceB = new AesGcmTokenEncryptionService(keyB);

        var encrypted = serviceA.Encrypt("super_secret_pat");

        var ex = Assert.Throws<InvalidOperationException>(() => serviceB.Decrypt(encrypted));
        Assert.Contains("данные повреждены или аутентификационный тег не совпадает", ex.Message);
    }

    [Fact]
    public async Task AppDbContext_UserConfiguration_PersistsAndRetrievesGitHubIntegrationFields()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var setupContext = new AppDbContext(options))
        {
            setupContext.Database.EnsureCreated();

            var role = new Role { Id = 1, Name = "Admin" };
            setupContext.Roles.Add(role);

            var createdAt = new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);
            var user = new User
            {
                Id = 1,
                Username = "developer",
                PasswordHash = "hash123",
                RoleId = 1,
                EncryptedGitHubToken = "AQEBESIzRFVmd4iZqrvM3e7/AQIDBAUGBwgJCgsMDQ4PEA==",
                GitHubUsername = "octocat",
                GitHubTokenCreatedAt = createdAt
            };

            setupContext.Users.Add(user);
            await setupContext.SaveChangesAsync();
        }

        using (var verifyContext = new AppDbContext(options))
        {
            var user = await verifyContext.Users.FirstOrDefaultAsync(u => u.Id == 1);

            Assert.NotNull(user);
            Assert.Equal("developer", user.Username);
            Assert.Equal("AQEBESIzRFVmd4iZqrvM3e7/AQIDBAUGBwgJCgsMDQ4PEA==", user.EncryptedGitHubToken);
            Assert.Equal("octocat", user.GitHubUsername);
            Assert.NotNull(user.GitHubTokenCreatedAt);
            Assert.Equal(new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc), user.GitHubTokenCreatedAt.Value);
        }

        connection.Close();
    }

    [Fact]
    public async Task AppDbContext_UserConfiguration_AllowsNullGitHubIntegrationFields()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var setupContext = new AppDbContext(options))
        {
            setupContext.Database.EnsureCreated();

            var role = new Role { Id = 1, Name = "Admin" };
            setupContext.Roles.Add(role);

            var user = new User
            {
                Id = 2,
                Username = "user_without_token",
                PasswordHash = "hash123",
                RoleId = 1,
                EncryptedGitHubToken = null,
                GitHubUsername = null,
                GitHubTokenCreatedAt = null
            };

            setupContext.Users.Add(user);
            await setupContext.SaveChangesAsync();
        }

        using (var verifyContext = new AppDbContext(options))
        {
            var user = await verifyContext.Users.FirstOrDefaultAsync(u => u.Id == 2);

            Assert.NotNull(user);
            Assert.Null(user.EncryptedGitHubToken);
            Assert.Null(user.GitHubUsername);
            Assert.Null(user.GitHubTokenCreatedAt);
        }

        connection.Close();
    }
}
