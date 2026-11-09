using Moonlark.Libchdr;
using Romd.Storage;

namespace Romd.Media.Cd;

public static partial class ChdCdInspectionService
{
    /// <summary>Borrowed first-track view. A positive observation's bounded samples are compared with
    /// the full integrity pass, so another native read cannot substitute contradictory boot bytes.</summary>
    private sealed class BootTrack(ChdCdImage image, uint frames, CancellationToken ct) : ISynchronousRawContent
    {
        // Recognition reads at most 64 descriptors, 128 root blocks, eight config blocks and one executable header.
        private const int _maximumSampledSectors = 512;
        private readonly Dictionary<uint, byte[]> _samples = [];
        public long Length => (long)frames * 2352;
        public int ReadAt(long offset, Span<byte> destination)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            if (offset >= Length || destination.IsEmpty)
                return 0;
            int count = (int)Math.Min(destination.Length, Length - offset), copied = 0;
            while (copied < count)
            {
                ct.ThrowIfCancellationRequested();
                uint frame = checked((uint)((offset + copied) / 2352));
                int within = (int)((offset + copied) % 2352);
                if (!_samples.TryGetValue(frame, out var sector))
                {
                    if (_samples.Count >= _maximumSampledSectors)
                        throw new InvalidDataException("CD boot recognition exceeds its sampled-sector bound.");
                    sector = new byte[2352];
                    image.ReadSector(1, frame, ChdCdSectorFormat.Raw2352, sector);
                    _samples.Add(frame, sector);
                }
                int take = Math.Min(count - copied, 2352 - within);
                sector.AsSpan(within, take).CopyTo(destination[copied..]);
                copied += take;
            }
            return count;
        }
        public bool Matches(uint frame, ReadOnlySpan<byte> sector) =>
            !_samples.TryGetValue(frame, out var sample) || sector.SequenceEqual(sample);
        public void Dispose()
        {
            foreach (var sector in _samples.Values)
                Array.Clear(sector);
            _samples.Clear(); // The enclosing inspection owns the native image.
        }
    }
}
