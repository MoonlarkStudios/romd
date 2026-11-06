using System.Buffers;
using Romd.Admin.Application.Hashing;
using Romd.Infrastructure.Hashing;

namespace Romd.Infrastructure.Storage;

/// <summary>
/// Measures one complete artifact read while preserving CAS seekability and source ownership.
/// CAS may reread the source for raw fallback or publication recovery; those reads are not appended.
/// </summary>
internal sealed class ArtifactHashReadStream : Stream
{
    private readonly Stream _source;
    private readonly long _startPosition;
    private MultiHashReadStream? _hashing;

    public ArtifactHashReadStream(Stream source)
    {
        _source = source;
        _startPosition = source.CanSeek ? source.Position : 0;
        _hashing = CreateHashingStream();
    }

    public ContentHash? Hashes { get; private set; }
    public long? MeasuredSize { get; private set; }
    public override bool CanRead => _source.CanRead;
    public override bool CanSeek => _source.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _source.Length;
    public override long Position
    {
        get => _source.Position;
        set
        {
            if (_hashing is not null)
            {
                if (value != _startPosition)
                    throw new InvalidDataException("An incomplete artifact measurement may only restart from its beginning.");
                if (_hashing.BytesRead > 0)
                {
                    _hashing.Dispose();
                    _hashing = CreateHashingStream();
                }
            }
            _source.Position = value;
        }
    }

    // A deduplicated CAS object may skip the input copy. Unknown ledger facts then require a real,
    // bounded measurement before publication; this never decodes or uses logical ROM hashes.
    public async Task EnsureMeasuredAsync(long expectedSize, CancellationToken ct)
    {
        if (Hashes is not null)
            return;
        if (CanSeek)
            Position = _startPosition;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(131072);
        try
        {
            while (await ReadAsync(buffer.AsMemory(0, 131072), ct) != 0)
                if (_hashing is { BytesRead: var read } && read > expectedSize)
                    throw new InvalidDataException("Artifact measurement exceeded the staged length.");
            if (MeasuredSize != expectedSize)
                throw new InvalidDataException("Artifact measurement differs from the staged length.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = (_hashing as Stream ?? _source).Read(buffer, offset, count);
        if (read == 0 && count > 0)
            CompleteMeasurement();
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int read = await (_hashing as Stream ?? _source).ReadAsync(buffer, ct);
        if (read == 0 && buffer.Length > 0)
            CompleteMeasurement();
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    private MultiHashReadStream CreateHashingStream() =>
        new(_source, HashAlgorithms.Sha256 | HashAlgorithms.Md5 | HashAlgorithms.Sha1, leaveOpen: true);

    private void CompleteMeasurement()
    {
        if (_hashing is null)
            return;
        MeasuredSize = _hashing.BytesRead;
        Hashes = _hashing.GetComputedHashes();
        _hashing.Dispose();
        _hashing = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _hashing?.Dispose();
        base.Dispose(disposing);
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin)
    {
        long position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(Position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        Position = position;
        return position;
    }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
