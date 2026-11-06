namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Enforces the host's header-only responsibility; reading any payload or seeking around the bound fails the test.</summary>
internal sealed class BoundedHeaderReadStream(Stream inner, int maximumBytes, Action<int> observed) : Stream
{
    private int readBytes;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => readBytes; set => throw new NotSupportedException(); }
    private void RequireBound(int count)
    {
        if (count > maximumBytes - readBytes)
            throw new InvalidOperationException("The host must not read beyond the bounded CHD header.");
    }
    public override int Read(Span<byte> buffer)
    {
        RequireBound(buffer.Length);
        int count = inner.Read(buffer);
        readBytes += count;
        observed(count);
        return count;
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        RequireBound(buffer.Length);
        int count = await inner.ReadAsync(buffer, ct);
        readBytes += count;
        observed(count);
        return count;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); GC.SuppressFinalize(this); }
}
