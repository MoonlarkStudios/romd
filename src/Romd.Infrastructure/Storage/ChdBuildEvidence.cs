using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Media.PlayStation;

namespace Romd.Infrastructure.Storage;

/// <summary>Projects verified native build identities onto the tuples persisted as evidence.</summary>
/// <remarks>Binary bytes stay with the runtime identities; evidence keys on source pin, recipe version and RID.</remarks>
internal static class ChdBuildEvidence
{
    public static RomChdDecoderBuild Decoder(ChdNativeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new(identity.Rid, Sha1.Parse(identity.UpstreamCommit), identity.RecipeVersion);
    }

    public static RomChdEncoderBuild Encoder(ChdmanToolIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new(identity.Rid, identity.MameTag, Sha256.Parse(identity.MameSourceSha256), identity.RecipeVersion);
    }
}
