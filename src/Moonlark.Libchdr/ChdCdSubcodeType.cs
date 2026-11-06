namespace Moonlark.Libchdr;

/// <summary>The stored CD subcode layout named by CHD track metadata.</summary>
public enum ChdCdSubcodeType
{
    /// <summary>No semantic subcode is stored.</summary>
    None = 0,
    /// <summary>96 bytes in the metadata's RW layout.</summary>
    Rw = 1,
    /// <summary>96 bytes in the metadata's RW_RAW layout.</summary>
    RwRaw = 2
}
