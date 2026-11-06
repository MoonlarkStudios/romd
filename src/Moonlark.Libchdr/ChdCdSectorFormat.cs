namespace Moonlark.Libchdr;

/// <summary>An explicit projection of stored CD bytes, without sector synthesis or byte-order conversion.</summary>
public enum ChdCdSectorFormat
{
    /// <summary>The complete 2,352 bytes of a raw data sector or audio frame.</summary>
    Raw2352 = 0,
    /// <summary>User data whose size and position are unambiguous from the stored track type.</summary>
    UserData = 1,
    /// <summary>The 96 semantic subcode bytes, immediately after the track's data bytes.</summary>
    Subcode96 = 2,
    /// <summary>The complete 2,336-byte mode 2 data region, including any XA subheader.</summary>
    Mode2Data = 3,
    /// <summary>2,048 XA form 1 user bytes, after validating both copies of the subheader.</summary>
    XaForm1UserData = 4,
    /// <summary>2,324 XA form 2 user bytes, after validating both copies of the subheader.</summary>
    XaForm2UserData = 5
}
