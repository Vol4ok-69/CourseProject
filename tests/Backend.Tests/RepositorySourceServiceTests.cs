using Application.Exceptions;
using Infrastructure.GitHub;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace Backend.Tests;

public sealed class RepositorySourceServiceTests
{
    private const string TestPat = "ghp_valid_test_token_1234567890abcdef";

    private sealed class TestHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handlerFunc) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return handlerFunc(request);
        }
    }

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> handlerFunc)
    {
        return new HttpClient(new TestHttpMessageHandler(handlerFunc))
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
    }

    private static byte[] CreateMinimalZipArchive()
    {
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("repo-head/file.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("content");
        }
        return memoryStream.ToArray();
    }

    [Fact]
    public async Task DownloadAndExtractAsync_WithoutPat_ThrowsInvalidOperationException_AndDoesNotSendHttpRequest()
    {
        var requestSent = false;
        var httpClient = CreateHttpClient(_ =>
        {
            requestSent = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var service = new RepositorySourceService(httpClient);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DownloadAndExtractAsync("https://github.com/owner/repo", "sha123", 100));

        Assert.Contains("требуется персональный токен доступа (PAT)", ex.Message);
        Assert.False(requestSent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    [InlineData("\r\n")]
    [InlineData("  \t  \n  ")]
    [InlineData(null)]
    public async Task DownloadAndExtractAsync_EmptyOrWhitespacePat_ThrowsArgumentException_AndDoesNotSendHttpRequest(string? invalidPat)
    {
        var requestSent = false;
        var httpClient = CreateHttpClient(_ =>
        {
            requestSent = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var service = new RepositorySourceService(httpClient);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.DownloadAndExtractAsync("https://github.com/owner/repo", "sha123", 101, invalidPat!));

        Assert.False(requestSent);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_SetsBearerAuthorizationHeader_OnHttpRequestMessage()
    {
        HttpRequestMessage? capturedRequest = null;
        var archiveBytes = CreateMinimalZipArchive();

        var httpClient = CreateHttpClient(req =>
        {
            capturedRequest = req;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archiveBytes)
            };
            return Task.FromResult(response);
        });

        var service = new RepositorySourceService(httpClient);
        var path = await service.DownloadAndExtractAsync("https://github.com/owner/repo", "sha123", 102, TestPat);

        try
        {
            Assert.NotNull(capturedRequest);
            Assert.NotNull(capturedRequest.Headers.Authorization);
            Assert.Equal("Bearer", capturedRequest.Headers.Authorization.Scheme);
            Assert.Equal(TestPat, capturedRequest.Headers.Authorization.Parameter);
            Assert.Null(httpClient.DefaultRequestHeaders.Authorization);
            Assert.True(Directory.Exists(path));
        }
        finally
        {
            await service.CleanupAsync(102);
        }
    }

    [Fact]
    public async Task DownloadAndExtractAsync_401Unauthorized_ThrowsTokenExpired()
    {
        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var service = new RepositorySourceService(httpClient);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.DownloadAndExtractAsync("https://github.com/owner/repo", "sha123", 103, TestPat));

        Assert.Equal(GitHubErrorKind.TokenExpired, ex.Kind);
        Assert.DoesNotContain(TestPat, ex.Message);
        Assert.DoesNotContain(TestPat, ex.ToString());
    }

    [Fact]
    public async Task DownloadAndExtractAsync_404NotFound_ThrowsRepoNotFound()
    {
        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var service = new RepositorySourceService(httpClient);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.DownloadAndExtractAsync("https://github.com/owner/repo", "sha123", 104, TestPat));

        Assert.Equal(GitHubErrorKind.RepoNotFound, ex.Kind);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_429TooManyRequests_ThrowsRateLimited()
    {
        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)));
        var service = new RepositorySourceService(httpClient);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.DownloadAndExtractAsync("https://github.com/owner/repo", "sha123", 105, TestPat));

        Assert.Equal(GitHubErrorKind.RateLimited, ex.Kind);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_403WithRateLimitZero_ThrowsRateLimited()
    {
        var httpClient = CreateHttpClient(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            return Task.FromResult(response);
        });
        var service = new RepositorySourceService(httpClient);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.DownloadAndExtractAsync("https://github.com/owner/repo", "sha123", 106, TestPat));

        Assert.Equal(GitHubErrorKind.RateLimited, ex.Kind);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_403WithoutRateLimit_ThrowsAccessDenied()
    {
        var httpClient = CreateHttpClient(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "60");
            return Task.FromResult(response);
        });
        var service = new RepositorySourceService(httpClient);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.DownloadAndExtractAsync("https://github.com/owner/repo", "sha123", 107, TestPat));

        Assert.Equal(GitHubErrorKind.AccessDenied, ex.Kind);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_502BadGateway_ThrowsServiceUnavailable()
    {
        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        var service = new RepositorySourceService(httpClient);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.DownloadAndExtractAsync("https://github.com/owner/repo", "sha123", 108, TestPat));

        Assert.Equal(GitHubErrorKind.ServiceUnavailable, ex.Kind);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_NetworkTimeout_ThrowsServiceUnavailable()
    {
        var httpClient = CreateHttpClient(_ => throw new HttpRequestException("Network failure"));
        var service = new RepositorySourceService(httpClient);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.DownloadAndExtractAsync("https://github.com/owner/repo", "sha123", 109, TestPat));

        Assert.Equal(GitHubErrorKind.ServiceUnavailable, ex.Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData((HttpStatusCode)429)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task DownloadAndExtractAsync_ErrorStatuses_NeverLeakTokenInException(HttpStatusCode statusCode)
    {
        const string SecretToken = "ghp_super_secret_archive_pat_xyz_987654321";
        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("{\"message\":\"error downloading archive with details\"}")
        }));

        var service = new RepositorySourceService(httpClient);
        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.DownloadAndExtractAsync("https://github.com/owner/repo", "sha123", 999, SecretToken));

        Assert.DoesNotContain(SecretToken, ex.Message);
        Assert.DoesNotContain(SecretToken, ex.ToString());
    }

    [Fact]
    public async Task DownloadAndExtractAsync_NetworkException_NeverLeaksTokenInException()
    {
        const string SecretToken = "ghp_super_secret_archive_pat_xyz_987654321";
        var httpClient = CreateHttpClient(_ => throw new HttpRequestException("Connection reset"));

        var service = new RepositorySourceService(httpClient);
        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            service.DownloadAndExtractAsync("https://github.com/owner/repo", "sha123", 998, SecretToken));

        Assert.DoesNotContain(SecretToken, ex.Message);
        Assert.DoesNotContain(SecretToken, ex.ToString());
    }

    [Fact]
    public async Task DownloadAndExtractAsync_SequentialRequests_WithDifferentTokens_UsesRespectiveTokens()
    {
        var capturedTokens = new List<string?>();
        var archiveBytes = CreateMinimalZipArchive();

        var httpClient = CreateHttpClient(req =>
        {
            capturedTokens.Add(req.Headers.Authorization?.Parameter);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archiveBytes)
            };
            return Task.FromResult(response);
        });

        var service = new RepositorySourceService(httpClient);
        try
        {
            await service.DownloadAndExtractAsync("https://github.com/owner/repo1", "sha1", 110, "token-user-1");
            await service.DownloadAndExtractAsync("https://github.com/owner/repo2", "sha2", 111, "token-user-2");

            Assert.Equal(2, capturedTokens.Count);
            Assert.Equal("token-user-1", capturedTokens[0]);
            Assert.Equal("token-user-2", capturedTokens[1]);
            Assert.Null(httpClient.DefaultRequestHeaders.Authorization);
        }
        finally
        {
            await service.CleanupAsync(110);
            await service.CleanupAsync(111);
        }
    }

    [Fact]
    public async Task DownloadAndExtractAsync_ConcurrentRequests_WithDifferentTokens_DoNotCrossContaminate()
    {
        var recordedTokens = new ConcurrentDictionary<string, string>();
        var archiveBytes = CreateMinimalZipArchive();

        var httpClient = CreateHttpClient(req =>
        {
            var url = req.RequestUri?.ToString() ?? string.Empty;
            var token = req.Headers.Authorization?.Parameter ?? string.Empty;
            recordedTokens[url] = token;

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archiveBytes)
            };
            return Task.FromResult(response);
        });

        var service = new RepositorySourceService(httpClient);
        var baseAnalysisId = 200;

        try
        {
            var tasks = Enumerable.Range(1, 20).Select(async i =>
            {
                var analysisId = baseAnalysisId + i;
                var repoUrl = $"https://github.com/owner/repo{i}";
                var token = $"pat-user-{i}";
                await Task.Yield();
                await service.DownloadAndExtractAsync(repoUrl, $"sha{i}", analysisId, token);
            });

            await Task.WhenAll(tasks);

            Assert.Equal(20, recordedTokens.Count);
            for (int i = 1; i <= 20; i++)
            {
                var expectedUrl = $"https://api.github.com/repos/owner/repo{i}/zipball/sha{i}";
                Assert.True(recordedTokens.TryGetValue(expectedUrl, out var actualToken));
                Assert.Equal($"pat-user-{i}", actualToken);
            }
            Assert.Null(httpClient.DefaultRequestHeaders.Authorization);
        }
        finally
        {
            for (int i = 1; i <= 20; i++)
            {
                await service.CleanupAsync(baseAnalysisId + i);
            }
        }
    }

    [Fact]
    public async Task DownloadAndExtractAsync_PatIsNeverRetainedInServiceFields()
    {
        const string secretPat = "ghp_super_secret_pat_reflection_audit_1234567";
        var archiveBytes = CreateMinimalZipArchive();

        var httpClient = CreateHttpClient(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archiveBytes)
            };
            return Task.FromResult(response);
        });

        var service = new RepositorySourceService(httpClient);
        const int testAnalysisId = 999;

        try
        {
            await service.DownloadAndExtractAsync("https://github.com/owner/repo", "sha-test", testAnalysisId, secretPat);

            var fields = typeof(RepositorySourceService).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            foreach (var field in fields)
            {
                var value = field.GetValue(service);
                Assert.NotEqual(secretPat, value);
                if (value is string strVal)
                {
                    Assert.DoesNotContain(secretPat, strVal, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        finally
        {
            await service.CleanupAsync(testAnalysisId);
        }
    }


    private static byte[] CreateZipWithEntries(params (string Name, string Content)[] entries)
    {
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, fileContent) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(fileContent);
            }
        }
        return memoryStream.ToArray();
    }

    [Fact]
    public async Task DownloadAndExtractAsync_ValidFileInAllowedDirectory_ExtractsSuccessfully()
    {
        var zipBytes = CreateZipWithEntries(("repo-main/subfolder/file.cs", "public class A {}"));
        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zipBytes)
        }));

        var service = new RepositorySourceService(httpClient);
        const int testAnalysisId = 301;

        try
        {
            var extractedPath = await service.DownloadAndExtractAsync("https://github.com/owner/repo", "sha-301", testAnalysisId, TestPat);

            var expectedFilePath = Path.Combine(extractedPath, "subfolder", "file.cs");
            Assert.True(File.Exists(expectedFilePath));
            Assert.Equal("public class A {}", await File.ReadAllTextAsync(expectedFilePath));
        }
        finally
        {
            await service.CleanupAsync(testAnalysisId);
        }
    }

    [Theory]
    [InlineData("repo-main/../outside.txt")]
    [InlineData("repo-main/../../outside.txt")]
    [InlineData("repo-main/../../../outside.txt")]
    public async Task DownloadAndExtractAsync_DirectoryTraversal_RelativePathEscapes_ThrowsInvalidOperationException_AndCreatesNoFiles(string maliciousPath)
    {
        var zipBytes = CreateZipWithEntries((maliciousPath, "malicious payload"));
        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zipBytes)
        }));

        var service = new RepositorySourceService(httpClient);
        const int testAnalysisId = 302;
        var outsideFilePath = Path.Combine(AppContext.BaseDirectory, "data", "analysis", "outside.txt");

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.DownloadAndExtractAsync("https://github.com/owner/repo", "sha-302", testAnalysisId, TestPat));

            Assert.Contains("Zip Slip", ex.Message);
            Assert.False(File.Exists(outsideFilePath), "Файл не должен быть записан за пределами целевого каталога!");
        }
        finally
        {
            await service.CleanupAsync(testAnalysisId);
            if (File.Exists(outsideFilePath)) File.Delete(outsideFilePath);
        }
    }

    [Theory]
    [InlineData("repo-main/../repository-evil/exploit.txt")]
    [InlineData("repo-main\\..\\repository-evil\\exploit.txt")]
    [InlineData("repo-main/../repository_neighbor/exploit.txt")]
    public async Task DownloadAndExtractAsync_NeighborDirectoryWithSimilarPrefix_ThrowsInvalidOperationException_AndCreatesNoFiles(string maliciousPath)
    {
        var zipBytes = CreateZipWithEntries((maliciousPath, "neighbor directory exploit"));
        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zipBytes)
        }));

        var service = new RepositorySourceService(httpClient);
        const int testAnalysisId = 303;
        var evilDir = Path.Combine(AppContext.BaseDirectory, "data", "analysis", testAnalysisId.ToString(), "repository-evil");
        var evilFile = Path.Combine(evilDir, "exploit.txt");

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.DownloadAndExtractAsync("https://github.com/owner/repo", "sha-303", testAnalysisId, TestPat));

            Assert.Contains("Zip Slip", ex.Message);
            Assert.False(File.Exists(evilFile), "Файл в соседнем каталоге не должен быть создан!");
            Assert.False(Directory.Exists(evilDir), "Соседний каталог не должен быть создан!");
        }
        finally
        {
            await service.CleanupAsync(testAnalysisId);
            if (Directory.Exists(evilDir)) Directory.Delete(evilDir, true);
        }
    }

    [Theory]
    [InlineData("repo-main/C:/evil_rooted.txt")]
    [InlineData("repo-main\\D:\\evil_rooted.txt")]
    public async Task DownloadAndExtractAsync_RootedOrAbsolutePaths_ThrowsInvalidOperationException_AndCreatesNoFiles(string maliciousPath)
    {
        var zipBytes = CreateZipWithEntries((maliciousPath, "absolute path exploit"));
        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zipBytes)
        }));

        var service = new RepositorySourceService(httpClient);
        const int testAnalysisId = 304;

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.DownloadAndExtractAsync("https://github.com/owner/repo", "sha-304", testAnalysisId, TestPat));

            Assert.Contains("Zip Slip", ex.Message);
        }
        finally
        {
            await service.CleanupAsync(testAnalysisId);
        }
    }

    [Fact]
    public async Task DownloadAndExtractAsync_Symlinks_AreSafelyIgnored()
    {
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var normalEntry = archive.CreateEntry("repo-main/normal.txt");
            using (var writer = new StreamWriter(normalEntry.Open()))
            {
                writer.Write("normal file");
            }

            var symlinkEntry = archive.CreateEntry("repo-main/symlink.txt");
            // S_IFLNK = 0xA000 в старших 16 битах ExternalAttributes
            symlinkEntry.ExternalAttributes = (int)(0xA000 << 16);
            using (var writer = new StreamWriter(symlinkEntry.Open()))
            {
                writer.Write("/etc/passwd");
            }
        }
        var zipBytes = memoryStream.ToArray();

        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zipBytes)
        }));

        var service = new RepositorySourceService(httpClient);
        const int testAnalysisId = 305;

        try
        {
            var repoPath = await service.DownloadAndExtractAsync("https://github.com/owner/repo", "sha-305", testAnalysisId, TestPat);

            Assert.True(File.Exists(Path.Combine(repoPath, "normal.txt")));
            Assert.False(File.Exists(Path.Combine(repoPath, "symlink.txt")), "Символическая ссылка должна быть проигнорирована и не извлечена!");
        }
        finally
        {
            await service.CleanupAsync(testAnalysisId);
        }
    }
}
