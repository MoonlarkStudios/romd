using Romd.Domain.ReferenceData;

namespace Romd.Persistence.Systems;

/// <summary>Admin setup facts keyed by the stable ROMD system identity.</summary>
internal static class BuiltInSystemHardware
{
    // Release years are first public hardware releases. Uncertain dates and generations stay empty.
    private static readonly Dictionary<string, SystemHardware> Facts = new(StringComparer.Ordinal)
    {
        ["2600"] = new("console", 2, 1977),
        ["32x"] = new("add-on", 5, 1994),
        ["3do"] = new("console", 5, 1993),
        ["3ds"] = new("handheld", 8, 2011),
        ["5200"] = new("console", 2, 1982),
        ["7800"] = new("console", 3),
        ["amiga"] = new("computer"),
        ["arcade"] = new("arcade"),
        ["atarist"] = new("computer"),
        ["c64"] = new("computer"),
        ["cdi"] = new("media-player"),
        ["coleco"] = new("console", 2, 1982),
        ["dc"] = new("console", 6, 1998),
        ["dos"] = new("software"),
        ["gb"] = new("handheld", 4, 1989),
        ["gba"] = new("handheld", 6, 2001),
        ["gbc"] = new("handheld", 5, 1998),
        ["gc"] = new("console", 6, 2001),
        ["genesis"] = new("console", 4, 1988),
        ["gg"] = new("handheld", 4, 1990),
        ["intv"] = new("console", 2),
        ["jaguar"] = new("console", 5, 1993),
        ["lynx"] = new("handheld", 4),
        ["msx"] = new("computer"),
        ["n64"] = new("console", 5, 1996),
        ["nds"] = new("handheld", 7, 2004),
        ["neogeo"] = new("console", 4),
        ["neogeocd"] = new("console", 4),
        ["nes"] = new("console", 3, 1983),
        ["ngp"] = new("handheld"),
        ["ngpc"] = new("handheld"),
        ["ps2"] = new("console", 6, 2000),
        ["ps3"] = new("console", 7, 2006),
        ["ps4"] = new("console", 8, 2013),
        ["ps5"] = new("console", 9, 2020),
        ["psp"] = new("handheld", 7, 2004),
        ["psx"] = new("console", 5, 1994),
        ["saturn"] = new("console", 5, 1994),
        ["segacd"] = new("add-on", 4, 1991),
        ["sgx"] = new("console", 4),
        ["sms"] = new("console", 3),
        ["snes"] = new("console", 4, 1990),
        ["switch"] = new("hybrid", 8, 2017),
        ["tg16"] = new("console", 4),
        ["tgcd"] = new("add-on", 4),
        ["vb"] = new("console", 5, 1995),
        ["vita"] = new("handheld", 8, 2011),
        ["wii"] = new("console", 7, 2006),
        ["wiiu"] = new("console", 8, 2012),
        ["ws"] = new("handheld"),
        ["wsc"] = new("handheld"),
        ["x360"] = new("console", 7, 2005),
        ["xbox"] = new("console", 6, 2001),
        ["xone"] = new("console", 8, 2013),
        ["xsx"] = new("console", 9, 2020),
        ["zxs"] = new("computer")
    };

    internal static SystemHardware? Get(string key) => Facts.GetValueOrDefault(key);
}
