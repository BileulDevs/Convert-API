using ConvertApi.Core.Storage;

namespace ConvertApi.UnitTests.Storage;

public sealed class LocalFileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "convertapi-tests", Guid.NewGuid().ToString("N"));
    private readonly LocalFileStorage _storage;

    public LocalFileStorageTests()
    {
        _storage = new LocalFileStorage(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_WritesTheContentUnderTheKey()
    {
        using var content = new MemoryStream([1, 2, 3, 4]);

        var size = await _storage.SaveAsync("jobs/abc/source", content, maxBytes: 10, TestContext.Current.CancellationToken);

        Assert.Equal(4, size);
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(Path.Combine(_root, "jobs", "abc", "source"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveAsync_AcceptsContentOfExactlyTheLimit()
    {
        using var content = new MemoryStream(new byte[10]);

        var size = await _storage.SaveAsync("exact", content, maxBytes: 10, TestContext.Current.CancellationToken);

        Assert.Equal(10, size);
    }

    [Fact]
    public async Task SaveAsync_OverTheLimit_ThrowsAndLeavesNothingBehind()
    {
        using var content = new MemoryStream(new byte[11]);

        var error = await Assert.ThrowsAsync<FileTooLargeException>(
            () => _storage.SaveAsync("too-big", content, maxBytes: 10, TestContext.Current.CancellationToken));

        Assert.Equal(10, error.MaxBytes);
        Assert.False(File.Exists(Path.Combine(_root, "too-big")));
    }

    [Fact]
    public async Task SaveAsync_WhenTheSourceFailsMidway_LeavesNothingBehind()
    {
        using var content = new FailingStream(bytesBeforeFailure: 5);

        await Assert.ThrowsAsync<IOException>(
            () => _storage.SaveAsync("aborted", content, maxBytes: 100, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(Path.Combine(_root, "aborted")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("trailing-newline\n")]
    [InlineData("../escape")]
    [InlineData("a/../../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/absolute")]
    [InlineData("with space")]
    [InlineData("UPPER")]
    public async Task SaveAsync_RejectsKeysThatAreNotPlainServerIds(string key)
    {
        using var content = new MemoryStream([1]);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _storage.SaveAsync(key, content, maxBytes: 10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveAsync_RefusesToOverwriteAnExistingFile()
    {
        using var first = new MemoryStream([1]);
        using var second = new MemoryStream([2]);
        await _storage.SaveAsync("once", first, maxBytes: 10, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(
            () => _storage.SaveAsync("once", second, maxBytes: 10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheFile_AndIgnoresAMissingOne()
    {
        using var content = new MemoryStream([1]);
        await _storage.SaveAsync("gone", content, maxBytes: 10, TestContext.Current.CancellationToken);

        await _storage.DeleteAsync("gone", TestContext.Current.CancellationToken);
        await _storage.DeleteAsync("gone", TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(_root, "gone")));
    }

    /// <summary>Yields some bytes, then fails like a connection the client dropped.</summary>
    private sealed class FailingStream(int bytesBeforeFailure) : MemoryStream(new byte[bytesBeforeFailure])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position < Length
                ? base.ReadAsync(buffer, cancellationToken)
                : throw new IOException("The client disconnected.");
    }
}
