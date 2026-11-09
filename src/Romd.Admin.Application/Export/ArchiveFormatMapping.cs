using Romd.Application.Common.Content;
using Romd.Contracts.Management.Export;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Export;

public static class ArchiveFormatMapping
{
    /// <summary>Each format's wire value with its label from the format catalog, in the given order.</summary>
    public static IReadOnlyList<ArchiveFormat> ToContract(IEnumerable<RomDeliveryFormat> formats, RomDeliveryFormatCatalog catalog) =>
        formats.Select(format => new ArchiveFormat
        {
            Format = RomDeliveryFormats.ToWire(format),
            Label = catalog.Entry(format).Label
        }).ToArray();
}
