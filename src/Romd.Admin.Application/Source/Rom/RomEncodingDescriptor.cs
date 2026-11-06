using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>
///     What Application consumers need to know about one stored representation encoding, composed once per encoding.
///     <paramref name="RequiresRawStorage" /> declares a functional need for physically raw objects (CHD's synchronous
///     native reads); every other encoding leaves compressibility to content-addressed staging.
/// </summary>
public sealed record RomEncodingDescriptor(RomRepresentationEncoding Encoding, bool RequiresRawStorage);
