using ErrorOr;
using Romd.Domain.Vfs;

namespace Romd.Consumer.Application.Vfs;

public static class VfsFormats
{
    public static ErrorOr<VfsFormat> Parse(string? value) => value switch
    {
        "stored" => VfsFormat.Stored,
        "original" => VfsFormat.Original,
        _ => VfsConnectionErrors.InvalidFormat()
    };

    public static bool IsValid(VfsFormat format) => format is VfsFormat.Stored or VfsFormat.Original;

    public static string ToWire(VfsFormat format) => format switch
    {
        VfsFormat.Stored => "stored",
        VfsFormat.Original => "original",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
}
