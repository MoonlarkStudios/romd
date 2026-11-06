namespace Romd.Infrastructure.Export;

/// <summary>
/// .NET 10 closes ZIP entries with a small synchronous data-descriptor write even
/// when OpenAsync/DisposeAsync are used. Buffer only those metadata writes and
/// drain asynchronously; never enable synchronous I/O on an HTTP response.
/// </summary>
internal sealed class ArchiveOutputStream(Stream destination, CancellationToken cancellationToken) : Stream
{
    private readonly MemoryStream _metadata = new();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_metadata.Length + buffer.Length > 64 * 1024)
            throw new IOException("ZIP metadata exceeded the synchronous-write buffer.");
        _metadata.Write(buffer);
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await DrainAsync();
        await destination.WriteAsync(buffer, cancellationToken);
    }
    private async Task DrainAsync()
    {
        if (_metadata.Length == 0)
            return;
        await destination.WriteAsync(_metadata.GetBuffer().AsMemory(0, (int)_metadata.Length), cancellationToken);
        _metadata.SetLength(0);
        _metadata.Position = 0;
    }
    public override async Task FlushAsync(CancellationToken ct)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await DrainAsync();
        await destination.FlushAsync(cancellationToken);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _metadata.Dispose();
        base.Dispose(disposing);
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
