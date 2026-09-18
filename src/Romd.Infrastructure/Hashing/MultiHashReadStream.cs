using System.Security.Cryptography;
using Romd.Admin.Application.Hashing;
using Romd.Domain.Hashing;
using IoHashing = System.IO.Hashing;

namespace Romd.Infrastructure.Hashing;

/// <summary>
///     A read stream wrapper that computes multiple hash algorithms as data is read.
/// </summary>
/// <remarks>
///     This stream computes hashes incrementally during read operations, enabling
///     single-pass hashing without buffering the entire content. The computed hashes
///     are available after the stream has been fully read via <see cref="GetComputedHashes" />.
/// </remarks>
public sealed class MultiHashReadStream : Stream
{
    private readonly HashAlgorithms _algorithms;
    private readonly IoHashing.Crc32? _crc32;
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly IncrementalHash? _md5;
    private readonly IncrementalHash? _sha1;

    private readonly IncrementalHash? _sha256;

    private bool _disposed;
    private bool _finalized;

    /// <summary>
    ///     Creates a new multi-hash read stream.
    /// </summary>
    /// <param name="inner">The underlying stream to read from.</param>
    /// <param name="algorithms">Which hash algorithms to compute.</param>
    /// <param name="leaveOpen">Whether to leave the inner stream open when this stream is disposed.</param>
    public MultiHashReadStream(Stream inner, HashAlgorithms algorithms, bool leaveOpen = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _algorithms = algorithms;
        _leaveOpen = leaveOpen;

        if (!inner.CanRead)
        {
            throw new ArgumentException("Stream must be readable.", nameof(inner));
        }

        if ((algorithms & HashAlgorithms.Sha256) != 0)
        {
            _sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }

        if ((algorithms & HashAlgorithms.Sha1) != 0)
        {
            _sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        }

        if ((algorithms & HashAlgorithms.Md5) != 0)
        {
            _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        }

        if ((algorithms & HashAlgorithms.Crc32) != 0)
        {
            _crc32 = new IoHashing.Crc32();
        }
    }

    /// <summary>
    ///     Gets the total number of bytes read from the stream.
    /// </summary>
    public long BytesRead { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.CanSeek ? _inner.Length : throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    ///     Gets the computed hashes after the stream has been fully read.
    /// </summary>
    /// <returns>A ContentHash containing all requested hashes.</returns>
    /// <exception cref="InvalidOperationException">Called more than once or before stream is exhausted.</exception>
    public ContentHash GetComputedHashes()
    {
        if (_finalized)
        {
            throw new InvalidOperationException("GetComputedHashes can only be called once.");
        }

        _finalized = true;

        Sha256 sha256 = default;
        Sha1 sha1 = default;
        Md5 md5 = default;
        Crc32 crc32 = default;

        if (_sha256 is not null)
        {
            Span<byte> hash = stackalloc byte[Sha256.ByteLength];
            _sha256.GetHashAndReset(hash);
            sha256 = Sha256.FromSpan(hash);
        }

        if (_sha1 is not null)
        {
            Span<byte> hash = stackalloc byte[Sha1.ByteLength];
            _sha1.GetHashAndReset(hash);
            sha1 = Sha1.FromSpan(hash);
        }

        if (_md5 is not null)
        {
            Span<byte> hash = stackalloc byte[Md5.ByteLength];
            _md5.GetHashAndReset(hash);
            md5 = Md5.FromSpan(hash);
        }

        if (_crc32 is not null)
        {
            Span<byte> hash = stackalloc byte[Crc32.ByteLength];
            _crc32.GetCurrentHash(hash);
            crc32 = Crc32.FromLittleEndian(hash);
        }

        return new ContentHash { Sha256 = sha256, Sha1 = sha1, Md5 = md5, Crc32 = crc32 };
    }

    private void ProcessBytes(ReadOnlySpan<byte> data)
    {
        _sha256?.AppendData(data);
        _sha1?.AppendData(data);
        _md5?.AppendData(data);
        _crc32?.Append(data);

        BytesRead += data.Length;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int bytesRead = _inner.Read(buffer, offset, count);
        if (bytesRead > 0)
        {
            ProcessBytes(buffer.AsSpan(offset, bytesRead));
        }

        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int bytesRead = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
        if (bytesRead > 0)
        {
            ProcessBytes(buffer.AsSpan(offset, bytesRead));
        }

        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int bytesRead = await _inner.ReadAsync(buffer, cancellationToken);
        if (bytesRead > 0)
        {
            ProcessBytes(buffer.Span[..bytesRead]);
        }

        return bytesRead;
    }

    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (disposing)
        {
            _sha256?.Dispose();
            _sha1?.Dispose();
            _md5?.Dispose();

            if (!_leaveOpen)
            {
                _inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
