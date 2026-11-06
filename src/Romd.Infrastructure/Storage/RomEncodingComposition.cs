using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.Storage;

/// <summary>The production encoding registration shared by hosts and qualification probes.</summary>
public static class RomEncodingComposition
{
    /// <summary>
    ///     One entry per encoding, declared once; the descriptor, reader, read admission, and preparation
    ///     compositions all derive from it. Declaration order is preparation claim order: Identity's plain adapter claims
    ///     every upload, so it comes last.
    /// </summary>
    internal static IReadOnlyList<RomEncodingEntry> Entries { get; } =
    [
        // RVZ reads require an independent measurement from the resident managed decoder build.
        new(new(RomRepresentationEncoding.Rvz, RequiresRawStorage: false),
            _ => new RvzMediaAdapter(),
            dependencies => RomReadAdmission.Managed(dependencies.ChdDelivery),
            dependencies => [new RvzPreparationAdapter(dependencies.Rvz)], new("RVZ\u0001"u8, 4, ".rvz")),
        // Only CHD needs raw objects: libchdr reads hunks through synchronous raw content. RVZ and Identity stay on
        // ordinary staging so the content store decides compressibility. Without native assets CHD is unreadable and
        // its preparation stays claimed but refuses, so CHD uploads never fall through to another adapter; CHD
        new(new(RomRepresentationEncoding.Chd, RequiresRawStorage: true),
            dependencies => dependencies.Chd,
            dependencies => ChdReadAdmission.Create(dependencies.ChdDelivery),
            dependencies => [new ChdPreparationAdapter(dependencies.Chd, dependencies.Cd)], new("MComprHD"u8, 124, ".chd")),
        // Identity preparation preserves original bytes.
        new(new(RomRepresentationEncoding.Identity, RequiresRawStorage: false),
            _ => new IdentityMediaAdapter(),
            _ => RomReadAdmission.Identity,
            dependencies => [new IsoPreparationAdapter(dependencies.OriginalMedia), new IdentityPreparationAdapter(dependencies.Hashing)], null)
    ];

    public static RomEncodingRegistry CreateRegistry() => CreateRegistry(Entries);

    internal static RomEncodingRegistry CreateRegistry(IEnumerable<RomEncodingEntry> entries) =>
        new(entries.Select(entry => entry.Descriptor));
}
