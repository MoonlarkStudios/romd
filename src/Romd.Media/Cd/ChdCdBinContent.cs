using System.Buffers;
using Romd.Storage;

namespace Romd.Media.Cd;

/// <summary>A closed whole-track BIN recipe: 2,352 bytes from each 2,448-byte CHD frame, with optional 16-bit audio swap.</summary>
public sealed record ChdCdBinExtent(long FirstFrame, int Frames, bool SwapAudio);

/// <summary>Selected original-file projection over a bounded shared CHD block device. No adjacent member, padding or
/// subcode bytes can escape. The caller validates retained evidence and authorizes this member before opening.</summary>
public sealed class ChdCdBinContent : IRandomAccessContent
{
    private readonly IRandomAccessContent _source;
    private readonly ChdCdBinExtent[] _extents;
    private readonly SemaphoreSlim _serial = new(1);
    private readonly object _gate = new();
    private bool _closing;
    private Task? _disposal;

    private ChdCdBinContent(IRandomAccessContent source, ChdCdBinExtent[] extents)
    { _source = source; _extents = extents; Length = extents.Sum(extent => (long)extent.Frames * 2352); }

    /// <summary>Takes ownership, including on invalid recipe. Does not infer original file boundaries.</summary>
    public static async ValueTask<ChdCdBinContent> CreateAsync(IRandomAccessContent source,
        IReadOnlyList<ChdCdBinExtent> extents)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(extents);
            if (source.Length is < 2448 or > ChdCdLimits.MaximumLogicalBytes || source.Length % 2448 != 0 ||
                extents.Count is < 1 or > 99)
                throw new InvalidDataException("CD BIN projection requires bounded frame geometry and whole-track extents.");
            var copy = extents.ToArray();
            long end = 0;
            foreach (var extent in copy)
            {
                if (extent is null || extent.FirstFrame < end || extent.Frames is < 1 or > (int)ChdCdLimits.MaximumFrames ||
                    extent.FirstFrame > source.Length / 2448 - extent.Frames)
                    throw new InvalidDataException("CD BIN extent exceeds the retained source or overlaps a previous track.");
                end = extent.FirstFrame + extent.Frames;
            }
            return new(source, copy);
        }
        catch { if (source is not null) await source.DisposeAsync(); throw; }
    }

    public long Length { get; }
    public ContentReadIntegrity Integrity => ContentReadIntegrity.None;

    public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        await _serial.WaitAsync(ct);
        byte[]? sector = null;
        try
        {
            lock (_gate)
                ObjectDisposedException.ThrowIf(_closing, this);
            ct.ThrowIfCancellationRequested();
            if (offset >= Length || destination.IsEmpty)
                return 0;
            int count = (int)Math.Min(destination.Length, Length - offset);
            sector = ArrayPool<byte>.Shared.Rent(2352);
            long start = 0;
            int copied = 0;
            foreach (var extent in _extents)
            {
                long end = start + (long)extent.Frames * 2352;
                while (offset + copied < end && copied < count)
                {
                    ct.ThrowIfCancellationRequested();
                    long within = offset + copied - start;
                    if (within < 0)
                        throw new InvalidDataException("CD member recipe has a gap.");
                    long frame = extent.FirstFrame + within / 2352;
                    int position = (int)(within % 2352);
                    await _source.ReadExactlyAtAsync(frame * 2448, sector.AsMemory(0, 2352), ct);
                    if (extent.SwapAudio)
                        for (int i = 0; i < 2352; i += 2)
                            (sector[i], sector[i + 1]) = (sector[i + 1], sector[i]);
                    int take = Math.Min(count - copied, 2352 - position);
                    sector.AsMemory(position, take).CopyTo(destination[copied..]);
                    copied += take;
                }
                if (copied == count)
                    break;
                start = end;
            }
            if (copied != count)
                throw new InvalidDataException("CD member recipe ended before its declared length.");
            return copied;
        }
        finally
        {
            if (sector is not null)
                ArrayPool<byte>.Shared.Return(sector);
            _serial.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null)
                return new(_disposal);
            _closing = true;
            _disposal = DisposeCoreAsync();
            return new(_disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _serial.WaitAsync();
        try
        { await _source.DisposeAsync(); } // Block device joins surviving native work before releasing its raw snapshot.
        finally { _serial.Release(); }
    }
}
