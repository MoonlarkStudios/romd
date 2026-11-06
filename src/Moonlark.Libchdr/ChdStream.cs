namespace Moonlark.Libchdr;

/// <summary>A read-only seekable synchronous stream over a CHD's logical bytes.</summary>
/// <remarks>The stream and decoder share a hunk cache and are not thread-safe: a read that overlaps another read of the
/// decoder throws <see cref="InvalidOperationException"/>.
/// Native I/O is synchronous, and the asynchronous read methods throw <see cref="NotSupportedException"/> even
/// though <see cref="CanRead"/> is true, so asynchronous consumers such as <c>CopyToAsync</c> or
/// <c>StreamReader.ReadToEndAsync</c> fail. Use the synchronous methods, on a worker thread if needed.</remarks>
public sealed class ChdStream : Stream
{
    private readonly ChdFile _file;
    private readonly bool _leaveOpen;
    private long _position;
    private bool _disposed;

    /// <summary>Creates a stream that owns the decoder.</summary>
    /// <param name="file">The opened decoder.</param>
    public ChdStream(ChdFile file) : this(file, false) { }

    /// <summary>Creates a stream with explicit decoder ownership.</summary>
    /// <param name="file">The opened decoder.</param>
    /// <param name="leaveOpen">Whether stream disposal preserves the decoder.</param>
    public ChdStream(ChdFile file, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(file);
        _ = file.Header;
        _file = file;
        _leaveOpen = leaveOpen;
    }

    /// <summary>True until this stream or its decoder is disposed, as the Stream contract requires.</summary>
    public override bool CanRead => !_disposed && !_file.IsDisposed;
    /// <summary>True until this stream or its decoder is disposed, as the Stream contract requires.</summary>
    public override bool CanSeek => !_disposed && !_file.IsDisposed;
    /// <summary>Always false; CHD streams are read-only.</summary>
    public override bool CanWrite => false;
    /// <inheritdoc/>
    public override long Length { get { ThrowIfDisposed(); return checked((long)_file.Header.LogicalBytes); } }
    /// <inheritdoc/>
    public override long Position
    {
        get { ThrowIfDisposed(); return _position; }
        set { ThrowIfDisposed(); ArgumentOutOfRangeException.ThrowIfNegative(value); _position = value; }
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        int read = _file.ReadAt(_position, buffer);
        _position = checked(_position + read);
        return read;
    }

    /// <inheritdoc/>
    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 0 ? -1 : one[0];
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        long start = origin switch
        {
            SeekOrigin.Begin => 0,
            SeekOrigin.Current => _position,
            SeekOrigin.End => Length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        long position;
        try
        { position = checked(start + offset); }
        catch (OverflowException error) { throw new IOException("CHD stream seek overflows a 64-bit offset.", error); }
        if (position < 0)
            throw new IOException("CHD stream cannot seek before byte zero.");
        _position = position;
        return position;
    }

    /// <inheritdoc/>
    public override void Flush() => ThrowIfDisposed();
    /// <inheritdoc/>
    public override void SetLength(long value) { ThrowIfDisposed(); throw new NotSupportedException("CHD streams are read-only."); }
    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) { ThrowIfDisposed(); throw new NotSupportedException("CHD streams are read-only."); }
    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer) { ThrowIfDisposed(); throw new NotSupportedException("CHD streams are read-only."); }
    /// <summary>Unsupported; throws <see cref="InvalidOperationException"/>, as BCL streams without timeouts do.</summary>
    public override int ReadTimeout { get { ThrowIfDisposed(); throw NoTimeouts(); } set { ThrowIfDisposed(); throw NoTimeouts(); } }
    /// <summary>Unsupported; throws <see cref="InvalidOperationException"/>, as BCL streams without timeouts do.</summary>
    public override int WriteTimeout { get { ThrowIfDisposed(); throw NoTimeouts(); } set { ThrowIfDisposed(); throw NoTimeouts(); } }
    /// <summary>Always false; CHD streams have no timeouts.</summary>
    public override bool CanTimeout => false;

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { ThrowIfDisposed(); throw new NotSupportedException("CHD native reads are synchronous; use Read."); }
    /// <inheritdoc/>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    { ThrowIfDisposed(); throw new NotSupportedException("CHD native reads are synchronous; use Read."); }

    /// <summary>Completes immediately, as <see cref="Flush"/> does nothing for a read-only stream; no work is queued.</summary>
    /// <param name="cancellationToken">A token whose cancellation yields a canceled task.</param>
    /// <returns>A completed or canceled task.</returns>
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        // Cancellation wins over disposal, as FileStream, BufferedStream and MemoryStream report it.
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);
        ThrowIfDisposed();
        return Task.CompletedTask;
    }
    /// <inheritdoc/>
    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    { ThrowIfDisposed(); throw new NotSupportedException("CHD native reads are synchronous; use CopyTo."); }
    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { ThrowIfDisposed(); throw new NotSupportedException("CHD streams are read-only."); }
    /// <inheritdoc/>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    { ThrowIfDisposed(); throw new NotSupportedException("CHD streams are read-only."); }
    /// <inheritdoc/>
    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    { ThrowIfDisposed(); throw new NotSupportedException("CHD native reads are synchronous; use Read."); }
    /// <inheritdoc/>
    public override int EndRead(IAsyncResult asyncResult)
    { ThrowIfDisposed(); throw new NotSupportedException("CHD native reads are synchronous; use Read."); }
    /// <inheritdoc/>
    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    { ThrowIfDisposed(); throw new NotSupportedException("CHD streams are read-only."); }
    /// <inheritdoc/>
    public override void EndWrite(IAsyncResult asyncResult)
    { ThrowIfDisposed(); throw new NotSupportedException("CHD streams are read-only."); }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        { if (disposing && !_leaveOpen) _file.Dispose(); }
        finally { base.Dispose(disposing); }
    }

    private void ThrowIfDisposed() { ObjectDisposedException.ThrowIf(_disposed, this); _file.ThrowIfDisposed(); }
    private static InvalidOperationException NoTimeouts() => new("CHD streams do not support timeouts.");
}
