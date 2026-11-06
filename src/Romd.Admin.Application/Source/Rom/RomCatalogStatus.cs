namespace Romd.Admin.Application.Source.Rom;

/// <summary>
///     The catalog status of a ROM file based on its DAT and Title relationships.
/// </summary>
public enum RomCatalogStatus
{
    /// <summary>
    ///     ROM file has no matching DatRom entries (not in any DAT).
    /// </summary>
    Unidentified = 0,

    /// <summary>
    ///     ROM file matches DatRom entries but the DatGame has no TitleId (DAT has no platform assigned).
    /// </summary>
    Unrouted = 1,

    /// <summary>
    ///     ROM file matches DatRom entries and at least one DatGame has a TitleId (fully cataloged).
    /// </summary>
    Cataloged = 2
}
