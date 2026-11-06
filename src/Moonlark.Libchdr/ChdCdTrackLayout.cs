namespace Moonlark.Libchdr;

/// <summary>Where one track's stored frames sit in CHD logical storage.</summary>
/// <remarks>Each track starts on a four-frame boundary. The alignment frames that follow it hold no track data;
/// chdman writes them as zeros, but their bytes are not validated. Unrelated to the GD-ROM <see cref="ChdCdTrack.PadFrames"/>.</remarks>
public readonly record struct ChdCdTrackLayout
{
    internal ChdCdTrackLayout(uint trackNumber, ulong firstFrame, uint frames, uint alignmentFrames)
    {
        TrackNumber = trackNumber;
        FirstFrame = firstFrame;
        Frames = frames;
        AlignmentFrames = alignmentFrames;
    }

    /// <summary>The one-based track number.</summary>
    public uint TrackNumber { get; }

    /// <summary>The zero-based CHD logical frame of the track's first stored frame, including any stored pregap.</summary>
    public ulong FirstFrame { get; }

    /// <summary>The track's stored frames; equal to <see cref="ChdCdTrack.Frames"/>.</summary>
    public uint Frames { get; }

    /// <summary>The alignment frames after the track, from zero through three, that pad it to a four-frame boundary.</summary>
    public uint AlignmentFrames { get; }

    /// <summary>The CHD logical byte offset of the track's first stored frame.</summary>
    public ulong ByteOffset => FirstFrame * ChdCdImage.FrameBytes;
}
