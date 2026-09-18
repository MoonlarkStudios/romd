namespace Romd.Admin.Application.Ingestion.Extraction;

/// <summary>
///     Cumulative allowance charged live while archives extract. Every created filesystem entry
///     (file, explicit directory entry, or implicit parent directory) charges one entry at its
///     creation site, and every write chunk charges bytes before it reaches disk — so extraction
///     stops AS a limit is crossed instead of detecting the overdraw afterwards. Implementations
///     must be thread-safe.
/// </summary>
public interface IExtractionQuota
{
    /// <summary>
    ///     Charges one filesystem entry (file or directory) about to be created. Returns false
    ///     without charging when the entry budget is exhausted.
    /// </summary>
    bool TryChargeEntry();

    /// <summary>
    ///     Charges bytes about to be written. Returns false without charging when the chunk would
    ///     cross the remaining byte budget.
    /// </summary>
    bool TryChargeBytes(long byteCount);
}

/// <summary>
///     Thrown by extractors when creating an entry or writing a chunk would exceed the
///     <see cref="IExtractionQuota" />. Partial output may remain on disk; callers convert this
///     into their budget-exceeded failure and clean the workspace.
/// </summary>
public sealed class ExtractionQuotaExceededException(string message) : Exception(message);

/// <summary>
///     Write-only stream wrapper that charges an <see cref="IExtractionQuota" /> for each chunk
///     before delegating to the inner stream, throwing <see cref="ExtractionQuotaExceededException" />
///     when a write would cross the byte budget — the chunk that crosses the boundary is refused
///     entirely, so bytes on disk never exceed the budget. Owns and disposes the inner stream.
/// </summary>
public sealed class QuotaEnforcingStream(Stream inner, IExtractionQuota quota) : Stream
{
    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        Charge(count);
        inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Charge(buffer.Length);
        inner.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        Charge(1);
        inner.WriteByte(value);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Charge(buffer.Length);
        return inner.WriteAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }

    private void Charge(long byteCount)
    {
        if (!quota.TryChargeBytes(byteCount))
        {
            throw new ExtractionQuotaExceededException(
                $"Writing {byteCount:N0} more bytes would exceed the remaining extraction byte budget.");
        }
    }
}
