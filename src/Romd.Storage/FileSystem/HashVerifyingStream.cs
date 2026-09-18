using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Romd.Domain.Hashing;
using Romd.Storage.Exceptions;

namespace Romd.Storage.FileSystem;

/// <summary>
///     A read stream that verifies content integrity by computing SHA-256 as data is read.
///     Throws <see cref="ContentCorruptedException" /> if hash doesn't match when the stream is fully consumed.
/// </summary>
/// <remarks>
///     <para>
///         <b>Important:</b> Callers must either read the stream to completion or call
///         <see cref="ForceVerify" /> / <see cref="ForceVerifyAsync" /> to ensure integrity verification occurs.
///         Disposing without verification will skip integrity checking.
///     </para>
///     <para>
///         This stream always disposes its inner stream on disposal.
///     </para>
/// </remarks>
public sealed class HashVerifyingStream : Stream
{
    private readonly Sha256 _expectedHash;
    private readonly IncrementalHash _hasher;
    private readonly Stream _inner;
    private readonly StorageKey _key;
    private readonly ILogger? _logger;
    private bool _disposed;
    private bool _verificationFailed;
    private bool _verified;

    public HashVerifyingStream(Stream inner, StorageKey key, ILogger? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _key = key;
        _expectedHash = key.Hash;
        _logger = logger;
        _hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }

    /// <summary>
    ///     True if the stream has been fully read and hash verification passed.
    /// </summary>
    public bool IsVerified => _verified && !_verificationFailed;

    /// <summary>
    ///     Number of bytes read so far.
    /// </summary>
    public long TotalBytesRead { get; private set; }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => TotalBytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfVerificationFailed();

        int read = _inner.Read(buffer);
        ProcessBytesRead(buffer[..read], read == 0);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => await ReadAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfVerificationFailed();

        int read = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        ProcessBytesRead(buffer.Span[..read], read == 0);
        return read;
    }

    private void ProcessBytesRead(ReadOnlySpan<byte> data, bool endOfStream)
    {
        if (data.Length > 0)
        {
            _hasher.AppendData(data);
            TotalBytesRead += data.Length;
        }

        if (endOfStream)
        {
            VerifyHash();
        }
    }

    private void ThrowIfVerificationFailed()
    {
        if (_verificationFailed)
        {
            throw new InvalidOperationException(
                "Stream is in a failed state due to previous verification failure.");
        }
    }

    private void VerifyHash()
    {
        if (_verified)
        {
            return;
        }

        _verified = true;

        Span<byte> actualBytes = stackalloc byte[Sha256.ByteLength];
        _hasher.GetHashAndReset(actualBytes);
        var actualHash = Sha256.FromSpan(actualBytes);

        if (!actualHash.Equals(_expectedHash))
        {
            _verificationFailed = true;
            throw new ContentCorruptedException(_key, _expectedHash, actualHash);
        }
    }

    /// <summary>
    ///     Forces verification by reading and discarding all remaining data.
    ///     Call this if you need to verify integrity without processing all content yourself.
    /// </summary>
    /// <exception cref="ContentCorruptedException">Hash mismatch detected.</exception>
    /// <exception cref="ObjectDisposedException">Stream has been disposed.</exception>
    public void ForceVerify()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_verified)
        {
            return;
        }

        Span<byte> buffer = stackalloc byte[8192];
        while (Read(buffer) > 0) { }
    }

    /// <summary>
    ///     Asynchronously forces verification by reading and discarding all remaining data.
    /// </summary>
    /// <exception cref="ContentCorruptedException">Hash mismatch detected.</exception>
    /// <exception cref="ObjectDisposedException">Stream has been disposed.</exception>
    public async Task ForceVerifyAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_verified)
        {
            return;
        }

        byte[] buffer = new byte[8192];
        while (await ReadAsync(buffer, ct).ConfigureAwait(false) > 0) { }
    }

    public override void Flush() { }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                if (TotalBytesRead > 0 && !_verified)
                {
                    if (_logger?.IsEnabled(LogLevel.Debug) == true)
                    {
                        _logger.LogDebug(
                            "HashVerifyingStream for {StorageKey} disposed without completing verification. " +
                            "Read {BytesRead:N0} bytes. Call ForceVerify() or read to completion to ensure integrity",
                            _key,
                            TotalBytesRead);
                    }
                }

                _hasher.Dispose();
                _inner.Dispose();
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }
}
