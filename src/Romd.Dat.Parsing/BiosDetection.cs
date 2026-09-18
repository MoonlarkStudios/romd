namespace Romd.Dat.Parsing;

/// <summary>
///     BIOS classification shared by all DAT format parsers.
///     Combines per-game signals (No-Intro / MAME style) with a DAT-level signal
///     (Redump style, where an entire DAT is dedicated to BIOS images).
/// </summary>
public static class BiosDetection
{
    /// <summary>
    ///     Detects whether a single game entry is a BIOS from per-game signals:
    ///     1. <c>isbios="yes"</c> attribute (authoritative),
    ///     2. a per-game category containing "BIOS",
    ///     3. <c>[BIOS]</c> in the game name (No-Intro convention).
    /// </summary>
    public static bool IsBiosGame(string? isBiosAttr, string? category, string name)
    {
        if (string.Equals(isBiosAttr, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (category?.Contains("BIOS", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return name.Contains("[BIOS]", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Detects whether a whole DAT is a dedicated BIOS set from its header name.
    ///     Redump publishes BIOS in standalone DATs named like "Sony - PlayStation 2 - BIOS Images",
    ///     where individual entries carry no per-game BIOS marker.
    /// </summary>
    public static bool IsBiosDat(string? datName) =>
        datName?.Contains("BIOS", StringComparison.OrdinalIgnoreCase) == true;
}
