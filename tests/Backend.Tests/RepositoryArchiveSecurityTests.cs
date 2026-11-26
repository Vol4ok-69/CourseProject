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

    [Fact]
    public async Task ExtractArchiveInternalAsync_GitHubStyleArchiveWithRootDirectoryEntry_StripsCommonRoot()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            archive.CreateEntry("owner-repo-abc123/");
            AddTextEntry(archive, "owner-repo-abc123/Program.cs", "Console.WriteLine();");
            AddTextEntry(archive, "owner-repo-abc123/src/App.cs", "class App {}");
        });

        await RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(destination, "Program.cs")));
        Assert.True(File.Exists(Path.Combine(destination, "src", "App.cs")));
        Assert.False(Directory.Exists(Path.Combine(destination, "owner-repo-abc123")));
    }

    [Fact]
    public async Task ExtractArchiveInternalAsync_FlatArchive_ExtractsAllFilesAndPreservesStructure()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            AddTextEntry(archive, "Program.cs", "Console.WriteLine();");
            AddTextEntry(archive, "README.md", "# Flat");
            AddTextEntry(archive, "src/App.cs", "class App {}");
        });

        await RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None);

        Assert.Equal("Console.WriteLine();", await File.ReadAllTextAsync(Path.Combine(destination, "Program.cs")));
        Assert.Equal("# Flat", await File.ReadAllTextAsync(Path.Combine(destination, "README.md")));
        Assert.Equal("class App {}", await File.ReadAllTextAsync(Path.Combine(destination, "src", "App.cs")));
    }

    [Fact]
    public async Task ExtractArchiveInternalAsync_MultipleRootDirectories_PreservesTopLevelFolders()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            AddTextEntry(archive, "src/App.cs", "class App {}");
            AddTextEntry(archive, "tests/AppTests.cs", "class AppTests {}");
            AddTextEntry(archive, "docs/readme.txt", "docs");
        });

        await RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(destination, "src", "App.cs")));
        Assert.True(File.Exists(Path.Combine(destination, "tests", "AppTests.cs")));
        Assert.True(File.Exists(Path.Combine(destination, "docs", "readme.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "App.cs")));
    }

    [Fact]
    public async Task ExtractArchiveInternalAsync_SingleRootFile_ExtractsSuccessfully()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            AddTextEntry(archive, "Program.cs", "Console.WriteLine(42);");
        });

        await RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None);

        var filePath = Path.Combine(destination, "Program.cs");
        Assert.True(File.Exists(filePath));
        Assert.Equal("Console.WriteLine(42);", await File.ReadAllTextAsync(filePath));
    }

    [Fact]
    public async Task ExtractArchiveInternalAsync_EmptyArchive_CompletesWithEmptyDirectory()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(_ => { });

        await RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None);

        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData(@"..\evil.txt")]
    [InlineData("../../outside.txt")]
    [InlineData("root/sub/../evil.txt")]
    public async Task ExtractArchiveInternalAsync_TraversalEntries_ThrowInvalidOperationException_AndCreateNothing(string entryName)
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            AddTextEntry(archive, entryName, "evil");
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None));

        Assert.Contains("Zip Slip", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        Assert.False(File.Exists(Path.Combine(_testAnalysisDir, "evil.txt")));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/usr/local/file.txt")]
    [InlineData("C:/Windows/file.txt")]
    [InlineData(@"C:\Windows\file.txt")]
    [InlineData(@"\\server\share\file.txt")]
    public async Task ExtractArchiveInternalAsync_AbsolutePaths_ThrowInvalidOperationException_AndCreateNothing(string entryName)
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            AddTextEntry(archive, entryName, "evil");
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None));

        Assert.Contains("Zip Slip", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task ExtractArchiveInternalAsync_DotSegmentsAndRepeatedSlashes_NormalizeBeforeRootDetection()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            AddTextEntry(archive, "./root/src/App.cs", "class App {}");
            AddTextEntry(archive, "root//./README.md", "# Readme");
        });

        await RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(destination, "src", "App.cs")));
        Assert.True(File.Exists(Path.Combine(destination, "README.md")));
        Assert.False(Directory.Exists(Path.Combine(destination, "root")));
    }

    [Fact]
    public async Task ExtractArchiveInternalAsync_DotSegmentInFlatArchive_ExtractsToRoot()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            AddTextEntry(archive, "./Program.cs", "class Program {}");
        });

        await RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(destination, "Program.cs")));
    }

    [Theory]
    [InlineData("file.txt:stream")]
    [InlineData("folder/file.txt:stream")]
    public async Task ExtractArchiveInternalAsync_NtfsAlternateDataStream_ThrowsInvalidOperationException_AndCreateNothing(string entryName)
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            AddTextEntry(archive, entryName, "ads payload");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None));

        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("NUL")]
    [InlineData("AUX")]
    [InlineData("PRN")]
    [InlineData("COM1")]
    [InlineData("LPT1")]
    [InlineData("COM\u00B9")]
    [InlineData("COM\u00B2")]
    [InlineData("COM\u00B3")]
    [InlineData("LPT\u00B9")]
    [InlineData("LPT\u00B2")]
    [InlineData("LPT\u00B3")]
    [InlineData("folder/CON.txt")]
    [InlineData("CON .txt")]
    [InlineData("NUL .log")]
    [InlineData("PRN .txt")]
    [InlineData("AUX .log")]
    public async Task ExtractArchiveInternalAsync_WindowsReservedDeviceNames_ThrowInvalidOperationException_AndCreateNothing(string entryName)
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            AddTextEntry(archive, entryName, "reserved");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None));

        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Theory]
    [InlineData("file.")]
    [InlineData("file ")]
    [InlineData("folder./App.cs")]
    public async Task ExtractArchiveInternalAsync_TrailingDotOrSpaceInSegment_ThrowsInvalidOperationException_AndCreateNothing(string entryName)
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            AddTextEntry(archive, entryName, "trailing");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None));

        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task ExtractArchiveInternalAsync_ExactDuplicateFilePaths_ThrowsInvalidOperationException_AndWritesNothing()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            AddTextEntry(archive, "root/valid.cs", "valid");
            AddTextEntry(archive, "root/src/App.cs", "v1");
            AddTextEntry(archive, "root/src/./App.cs", "v2");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None));

        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task ExtractArchiveInternalAsync_FileAndDirectoryCaseCollisions_ThrowInvalidOperationException_AndWriteNothing()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream1 = CreateZipStream(archive =>
        {
            AddTextEntry(archive, "Repo/valid.cs", "valid");
            AddTextEntry(archive, "Repo/App.cs", "upper");
            AddTextEntry(archive, "Repo/app.cs", "lower");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySourceService.ExtractArchiveInternalAsync(zipStream1, destination, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));

        using var zipStream2 = CreateZipStream(archive =>
        {
            AddTextEntry(archive, "Repo/valid.cs", "valid");
            AddTextEntry(archive, "Repo/Src/A.cs", "a");
            AddTextEntry(archive, "Repo/src/B.cs", "b");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySourceService.ExtractArchiveInternalAsync(zipStream2, destination, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task ExtractArchiveInternalAsync_DifferentCasedRootDirectories_DoesNotStripRoot()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            AddTextEntry(archive, "Repo1/src/A.cs", "a");
            AddTextEntry(archive, "Repo2/tests/B.cs", "b");
        });

        await RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(destination, "Repo1", "src", "A.cs")));
        Assert.True(File.Exists(Path.Combine(destination, "Repo2", "tests", "B.cs")));
    }

    [Theory]
    [InlineData("Repo/\u00C9/A.cs", "Repo/E\u0301/B.cs")]
    [InlineData("Repo/\u00C4/A.cs", "Repo/\u00E4/B.cs")]
    [InlineData("Repo/a/\u00C9/x.cs", "Repo/a/E\u0301/y.cs")]
    [InlineData("Repo/\u00C9.cs", "Repo/E\u0301.cs")]
    [InlineData("Repo/\u00C4.cs", "Repo/\u00E4.cs")]
    [InlineData("Repo/\u00C9", "Repo/E\u0301/A.cs")]
    public async Task ExtractArchiveInternalAsync_UnicodeNormalizationAndCaseConflicts_ThrowInvalidOperationException_AndWriteNothing(
        string firstPath,
        string secondPath)
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            AddTextEntry(archive, "Repo/valid.cs", "valid");
            AddTextEntry(archive, firstPath, "first");
            AddTextEntry(archive, secondPath, "second");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None));

        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExtractArchiveInternalAsync_FileAndDirectoryConflict_ThrowsRegardlessOfOrder_AndWritesNothing(bool fileFirst)
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            AddTextEntry(archive, "root/valid.cs", "valid");
            if (fileFirst)
            {
                AddTextEntry(archive, "root/src", "file");
                AddTextEntry(archive, "root/src/App.cs", "nested");
            }
            else
            {
                AddTextEntry(archive, "root/src/App.cs", "nested");
                AddTextEntry(archive, "root/src", "file");
            }
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None));

        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task ExtractArchiveInternalAsync_RepeatedIdenticalDirectoryEntries_ExtractsSuccessfully()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            archive.CreateEntry("Repo/src/");
            archive.CreateEntry("Repo/src/");
            AddTextEntry(archive, "Repo/src/A.cs", "class A {}");
        });

        await RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(destination, "src", "A.cs")));
    }

    [Theory]
    [InlineData(".git/config")]
    [InlineData("bin/app.dll")]
    [InlineData("obj/project.assets.json")]
    public async Task ExtractArchiveInternalAsync_ExcludedDirectoryAsOnlyRoot_IsCompletelyExcluded(string entryName)
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            AddTextEntry(archive, entryName, "excluded");
        });

        await RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None);

        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task ExtractArchiveInternalAsync_ExcludedDirectoriesInsideCommonRoot_AreFilteredOut()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            AddTextEntry(archive, "root/.git/config", "git");
            AddTextEntry(archive, "root/bin/file.dll", "bin");
            AddTextEntry(archive, "root/obj/project.assets.json", "obj");
            AddTextEntry(archive, "root/src/App.cs", "class App {}");
        });

        await RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(destination, "src", "App.cs")));
        Assert.False(Directory.Exists(Path.Combine(destination, ".git")));
        Assert.False(Directory.Exists(Path.Combine(destination, "bin")));
        Assert.False(Directory.Exists(Path.Combine(destination, "obj")));
    }

    [Fact]
    public async Task ExtractArchiveInternalAsync_SymlinkWithDifferentRoot_DoesNotPreventRootStripping()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            AddTextEntry(archive, "root/src/App.cs", "class App {}");
            var symlinkEntry = archive.CreateEntry("other/link");
            symlinkEntry.ExternalAttributes = (int)(0xA000 << 16);
            using var writer = new StreamWriter(symlinkEntry.Open(), Encoding.UTF8);
            writer.Write("/etc/passwd");
        });

        await RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(destination, "src", "App.cs")));
        Assert.False(Directory.Exists(Path.Combine(destination, "root")));
        Assert.False(Directory.Exists(Path.Combine(destination, "other")));
    }

    [Fact]
    public async Task ExtractArchiveInternalAsync_OnlySymlinkArchive_CompletesWithEmptyDirectory()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            var symlinkEntry = archive.CreateEntry("root/link");
            symlinkEntry.ExternalAttributes = (int)(0xA000 << 16);
            using var writer = new StreamWriter(symlinkEntry.Open(), Encoding.UTF8);
            writer.Write("target");
        });

        await RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None);

        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task ExtractArchiveInternalAsync_MaliciousSymlinkPath_ThrowsInvalidOperationException()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            var symlinkEntry = archive.CreateEntry("../evil_link");
            symlinkEntry.ExternalAttributes = (int)(0xA000 << 16);
            using var writer = new StreamWriter(symlinkEntry.Open(), Encoding.UTF8);
            writer.Write("/etc/passwd");
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None));

        Assert.Contains("Zip Slip", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task ExtractArchiveInternalAsync_ExcludedEntryWithDifferentRoot_DoesNotPreventRootStripping()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            AddTextEntry(archive, "repo/src/App.cs", "class App {}");
            AddTextEntry(archive, "repo/tests/AppTests.cs", "class AppTests {}");
            AddTextEntry(archive, "bin/cache.dat", "cache");
        });

        await RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(destination, "src", "App.cs")));
        Assert.True(File.Exists(Path.Combine(destination, "tests", "AppTests.cs")));
        Assert.False(Directory.Exists(Path.Combine(destination, "repo")));
        Assert.False(Directory.Exists(Path.Combine(destination, "bin")));
    }

    [Fact]
    public async Task ExtractArchiveInternalAsync_ExcludedRootDirectoryWithNestedFiles_ExtractsNothing()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream = CreateZipStream(archive =>
        {
            AddTextEntry(archive, "bin/cache.dat", "cache");
            AddTextEntry(archive, "bin/sub/file.dll", "binary");
        });

        await RepositorySourceService.ExtractArchiveInternalAsync(zipStream, destination, CancellationToken.None);

        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task ExtractArchiveInternalAsync_TrailingDotSegmentEntry_TreatsEntryAsDirectoryAndNeverCreatesFileNamedRoot()
    {
        var destination = Path.Combine(_testAnalysisDir, "repository");
        Directory.CreateDirectory(destination);

        using var zipStream1 = CreateZipStream(archive =>
        {
            archive.CreateEntry("root/.");
            AddTextEntry(archive, "root/src/App.cs", "class App {}");
        });

        await RepositorySourceService.ExtractArchiveInternalAsync(zipStream1, destination, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(destination, "src", "App.cs")));
        Assert.False(File.Exists(Path.Combine(destination, "root")));
        Assert.False(Directory.Exists(Path.Combine(destination, "root")));

        var destination2 = Path.Combine(_testAnalysisDir, "repository2");
        Directory.CreateDirectory(destination2);

        using var zipStream2 = CreateZipStream(archive =>
        {
            archive.CreateEntry("root/.");
            AddTextEntry(archive, "other/App.cs", "class App {}");
        });

        await RepositorySourceService.ExtractArchiveInternalAsync(zipStream2, destination2, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(destination2, "root")));
        Assert.True(Directory.Exists(Path.Combine(destination2, "root")));
        Assert.True(File.Exists(Path.Combine(destination2, "other", "App.cs")));
    }

    private static void AddTextEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }
}
