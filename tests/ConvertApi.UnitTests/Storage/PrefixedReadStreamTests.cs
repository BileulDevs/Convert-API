using ConvertApi.Core.Storage;

namespace ConvertApi.UnitTests.Storage;

public sealed class PrefixedReadStreamTests
{
    [Fact]
    public async Task Read_ReturnsThePrefixThenTheInnerStream()
    {
        using var inner = new MemoryStream([4, 5, 6]);
        using var stream = new PrefixedReadStream(new byte[] { 1, 2, 3 }, inner);
        using var output = new MemoryStream();

        await stream.CopyToAsync(output, bufferSize: 2, TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3, 4, 5, 6], output.ToArray());
    }

    [Fact]
    public void Read_WithAnEmptyPrefix_ReadsTheInnerStream()
    {
        using var inner = new MemoryStream([7, 8]);
        using var stream = new PrefixedReadStream(ReadOnlyMemory<byte>.Empty, inner);
        var buffer = new byte[4];

        var read = stream.Read(buffer, 0, buffer.Length);

        Assert.Equal(2, read);
        Assert.Equal([7, 8], buffer[..2]);
    }

    [Fact]
    public void Stream_IsReadOnlyAndForwardOnly()
    {
        using var inner = new MemoryStream();
        using var stream = new PrefixedReadStream(new byte[] { 1 }, inner);

        Assert.True(stream.CanRead);
        Assert.False(stream.CanWrite);
        Assert.False(stream.CanSeek);
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.Length);
    }
}
