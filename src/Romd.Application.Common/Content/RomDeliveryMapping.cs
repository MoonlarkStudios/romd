using Romd.Contracts.Common.Models;

namespace Romd.Application.Common.Content;

public static class RomDeliveryMapping
{
    /// <summary>The option's contract, labelled from the format catalog.</summary>
    public static RomDeliveryFormat ToContract(this RomDeliveryOption option, RomDeliveryFormatCatalog formats)
    {
        var display = formats.Entry(option.Format);
        return new()
        {
            Format = RomDeliveryFormats.ToWire(option.Format),
            Label = display.Label,
            FileExtension = display.FileExtension,
            State = RomDeliveryFormats.ToWire(option.State),
            Filename = option.Target?.Filename,
            SizeBytes = option.Target is { } target ? (ByteCount)target.SizeBytes : null,
            Sha256 = option.Target?.Sha256.ToString(),
            Md5 = option.Target?.Md5?.ToString(),
            Sha1 = option.Target?.Sha1?.ToString(),
            ProfileVersion = option.ProfileVersion,
            RuntimeApplicability = option.RuntimeApplicability
        };
    }
}
