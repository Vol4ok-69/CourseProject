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
}
