using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Media.PlayStation;
using Romd.Storage;

namespace Romd.Infrastructure.Storage;

public sealed partial class ChdMediaAdapter
{
    /// <summary>Selected whole-file CD projection under the same decoder admission, native pool and cache as DVD.
    /// Takes ownership on every path. No second slot and no whole-container delivery capability.</summary>
    private async ValueTask<IRandomAccessContent> OpenCdAsync(ISynchronousRawContent source, RomReconstruction.CdMember member,
        Sha256 artifact, TimeSpan? timeout, CancellationToken ct)
    {
        bool transferred = false;
        IRandomAccessContent? device = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (member.Facts.ArtifactSha256 != artifact || source.Length != member.Facts.ArtifactSize ||
                member.Manifest.Members[member.Ordinal] is not RomCdBinMember bin)
                throw new InvalidDataException("CD projection requires the exact artifact and an admitted original BIN.");
            ValidateHeader(source, (RomContainerFacts.Chd)member.Facts.Container, ChdCdLimits.Preflight, ct);
            if (!member.HasDecoder(ChdBuildEvidence.Decoder(_verifyNative(ct))))
                throw new InvalidDataException("Deployed decoder disagrees with retained CD whole-member verification.");
            transferred = true;
            device = await _openDevice(source, _pool, ChdCdLimits.Preflight, artifact.ToString(), timeout, ct);
            var owned = device;
            device = null; // Projection creation owns it even on failure.
            var projection = await ChdCdBinContent.CreateAsync(owned, bin.Tracks.Select(n => member.Manifest.Tracks[n - 1])
                .Select(t => new ChdCdBinExtent(t.FirstFrame, t.Frames, t.Kind == RomCdTrackKind.Audio)).ToArray());
            return new Content(projection);
        }
        catch (TimeoutException)
        {
            if (device is not null)
                await device.DisposeAsync();
            else if (!transferred)
                source.Dispose();
            throw new ResourceCapacityException("CHD read capacity is occupied; retry later.");
        }
        catch
        {
            if (device is not null)
                await device.DisposeAsync();
            else if (!transferred)
                source.Dispose();
            throw;
        }
    }
}
