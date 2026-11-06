using Romd.Domain.Source.Rom;
using Romd.Media;
using Romd.Media.Rvz;
using Romd.Storage;

namespace Romd.Infrastructure.Storage;

internal sealed class RvzMediaAdapter : IRomMediaAdapter
{
    public RomRepresentationEncoding Encoding => RomRepresentationEncoding.Rvz;
    public bool ReusesIdleImages => true;
    public async ValueTask<IRandomAccessContent> OpenAsync(RomArtifactSource source, RomMediaOpenContext context,
        MediaBlockCache cache, CancellationToken ct) =>
        await RvzImage.OpenAsync(await source.OpenRandomAccessAsync(ct), new RvzReadOptions(), cache,
            context.Artifact?.ToString(), ct);
}
