namespace ConvertApi.Core.Storage;

/// <summary>
/// A forward-only read stream that yields <c>prefix</c> and then the rest of <c>inner</c>.
/// Lets the upload path peek at a file's first bytes (to check its signature) and still
/// stream the whole file to storage. Does not own <c>inner</c>.
/// </summary>
public sealed class PrefixedReadStream(ReadOnlyMemory<byte> prefix, Stream inner) : Stream
{
    private ReadOnlyMemory<byte> _remainingPrefix = prefix;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_remainingPrefix.IsEmpty)
        {
            return inner.Read(buffer);
        }

        return TakeFromPrefix(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remainingPrefix.IsEmpty)
        {
            return inner.ReadAsync(buffer, cancellationToken);
        }

        return ValueTask.FromResult(TakeFromPrefix(buffer.Span));
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int TakeFromPrefix(Span<byte> buffer)
    {
        var count = Math.Min(buffer.Length, _remainingPrefix.Length);
        _remainingPrefix.Span[..count].CopyTo(buffer);
        _remainingPrefix = _remainingPrefix[count..];
        return count;
    }
}
