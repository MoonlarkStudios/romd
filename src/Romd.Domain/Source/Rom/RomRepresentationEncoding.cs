namespace Romd.Domain.Source.Rom;

/// <summary>Encoding of an artifact. Identity means the exact logical ROM byte sequence.</summary>
public enum RomRepresentationEncoding
{
    Identity = 0,
    Rvz = 1,
    Chd = 2
}

public static class RomEncodingProfiles
{
    public const string Identity = "identity/v1";
    public const string UnknownImported = "imported-unknown/v1";
}

public static class RomRepresentationEncodings
{
    public static bool IsEncoded(this RomRepresentationEncoding encoding) =>
        Enum.IsDefined(encoding) && encoding != RomRepresentationEncoding.Identity;
}
