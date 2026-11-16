using Infrastructure.Storage;
using Xunit;

namespace Backend.Tests;

public sealed class WorkspaceFileLockProviderTests : IDisposable
{
    private readonly string _testStorageRoot;
    private readonly WorkspaceFileLockProvider _provider;

    public WorkspaceFileLockProviderTests()
    {
        _testStorageRoot = Path.Combine(Path.GetTempPath(), "test_file_locks_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testStorageRoot);
        _provider = new WorkspaceFileLockProvider(_testStorageRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testStorageRoot))
            {
                Directory.Delete(_testStorageRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void Shared_TwoWorkers_CanAcquireSimultaneously()
    {
        const int analysisId = 101;

        using var worker1 = _provider.TryAcquireShared(analysisId);
        using var worker2 = _provider.TryAcquireShared(analysisId);

        Assert.NotNull(worker1);
        Assert.NotNull(worker2);
        Assert.True(worker1.CanRead);
        Assert.True(worker2.CanRead);
    }

    [Fact]
    public void Exclusive_Blocked_WhileSharedLockHeld()
    {
        const int analysisId = 102;

        using var worker = _provider.TryAcquireShared(analysisId);
        Assert.NotNull(worker);

        using var cleaner = _provider.TryAcquireExclusive(analysisId);
        Assert.Null(cleaner);
    }

    [Fact]
    public void Exclusive_Succeeds_AfterSharedLockReleased()
    {
        const int analysisId = 103;

        var worker1 = _provider.TryAcquireShared(analysisId);
        var worker2 = _provider.TryAcquireShared(analysisId);
        Assert.NotNull(worker1);
        Assert.NotNull(worker2);

        worker1.Dispose();

        using (var cleanerAttempt1 = _provider.TryAcquireExclusive(analysisId))
        {
            Assert.Null(cleanerAttempt1);
        }

        worker2.Dispose();

        using var cleanerAttempt2 = _provider.TryAcquireExclusive(analysisId);
        Assert.NotNull(cleanerAttempt2);
    }

    [Fact]
    public void Shared_Blocked_WhileExclusiveLockHeld()
    {
        const int analysisId = 104;

        using var cleaner = _provider.TryAcquireExclusive(analysisId);
        Assert.NotNull(cleaner);

        using var worker = _provider.TryAcquireShared(analysisId);
        Assert.Null(worker);
    }

    [Fact]
    public void DoubleDispose_IsSafeAndIdempotent()
    {
        const int analysisId = 105;

        var stream = _provider.TryAcquireShared(analysisId);
        Assert.NotNull(stream);

        var exception1 = Record.Exception(() => stream.Dispose());
        var exception2 = Record.Exception(() => stream.Dispose());

        Assert.Null(exception1);
        Assert.Null(exception2);
    }

    [Fact]
    public void LockFile_NotDeleted_AfterRelease()
    {
        const int analysisId = 106;
        var lockFilePath = Path.Combine(_testStorageRoot, ".locks", $"{analysisId}.lock");

        var stream = _provider.TryAcquireShared(analysisId);
        Assert.NotNull(stream);
        stream.Dispose();

        Assert.True(File.Exists(lockFilePath));
    }

    [Fact]
    public void DifferentAnalysisIds_DoNotInterfere()
    {
        const int analysisId1 = 107;
        const int analysisId2 = 108;

        using var lock1 = _provider.TryAcquireExclusive(analysisId1);
        using var lock2 = _provider.TryAcquireExclusive(analysisId2);

        Assert.NotNull(lock1);
        Assert.NotNull(lock2);

        var lockPath1 = Path.Combine(_testStorageRoot, ".locks", $"{analysisId1}.lock");
        var lockPath2 = Path.Combine(_testStorageRoot, ".locks", $"{analysisId2}.lock");

        Assert.True(File.Exists(lockPath1));
        Assert.True(File.Exists(lockPath2));
        Assert.NotEqual(lockPath1, lockPath2);
    }
}
