namespace Romd.Storage.FileSystem;

/// <summary>
///     A read stream wrapper that enforces a maximum size during reading.
///     Used to protect against oversized content from non-seekable streams.
/// </summary>
internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly long _maxSize;
    private long _bytesRead;
    private bool _disposed;

    public BoundedReadStream(Stream inner, long maxSize, bool leaveOpen = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSize);

        _maxSize = maxSize;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => !_disposed && _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _bytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfOverflow();

        long remaining = _maxSize - _bytesRead;
        if (remaining <= 0)
        {
            return 0;
        }

        int bytesToRead = (int)Math.Min(buffer.Length, remaining);
        int read = _inner.Read(buffer[..bytesToRead]);
        _bytesRead += read;

        // If we're at the limit, check for more data
        if (_bytesRead == _maxSize && read > 0)
        {
            Span<byte> probe = stackalloc byte[1];
            if (_inner.Read(probe) > 0)
            {
                _bytesRead++;
                ThrowIfOverflow();
            }
        }

        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => await ReadAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfOverflow();

        long remaining = _maxSize - _bytesRead;
        if (remaining <= 0)
        {
            return 0;
        }

        int bytesToRead = (int)Math.Min(buffer.Length, remaining);
        int read = await _inner.ReadAsync(buffer[..bytesToRead], ct).ConfigureAwait(false);
        _bytesRead += read;

        // If we're at the limit, check for more data
        if (_bytesRead == _maxSize && read > 0)
        {
            byte[] probe = new byte[1];
            if (await _inner.ReadAsync(probe, ct).ConfigureAwait(false) > 0)
            {
                _bytesRead++;
                ThrowIfOverflow();
            }
        }

        return read;
    }

    private void ThrowIfOverflow()
    {
        if (_bytesRead > _maxSize)
        {
            throw new InvalidOperationException(
                $"Content exceeds maximum allowed size of {_maxSize:N0} bytes.");
        }
    }

    public override void Flush() { }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing && !_leaveOpen)
            {
                _inner.Dispose();
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }
}
