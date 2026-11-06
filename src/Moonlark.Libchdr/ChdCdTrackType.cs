namespace Moonlark.Libchdr;

/// <summary>The stored CD data layout named by CHD track metadata.</summary>
public enum ChdCdTrackType
{
    /// <summary>2,048 cooked mode 1 bytes.</summary>
    Mode1 = 0,
    /// <summary>A complete 2,352-byte mode 1 sector.</summary>
    Mode1Raw = 1,
    /// <summary>2,336 bytes of mode 2 data.</summary>
    Mode2 = 2,
    /// <summary>2,048 cooked mode 2 form 1 bytes.</summary>
    Mode2Form1 = 3,
    /// <summary>2,324 cooked mode 2 form 2 bytes.</summary>
    Mode2Form2 = 4,
    /// <summary>2,336 bytes containing an XA subheader and either form.</summary>
    Mode2FormMixed = 5,
    /// <summary>A complete 2,352-byte mode 2 sector; its XA form requires inspection.</summary>
    Mode2Raw = 6,
    /// <summary>2,352 audio bytes exactly as CHD stores them, never swapped implicitly. By MAME's convention chdman
    /// stores CUE BINARY and WAVE audio as big-endian 16-bit samples, so little-endian consumers such as Redump BINs
    /// need <see cref="ChdCdImage.SwapAudioSamples16"/>. This is a convention, not a guarantee: chdman swaps every CUE
    /// audio track, so a CUE MOTOROLA (big-endian) source is stored little-endian.</summary>
    Audio = 7
}
