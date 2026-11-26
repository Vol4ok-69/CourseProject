using Application.Interfaces;
using Domain.Enums;
using Infrastructure.GitHub;
using Microsoft.Extensions.Configuration;
using Moq;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using Xunit;

namespace CSharpAnalyzer.Tests;

public class RepositorySourceServiceTests : IDisposable
{
    private const string ValidPat = "ghp_valid_test_token_1234567890abcdef";

    private readonly string _testStorageRoot;
    private readonly IConfiguration _configuration;
    private readonly FakeGitHubHandler _fakeHandler;
    private readonly RepositorySourceService _service;

    public RepositorySourceServiceTests()
    {
        _testStorageRoot = Path.Combine(
            Path.GetTempPath(),
            "netscope-csharpanalyzer-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testStorageRoot);

        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["Storage:AnalysisPath"]).Returns(_testStorageRoot);
        _configuration = configMock.Object;

        _fakeHandler = new FakeGitHubHandler(CreateTestArchive());
        var httpClient = new HttpClient(_fakeHandler)
        {
            BaseAddress = new Uri("https://api.github.com/")
        };

        _service = new RepositorySourceService(httpClient, _configuration);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testStorageRoot))
        {
            try
            {
                Directory.Delete(_testStorageRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task DownloadAndExtractAsync_ShouldExtractRepository()
    {
        const int analysisId = 42;
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(analysisId, executionToken);

        var repositoryPath = await _service.DownloadAndExtractAsync(
            "https://github.com/test/repository",
            "abc123",
            analysisId,
            executionToken,
            ValidPat,
            guard);

        Assert.True(Directory.Exists(repositoryPath));
        Assert.True(File.Exists(Path.Combine(repositoryPath, "src", "Program.cs")));
        Assert.True(File.Exists(Path.Combine(repositoryPath, "README.md")));
        Assert.True(guard.ValidationCallCount > 0);

        Assert.NotNull(_fakeHandler.LastRequest);
        Assert.NotNull(_fakeHandler.LastRequest.Headers.Authorization);
        Assert.Equal("Bearer", _fakeHandler.LastRequest.Headers.Authorization.Scheme);
        Assert.Equal(ValidPat, _fakeHandler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_ShouldExcludeUnnecessaryDirectories()
    {
        const int analysisId = 43;
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(analysisId, executionToken);

        var repositoryPath = await _service.DownloadAndExtractAsync(
            "https://github.com/test/repository",
            "abc123",
            analysisId,
            executionToken,
            ValidPat,
            guard);

        Assert.True(guard.ValidationCallCount > 0);
        Assert.False(Directory.Exists(Path.Combine(repositoryPath, ".git")));
        Assert.False(Directory.Exists(Path.Combine(repositoryPath, "bin")));
        Assert.False(Directory.Exists(Path.Combine(repositoryPath, "obj")));
        Assert.False(Directory.Exists(Path.Combine(repositoryPath, ".vs")));
    }

    [Fact]
    public async Task CleanupAsync_ShouldDeleteAnalysisDirectory()
    {
        const int analysisId = 44;
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(analysisId, executionToken);

        var repositoryPath = await _service.DownloadAndExtractAsync(
            "https://github.com/test/repository",
            "abc123",
            analysisId,
            executionToken,
            ValidPat,
            guard);

        Assert.True(Directory.Exists(repositoryPath));
        var callsBeforeCleanup = guard.ValidationCallCount;

        await _service.CleanupAsync(analysisId, executionToken, guard);

        Assert.True(guard.ValidationCallCount > callsBeforeCleanup);
        Assert.False(Directory.Exists(repositoryPath));
        Assert.False(Directory.Exists(Path.Combine(_testStorageRoot, analysisId.ToString())));
    }

    [Fact]
    public async Task DownloadAndExtractAsync_ShouldCleanupAfterFailure()
    {
        const int analysisId = 45;
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(analysisId, executionToken);

        var invalidArchiveClient = new HttpClient(
            new InvalidArchiveHandler())
        {
            BaseAddress = new Uri("https://api.github.com/")
        };

        var service = new RepositorySourceService(invalidArchiveClient, _configuration);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadAndExtractAsync(
                "https://github.com/test/repository",
                "abc123",
                analysisId,
                executionToken,
                ValidPat,
                guard));

        Assert.True(guard.ValidationCallCount > 0);
        Assert.False(Directory.Exists(Path.Combine(_testStorageRoot, analysisId.ToString())));
    }

    [Fact]
    public async Task DownloadAndExtractAsync_WithoutToken_ThrowsInvalidOperationException()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.DownloadAndExtractAsync(
                "https://github.com/test/repository",
                "abc123",
                46));

        Assert.Contains("IAnalysisLeaseGuard", ex.Message);
        Assert.Null(_fakeHandler.LastRequest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task DownloadAndExtractAsync_EmptyOrWhitespaceToken_ThrowsArgumentException(string? invalidToken)
    {
        const int analysisId = 47;
        var executionToken = Guid.NewGuid();
        var guard = new TestAnalysisLeaseGuard(analysisId, executionToken);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.DownloadAndExtractAsync(
                "https://github.com/test/repository",
                "abc123",
                analysisId,
                executionToken,
                invalidToken!,
                guard));

        Assert.Equal(0, guard.ValidationCallCount);
        Assert.Null(_fakeHandler.LastRequest);
    }

    private sealed class TestAnalysisLeaseGuard(
        int analysisId,
        Guid executionToken,
        bool isAcquired = true) : IAnalysisLeaseGuard
    {
        private int _validationCallCount;

        public int AnalysisId { get; } = analysisId;

        public Guid ExecutionToken { get; } = executionToken;

        public bool IsAcquired { get; private set; } = isAcquired;

        public int ValidationCallCount => Volatile.Read(ref _validationCallCount);

        public Task<bool> ValidateOwnershipAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _validationCallCount);
            return Task.FromResult(IsAcquired);
        }

        public async Task<bool> ValidateOwnershipAsync(
            int[] allowedStatuses,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(allowedStatuses);
            var isOwner = await ValidateOwnershipAsync(cancellationToken);
            return isOwner && allowedStatuses.Contains((int)AnalysisStatus.Running);
        }

        public ValueTask DisposeAsync()
        {
            IsAcquired = false;
            return ValueTask.CompletedTask;
        }
    }

    private static byte[] CreateTestArchive()
    {
        using var stream = new MemoryStream();

        using (var archive = new ZipArchive(
                   stream,
                   ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            AddFile(archive, "test-repository-abc123/src/Program.cs", "public class Program { }");
            AddFile(archive, "test-repository-abc123/README.md", "# Test");
            AddFile(archive, "test-repository-abc123/.git/config", "git");
            AddFile(archive, "test-repository-abc123/bin/app.dll", "binary");
            AddFile(archive, "test-repository-abc123/obj/project.assets.json", "{}");
            AddFile(archive, "test-repository-abc123/.vs/config.json", "{}");
        }

        return stream.ToArray();
    }

    private static void AddFile(
        ZipArchive archive,
        string path,
        string content)
    {
        var entry = archive.CreateEntry(path);

        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private sealed class FakeGitHubHandler(byte[] archive) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive)
            };

            return Task.FromResult(response);
        }
    }

    private sealed class InvalidArchiveHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4])
            };

            return Task.FromResult(response);
        }
    }
}
