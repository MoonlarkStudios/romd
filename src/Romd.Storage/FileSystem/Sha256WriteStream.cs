using System.Security.Cryptography;
using Romd.Domain.Hashing;

namespace Romd.Storage.FileSystem;

/// <summary>
///     A write stream that computes SHA-256 hash as data flows through.
/// </summary>
internal sealed class Sha256WriteStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly IncrementalHash _sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _disposed;
    private bool _finalized;

    public Sha256WriteStream(Stream inner, bool leaveOpen = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _leaveOpen = leaveOpen;
    }

    public long BytesWritten { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed && !_finalized;
    public override long Length => BytesWritten;

    public override long Position
    {
        get => BytesWritten;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfCannotWrite();

        _sha256.AppendData(buffer);
        _inner.Write(buffer);
        BytesWritten += buffer.Length;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => await WriteAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        ThrowIfCannotWrite();

        _sha256.AppendData(buffer.Span);
        await _inner.WriteAsync(buffer, ct).ConfigureAwait(false);
        BytesWritten += buffer.Length;
    }

    /// <summary>
    ///     Finalizes hash computation and returns the SHA-256 hash.
    ///     Can only be called once. After calling, no more writes are allowed.
    /// </summary>
    /// <exception cref="InvalidOperationException">Already finalized or disposed.</exception>
    public Sha256 GetSha256Hash()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_finalized)
        {
            throw new InvalidOperationException(
                "Hash already finalized. GetSha256Hash can only be called once per stream.");
        }

        _finalized = true;

        Span<byte> hash = stackalloc byte[Sha256.ByteLength];
        _sha256.GetHashAndReset(hash);
        return Sha256.FromSpan(hash);
    }

    private void ThrowIfCannotWrite()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_finalized)
        {
            throw new InvalidOperationException("Cannot write after GetSha256Hash has been called.");
        }
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _sha256.Dispose();

                if (!_leaveOpen)
                {
                    _inner.Dispose();
                }
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }
}
