using Infrastructure.GitHub;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using Xunit;

namespace CSharpAnalyzer.Tests;

public class RepositorySourceServiceTests
{
    private readonly RepositorySourceService _service;

    public RepositorySourceServiceTests()
    {
        var httpClient = new HttpClient(
            new FakeGitHubHandler(CreateTestArchive()))
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
            42);

        Assert.True(Directory.Exists(repositoryPath));
        Assert.True(File.Exists(Path.Combine(repositoryPath, "src", "Program.cs")));
        Assert.True(File.Exists(Path.Combine(repositoryPath, "README.md")));
    }

    [Fact]
    public async Task DownloadAndExtractAsync_ShouldExcludeUnnecessaryDirectories()
    {
        var repositoryPath = await _service.DownloadAndExtractAsync(
            "https://github.com/test/repository",
            "abc123",
            42);

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
            42);

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
                42));

        Assert.False(Directory.Exists(Path.Combine(AppContext.BaseDirectory, "data", "analysis", "42")));
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
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
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