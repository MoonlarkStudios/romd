using Romd.Domain.Source.Rom;
using Romd.Media;
using Romd.Storage;

namespace Romd.Infrastructure.Storage;

internal sealed class IdentityMediaAdapter : IRomMediaAdapter
{
    public RomRepresentationEncoding Encoding => RomRepresentationEncoding.Identity;
    public bool ReusesIdleImages => false;
    public ValueTask<IRandomAccessContent> OpenAsync(RomArtifactSource source, RomMediaOpenContext context,
        MediaBlockCache cache, CancellationToken ct) => source.OpenRandomAccessAsync(ct);
}
