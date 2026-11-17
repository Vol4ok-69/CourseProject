using Application.Exceptions;
using Application.Interfaces;
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

    private sealed class TestAnalysisLeaseGuard : IAnalysisLeaseGuard
    {
        private int _validationCallCount;

        public TestAnalysisLeaseGuard(
            int analysisId,
            Guid executionToken,
            bool isAcquired = true)
        {
            AnalysisId = analysisId;
            ExecutionToken = executionToken;
            IsAcquired = isAcquired;
        }

        public int AnalysisId { get; }

        public Guid ExecutionToken { get; }

        public bool IsAcquired { get; private set; }

        public int ValidationCallCount => _validationCallCount;

        public Func<int, bool>? ValidationCallback { get; set; }

        public Queue<bool>? ValidationQueue { get; set; }

        public int CurrentStatus { get; set; } =
            (int)Domain.Enums.AnalysisStatus.Running;

        public Task<bool> ValidateOwnershipAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var callIndex = Interlocked.Increment(ref _validationCallCount);

            if (ValidationCallback is not null)
            {
                var result = ValidationCallback(callIndex);
                if (!result)
                    IsAcquired = false;
                return Task.FromResult(result);
            }

            if (ValidationQueue is not null && ValidationQueue.Count > 0)
            {
                var result = ValidationQueue.Dequeue();
                if (!result)
                    IsAcquired = false;
                return Task.FromResult(result);
            }

            return Task.FromResult(IsAcquired);
        }

        public async Task<bool> ValidateOwnershipAsync(
            int[] allowedStatuses,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(allowedStatuses);

            var isOwner = await ValidateOwnershipAsync(cancellationToken);

            return isOwner && allowedStatuses.Contains(CurrentStatus);
        }

        public void Revoke()
        {
            IsAcquired = false;
        }

        public ValueTask DisposeAsync()
        {
            IsAcquired = false;
            return ValueTask.CompletedTask;
        }
    }

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

    private static Task<string> DownloadWithGuardAsync(
        RepositorySourceService service,
        string repositoryUrl,
        string commitHash,
        int analysisId,
        string personalAccessToken,
        Guid executionToken,
        TestAnalysisLeaseGuard guard,
        CancellationToken cancellationToken = default)
    {
        return service.DownloadAndExtractAsync(
            repositoryUrl,
            commitHash,
            analysisId,
            executionToken,
            personalAccessToken,
            guard,
            cancellationToken);
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
    public async Task DownloadAndExtractAsync_WithoutLeaseGuard_ThrowsInvalidOperationException_AndDoesNotSendHttpRequest()
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

        Assert.Contains("IAnalysisLeaseGuard", ex.Message);
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
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(101, executionToken);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            DownloadWithGuardAsync(
                service,
                "https://github.com/owner/repo",
                "sha123",
                101,
                invalidPat!,
                executionToken,
                guard));

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
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(102, executionToken);

        var path = await DownloadWithGuardAsync(
            service,
            "https://github.com/owner/repo",
            "sha123",
            102,
            TestPat,
            executionToken,
            guard);

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
            await service.CleanupAsync(102, executionToken, guard);
        }
    }

    [Fact]
    public async Task DownloadAndExtractAsync_401Unauthorized_ThrowsTokenExpired()
    {
        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var service = new RepositorySourceService(httpClient);
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(103, executionToken);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            DownloadWithGuardAsync(service, "https://github.com/owner/repo", "sha123", 103, TestPat, executionToken, guard));

        Assert.Equal(GitHubErrorKind.TokenExpired, ex.Kind);
        Assert.DoesNotContain(TestPat, ex.Message);
        Assert.DoesNotContain(TestPat, ex.ToString());
    }

    [Fact]
    public async Task DownloadAndExtractAsync_404NotFound_ThrowsRepoNotFound()
    {
        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var service = new RepositorySourceService(httpClient);
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(104, executionToken);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            DownloadWithGuardAsync(service, "https://github.com/owner/repo", "sha123", 104, TestPat, executionToken, guard));

        Assert.Equal(GitHubErrorKind.RepoNotFound, ex.Kind);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_429TooManyRequests_ThrowsRateLimited()
    {
        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)));
        var service = new RepositorySourceService(httpClient);
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(105, executionToken);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            DownloadWithGuardAsync(service, "https://github.com/owner/repo", "sha123", 105, TestPat, executionToken, guard));

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
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(106, executionToken);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            DownloadWithGuardAsync(service, "https://github.com/owner/repo", "sha123", 106, TestPat, executionToken, guard));

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
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(107, executionToken);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            DownloadWithGuardAsync(service, "https://github.com/owner/repo", "sha123", 107, TestPat, executionToken, guard));

        Assert.Equal(GitHubErrorKind.AccessDenied, ex.Kind);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_502BadGateway_ThrowsServiceUnavailable()
    {
        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        var service = new RepositorySourceService(httpClient);
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(108, executionToken);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            DownloadWithGuardAsync(service, "https://github.com/owner/repo", "sha123", 108, TestPat, executionToken, guard));

        Assert.Equal(GitHubErrorKind.ServiceUnavailable, ex.Kind);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_NetworkTimeout_ThrowsServiceUnavailable()
    {
        var httpClient = CreateHttpClient(_ => throw new HttpRequestException("Network failure"));
        var service = new RepositorySourceService(httpClient);
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(109, executionToken);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            DownloadWithGuardAsync(service, "https://github.com/owner/repo", "sha123", 109, TestPat, executionToken, guard));

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
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(999, executionToken);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            DownloadWithGuardAsync(service, "https://github.com/owner/repo", "sha123", 999, SecretToken, executionToken, guard));

        Assert.DoesNotContain(SecretToken, ex.Message);
        Assert.DoesNotContain(SecretToken, ex.ToString());
    }

    [Fact]
    public async Task DownloadAndExtractAsync_NetworkException_NeverLeaksTokenInException()
    {
        const string SecretToken = "ghp_super_secret_archive_pat_xyz_987654321";
        var httpClient = CreateHttpClient(_ => throw new HttpRequestException("Connection reset"));

        var service = new RepositorySourceService(httpClient);
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(998, executionToken);

        var ex = await Assert.ThrowsAsync<GitHubIntegrationException>(() =>
            DownloadWithGuardAsync(service, "https://github.com/owner/repo", "sha123", 998, SecretToken, executionToken, guard));

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
        var token1 = Guid.NewGuid();
        var guard1 = new TestAnalysisLeaseGuard(110, token1);
        var token2 = Guid.NewGuid();
        var guard2 = new TestAnalysisLeaseGuard(111, token2);

        try
        {
            await DownloadWithGuardAsync(service, "https://github.com/owner/repo1", "sha1", 110, "token-user-1", token1, guard1);
            await DownloadWithGuardAsync(service, "https://github.com/owner/repo2", "sha2", 111, "token-user-2", token2, guard2);

            Assert.Equal(2, capturedTokens.Count);
            Assert.Equal("token-user-1", capturedTokens[0]);
            Assert.Equal("token-user-2", capturedTokens[1]);
            Assert.Null(httpClient.DefaultRequestHeaders.Authorization);
        }
        finally
        {
            await service.CleanupAsync(110, token1, guard1);
            await service.CleanupAsync(111, token2, guard2);
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
        var executions = Enumerable.Range(1, 20)
            .Select(i =>
            {
                var id = baseAnalysisId + i;
                var tok = Guid.NewGuid();
                return (AnalysisId: id, Token: tok, Guard: new TestAnalysisLeaseGuard(id, tok));
            })
            .ToList();

        try
        {
            var tasks = executions.Select(async item =>
            {
                var repoUrl = $"https://github.com/owner/repo{item.AnalysisId - baseAnalysisId}";
                var token = $"pat-user-{item.AnalysisId - baseAnalysisId}";
                await Task.Yield();
                await DownloadWithGuardAsync(
                    service,
                    repoUrl,
                    $"sha{item.AnalysisId - baseAnalysisId}",
                    item.AnalysisId,
                    token,
                    item.Token,
                    item.Guard);
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
            foreach (var item in executions)
            {
                await service.CleanupAsync(item.AnalysisId, item.Token, item.Guard);
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
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(testAnalysisId, executionToken);

        try
        {
            await DownloadWithGuardAsync(service, "https://github.com/owner/repo", "sha-test", testAnalysisId, secretPat, executionToken, guard);

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
            await service.CleanupAsync(testAnalysisId, executionToken, guard);
        }
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
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(testAnalysisId, executionToken);

        try
        {
            var extractedPath = await DownloadWithGuardAsync(
                service,
                "https://github.com/owner/repo",
                "sha-301",
                testAnalysisId,
                TestPat,
                executionToken,
                guard);

            var expectedFilePath = Path.Combine(extractedPath, "subfolder", "file.cs");
            Assert.True(File.Exists(expectedFilePath));
            Assert.Equal("public class A {}", await File.ReadAllTextAsync(expectedFilePath));
        }
        finally
        {
            await service.CleanupAsync(testAnalysisId, executionToken, guard);
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
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(testAnalysisId, executionToken);
        var outsideFilePath = Path.Combine(AppContext.BaseDirectory, "data", "analysis", "outside.txt");

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DownloadWithGuardAsync(
                    service,
                    "https://github.com/owner/repo",
                    "sha-302",
                    testAnalysisId,
                    TestPat,
                    executionToken,
                    guard));

            Assert.Contains("Zip Slip", ex.Message);
            Assert.False(File.Exists(outsideFilePath), "Файл не должен быть записан за пределами целевого каталога!");
        }
        finally
        {
            await service.CleanupAsync(testAnalysisId, executionToken, guard);
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
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(testAnalysisId, executionToken);
        var evilDir = Path.Combine(AppContext.BaseDirectory, "data", "analysis", testAnalysisId.ToString(), "repository-evil");
        var evilFile = Path.Combine(evilDir, "exploit.txt");

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DownloadWithGuardAsync(
                    service,
                    "https://github.com/owner/repo",
                    "sha-303",
                    testAnalysisId,
                    TestPat,
                    executionToken,
                    guard));

            Assert.Contains("Zip Slip", ex.Message);
            Assert.False(File.Exists(evilFile), "Файл в соседнем каталоге не должен быть создан!");
            Assert.False(Directory.Exists(evilDir), "Соседний каталог не должен быть создан!");
        }
        finally
        {
            await service.CleanupAsync(testAnalysisId, executionToken, guard);
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
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(testAnalysisId, executionToken);

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DownloadWithGuardAsync(
                    service,
                    "https://github.com/owner/repo",
                    "sha-304",
                    testAnalysisId,
                    TestPat,
                    executionToken,
                    guard));

            Assert.Contains("Zip Slip", ex.Message);
        }
        finally
        {
            await service.CleanupAsync(testAnalysisId, executionToken, guard);
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
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(testAnalysisId, executionToken);

        try
        {
            var repoPath = await DownloadWithGuardAsync(
                service,
                "https://github.com/owner/repo",
                "sha-305",
                testAnalysisId,
                TestPat,
                executionToken,
                guard);

            Assert.True(File.Exists(Path.Combine(repoPath, "normal.txt")));
            Assert.False(File.Exists(Path.Combine(repoPath, "symlink.txt")), "Символическая ссылка должна быть проигнорирована и не извлечена!");
        }
        finally
        {
            await service.CleanupAsync(testAnalysisId, executionToken, guard);
        }
    }

    private sealed class ChunkedStream(IReadOnlyList<byte[]> chunks, Action<int>? onChunkRead = null) : Stream
    {
        private int _currentChunkIndex;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => chunks.Sum(c => c.Length);
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_currentChunkIndex >= chunks.Count)
            {
                return ValueTask.FromResult(0);
            }

            var chunkIndex = _currentChunkIndex++;
            var chunk = chunks[chunkIndex];
            chunk.CopyTo(buffer);

            onChunkRead?.Invoke(chunkIndex);

            return ValueTask.FromResult(chunk.Length);
        }
    }

    [Fact]
    public async Task DownloadAndExtractAsync_WhenLeaseLostDuringDownload_StopsImmediately_PreservesPartialFileAndWorkspace()
    {
        // 1. Подготовка тестовых блоков данных
        var chunk1 = new byte[128];
        Array.Fill<byte>(chunk1, 0x11);
        var chunk2 = new byte[256];
        Array.Fill<byte>(chunk2, 0x22);

        const int testAnalysisId = 701;
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(testAnalysisId, executionToken);

        // Поток отдаёт chunk1, затем перед записью chunk2 отзывает владение guard
        var stream = new ChunkedStream([chunk1, chunk2], chunkIndex =>
        {
            if (chunkIndex == 1)
            {
                // Перед записью следующего (второго) блока guard сообщает о потере владения
                guard.Revoke();
            }
        });

        var httpClient = CreateHttpClient(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            };
            return Task.FromResult(response);
        });

        var service = new RepositorySourceService(httpClient);

        var executionDir = Path.Combine(
            Path.GetTempPath(),
            "CourseProject",
            "analyses",
            testAnalysisId.ToString(),
            executionToken.ToString("D"));
        var tempZipPath = Path.Combine(executionDir, "archive.zip");

        try
        {
            // 2. Метод прекращает скачивание и выбрасывает исключение потери владения
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DownloadWithGuardAsync(
                    service,
                    "https://github.com/owner/repo",
                    "sha-chunk-test",
                    testAnalysisId,
                    TestPat,
                    executionToken,
                    guard));

            Assert.Contains("отклонено", ex.Message);

            // 3. Проверка файловой системы:
            // - Рабочий каталог анализа сохранён
            // - Временный ZIP сохранён и не удалён в finally/catch
            // - Второй блок НЕ записан, размер и содержимое файла строго равны первому блоку
            Assert.True(Directory.Exists(executionDir), "Рабочий каталог должен быть сохранён при потере владения.");
            Assert.True(File.Exists(tempZipPath), "Временный ZIP-файл должен быть сохранён для фонового клинера.");

            var actualZipBytes = await File.ReadAllBytesAsync(tempZipPath);
            Assert.Equal(chunk1.Length, actualZipBytes.Length);
            Assert.Equal(chunk1, actualZipBytes);
        }
        finally
        {
            try
            {
                if (Directory.Exists(executionDir))
                    Directory.Delete(executionDir, recursive: true);

                var parentDir = Path.Combine(
                    Path.GetTempPath(),
                    "CourseProject",
                    "analyses",
                    testAnalysisId.ToString());

                if (Directory.Exists(parentDir) && !Directory.EnumerateFileSystemEntries(parentDir).Any())
                    Directory.Delete(parentDir, recursive: false);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task DownloadAndExtractAsync_WhenLeaseLostDuringExtraction_StopsImmediately_PreservesExtractedDataAndWorkspace()
    {
        // 1. Подготовка архива с двумя файлами
        const string file1Content = "Content of first file for lease loss test.";
        const string file2Content = "Content of second file which must not be written.";

        var zipBytes = CreateZipWithEntries(
            ("repo-head/subfolder/file1.txt", file1Content),
            ("repo-head/subfolder/file2.txt", file2Content));

        const int testAnalysisId = 702;
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(testAnalysisId, executionToken);

        var executionDir = Path.Combine(
            Path.GetTempPath(),
            "CourseProject",
            "analyses",
            testAnalysisId.ToString(),
            executionToken.ToString("D"));
        var tempZipPath = Path.Combine(executionDir, "archive.zip");
        var file1Path = Path.Combine(executionDir, "repository", "subfolder", "file1.txt");
        var file2Path = Path.Combine(executionDir, "repository", "subfolder", "file2.txt");

        // Скачивание архива происходит успешно.
        // При распаковке guard подтверждает владение до тех пор, пока file1 не запишется полностью на диск.
        // Как только file1 записан (и его поток закрыт), следующий вызов ValidateOwnershipAsync возвращает false.
        guard.ValidationCallback = _ =>
        {
            if (File.Exists(file1Path))
            {
                try
                {
                    using var stream = File.Open(file1Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stream.Length > 0)
                    {
                        // Первый файл полностью записан и закрыт.
                        // Сообщаем о потере владения перед извлечением/записью следующего файла.
                        return false;
                    }
                }
                catch (IOException)
                {
                    // Файл 1 ещё открыт на запись с FileShare.None — запись первого файла продолжается.
                    return true;
                }
            }

            return true;
        };

        var httpClient = CreateHttpClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zipBytes)
        }));

        var service = new RepositorySourceService(httpClient);

        try
        {
            // 2. Метод прекращает распаковку и выбрасывает исключение потери владения
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DownloadWithGuardAsync(
                    service,
                    "https://github.com/owner/repo",
                    "sha-extract-loss",
                    testAnalysisId,
                    TestPat,
                    executionToken,
                    guard));

            Assert.Contains("отклонено", ex.Message);

            // 3. Проверка файловой системы:
            // - Первый файл успешно записан и его содержимое сохранено
            // - Следующий файл (file2) НЕ создан и не записан
            // - Рабочий каталог сохранён
            // - Временный ZIP сохранён и не удалён в finally/catch
            Assert.True(Directory.Exists(executionDir), "Рабочий каталог должен быть сохранён при потере владения.");
            Assert.True(File.Exists(tempZipPath), "Временный ZIP должен быть сохранён при потере владения.");
            Assert.True(File.Exists(file1Path), "Первый файл должен быть успешно записан на диск.");
            Assert.Equal(file1Content, await File.ReadAllTextAsync(file1Path));
            Assert.False(File.Exists(file2Path), "Второй файл НЕ должен быть создан после потери владения.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(executionDir))
                    Directory.Delete(executionDir, recursive: true);

                var parentDir = Path.Combine(
                    Path.GetTempPath(),
                    "CourseProject",
                    "analyses",
                    testAnalysisId.ToString());

                if (Directory.Exists(parentDir) && !Directory.EnumerateFileSystemEntries(parentDir).Any())
                    Directory.Delete(parentDir, recursive: false);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task DownloadAndExtractAsync_WhenAnalysisIsCompleted_RejectsWorkspaceAccess()
    {
        var requestSent = false;
        var httpClient = CreateHttpClient(_ =>
        {
            requestSent = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(CreateMinimalZipArchive())
            });
        });

        var service = new RepositorySourceService(httpClient);
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(900, executionToken)
        {
            CurrentStatus = (int)Domain.Enums.AnalysisStatus.Completed
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DownloadWithGuardAsync(
                service,
                "https://github.com/owner/repo",
                "sha-completed",
                900,
                TestPat,
                executionToken,
                guard));

        Assert.Contains("владение", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(requestSent);
    }
}
