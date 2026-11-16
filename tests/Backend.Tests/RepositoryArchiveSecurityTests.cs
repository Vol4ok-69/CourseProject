using Application.Exceptions;
using Infrastructure.GitHub;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace Backend.Tests;

public sealed class RepositoryArchiveSecurityTests : IDisposable
{
    private readonly int _testAnalysisId;
    private readonly string _testAnalysisDir;

    public RepositoryArchiveSecurityTests()
    {
        _testAnalysisId = Random.Shared.Next(100_000, 999_999);
        _testAnalysisDir = Path.Combine(AppContext.BaseDirectory, "data", "analysis", _testAnalysisId.ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_testAnalysisDir))
        {
            try
            {
                Directory.Delete(_testAnalysisDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static MemoryStream CreateZipStream(Action<ZipArchive> populate)
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            populate(archive);
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task ExtractArchiveAsync_WithoutGuard_ThrowsInvalidOperationExceptionAndFailsClosed()
    {
        var service = new RepositorySourceService(new HttpClient());
        var destination = Path.Combine(_testAnalysisDir, "repository");

        using var zipStream = CreateZipStream(archive =>
        {
            var entry = archive.CreateEntry("root/src/index.js");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("console.log('hello');");
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExtractArchiveAsync(zipStream, destination, _testAnalysisId));

        Assert.Contains("IAnalysisLeaseGuard", ex.Message);
        Assert.False(Directory.Exists(destination), "Целевой каталог не должен создаваться без guard!");
    }

    [Fact]
    public async Task ExtractArchiveInternalAsync_ValidArchiveWithinLimits_ExtractsSuccessfully()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            var entry = archive.CreateEntry("root/src/index.js");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("console.log('hello');");
        });

        await RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None);

        var extractedFile = Path.Combine(destination, "src", "index.js");
        Assert.True(File.Exists(extractedFile));
        Assert.Equal("console.log('hello');", await File.ReadAllTextAsync(extractedFile));
    }

    [Fact]
    public async Task ExtractArchiveInternalAsync_ZipSlipAttack_ThrowsInvalidOperationException()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            var maliciousEntry = archive.CreateEntry("root/../../outside.txt");
            using var writer = new StreamWriter(maliciousEntry.Open(), Encoding.UTF8);
            writer.Write("evil payload");
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None));

        Assert.Contains("Zip Slip", ex.Message);
    }

    [Fact]
    public async Task ExtractArchiveInternalAsync_FileCountExceedsLimit_ThrowsArchiveLimitExceededException()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            // Создаем 10 005 пустых файлов
            for (var i = 1; i <= 10_005; i++)
            {
                archive.CreateEntry($"root/file_{i}.txt");
            }
        });

        var ex = await Assert.ThrowsAsync<ArchiveLimitExceededException>(() =>
            RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None));

        Assert.Contains("10 000", ex.Message);
    }

    [Fact]
    public async Task ExtractArchiveInternalAsync_SingleFileExceeds50MiB_ThrowsArchiveLimitExceededException()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        // Создаем сжатый блок в 51 МиБ нулей (сжатый размер ~52 КБ)
        using var zipStream = CreateZipStream(archive =>
        {
            var entry = archive.CreateEntry("root/huge_file.bin", CompressionLevel.Optimal);
            using var entryStream = entry.Open();

            var chunk = new byte[64 * 1024]; // 64 КБ
            var written = 0L;
            var target = 51L * 1024 * 1024; // 51 МиБ

            while (written < target)
            {
                var toWrite = (int)Math.Min(chunk.Length, target - written);
                entryStream.Write(chunk, 0, toWrite);
                written += toWrite;
            }
        });

        var ex = await Assert.ThrowsAsync<ArchiveLimitExceededException>(() =>
            RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None));

        Assert.Contains("50 МиБ", ex.Message);
    }

    [Fact]
    public async Task ExtractArchiveInternalAsync_TotalExtractedExceeds250MiB_ThrowsArchiveLimitExceededException()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        // Создаем 6 файлов по 45 МиБ (суммарно 270 МиБ > 250 МиБ лимита, каждый файл < 50 МиБ)
        using var zipStream = CreateZipStream(archive =>
        {
            for (var i = 1; i <= 6; i++)
            {
                var entry = archive.CreateEntry($"root/part_{i}.bin", CompressionLevel.Optimal);
                using var entryStream = entry.Open();

                var chunk = new byte[64 * 1024];
                var written = 0L;
                var target = 45L * 1024 * 1024; // 45 МиБ

                while (written < target)
                {
                    var toWrite = (int)Math.Min(chunk.Length, target - written);
                    entryStream.Write(chunk, 0, toWrite);
                    written += toWrite;
                }
            }
        });

        var ex = await Assert.ThrowsAsync<ArchiveLimitExceededException>(() =>
            RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None));

        Assert.Contains("250 МиБ", ex.Message);
    }

    [Fact]
    public async Task CleanupAsync_WithoutGuard_ThrowsInvalidOperationExceptionAndDoesNotDeleteDirectory()
    {
        var service = new RepositorySourceService(new HttpClient());
        var path = Path.Combine(_testAnalysisDir, "repository", "somefile.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "test");

        Assert.True(Directory.Exists(_testAnalysisDir));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CleanupAsync(_testAnalysisId));

        Assert.Contains("IAnalysisLeaseGuard", ex.Message);
        Assert.True(Directory.Exists(_testAnalysisDir), "Каталог не должен удаляться старым CleanupAsync без guard!");
    }
}
