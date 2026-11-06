using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Source.Rom;

namespace Romd.Application.Tests.Source.Rom;

internal static class TestRomEncodings
{
    /// <summary>Mirrors the production registration: only CHD requires raw storage.</summary>
    internal static RomEncodingRegistry Create() => RequiringRaw(RomRepresentationEncoding.Chd);

    internal static RomEncodingRegistry RequiringRaw(params RomRepresentationEncoding[] raw) =>
        new(Enum.GetValues<RomRepresentationEncoding>().Select(encoding => new RomEncodingDescriptor(encoding, raw.Contains(encoding))));
}
