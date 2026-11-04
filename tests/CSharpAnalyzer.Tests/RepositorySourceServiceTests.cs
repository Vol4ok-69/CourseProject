using Infrastructure.GitHub;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using Xunit;

namespace CSharpAnalyzer.Tests;

public class RepositorySourceServiceTests
{
    private const string ValidPat = "ghp_valid_test_token_1234567890abcdef";

    private readonly FakeGitHubHandler _fakeHandler;
    private readonly RepositorySourceService _service;

    public RepositorySourceServiceTests()
    {
        _fakeHandler = new FakeGitHubHandler(CreateTestArchive());
        var httpClient = new HttpClient(_fakeHandler)
        {
            BaseAddress = new Uri("https://api.github.com/")
        };

        _service = new RepositorySourceService(httpClient);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_ShouldExtractRepository()
    {
        var repositoryPath = await _service.DownloadAndExtractAsync(
            "https://github.com/test/repository",
            "abc123",
            42,
            ValidPat);

        Assert.True(Directory.Exists(repositoryPath));
        Assert.True(File.Exists(Path.Combine(repositoryPath, "src", "Program.cs")));
        Assert.True(File.Exists(Path.Combine(repositoryPath, "README.md")));

        Assert.NotNull(_fakeHandler.LastRequest);
        Assert.NotNull(_fakeHandler.LastRequest.Headers.Authorization);
        Assert.Equal("Bearer", _fakeHandler.LastRequest.Headers.Authorization.Scheme);
        Assert.Equal(ValidPat, _fakeHandler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_ShouldExcludeUnnecessaryDirectories()
    {
        var repositoryPath = await _service.DownloadAndExtractAsync(
            "https://github.com/test/repository",
            "abc123",
            42,
            ValidPat);

        Assert.False(Directory.Exists(Path.Combine(repositoryPath, ".git")));
        Assert.False(Directory.Exists(Path.Combine(repositoryPath, "bin")));
        Assert.False(Directory.Exists(Path.Combine(repositoryPath, "obj")));
        Assert.False(Directory.Exists(Path.Combine(repositoryPath, ".vs")));
    }

    [Fact]
    public async Task CleanupAsync_ShouldDeleteAnalysisDirectory()
    {
        var repositoryPath = await _service.DownloadAndExtractAsync(
            "https://github.com/test/repository",
            "abc123",
            42,
            ValidPat);

        Assert.True(Directory.Exists(repositoryPath));

        await _service.CleanupAsync(42);

        Assert.False(Directory.Exists(Path.Combine(AppContext.BaseDirectory, "data", "analysis", "42")));
    }

    [Fact]
    public async Task DownloadAndExtractAsync_ShouldCleanupAfterFailure()
    {
        var invalidArchiveClient = new HttpClient(
            new InvalidArchiveHandler())
        {
            BaseAddress = new Uri("https://api.github.com/")
        };

        var service = new RepositorySourceService(invalidArchiveClient);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadAndExtractAsync(
                "https://github.com/test/repository",
                "abc123",
                42,
                ValidPat));

        Assert.False(Directory.Exists(Path.Combine(AppContext.BaseDirectory, "data", "analysis", "42")));
    }

    [Fact]
    public async Task DownloadAndExtractAsync_WithoutToken_ThrowsInvalidOperationException()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.DownloadAndExtractAsync(
                "https://github.com/test/repository",
                "abc123",
                42));

        Assert.Contains("требуется персональный токен доступа (PAT)", ex.Message);
        Assert.Null(_fakeHandler.LastRequest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task DownloadAndExtractAsync_EmptyOrWhitespaceToken_ThrowsArgumentException(string? invalidToken)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.DownloadAndExtractAsync(
                "https://github.com/test/repository",
                "abc123",
                42,
                invalidToken!));

        Assert.Null(_fakeHandler.LastRequest);
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
