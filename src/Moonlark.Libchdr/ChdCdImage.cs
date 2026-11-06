namespace Moonlark.Libchdr;

/// <summary>Projects stored CD frames through a validated track table without synthesizing or converting bytes.</summary>
/// <remarks>This view and its underlying ChdFile are not thread-safe. Stored pregaps are track-local frames with the
/// track's own layout, as MAME reads them; generated pregaps and postgaps have no stored frame. GD-ROM image
/// projection and legacy binary CHCD metadata are not supported.</remarks>
public sealed class ChdCdImage : IDisposable
{
    /// <summary>The bytes of one stored CD frame: 2,352 data or audio bytes and 96 subcode bytes.</summary>
    public const int FrameBytes = 2448;
    private static readonly ChdMetadataTag LegacyBinaryCdTrack = new(0x43484344);
    private readonly ChdFile _file;
    private readonly bool _leaveOpen;
    private readonly ChdCdTrack[] _tracks;
    private readonly ChdCdTrackLayout[] _layouts;
    private readonly ulong _frames;
    private bool _disposed;

    private ChdCdImage(ChdFile file, bool leaveOpen, ChdCdTrack[] tracks, ChdCdTrackLayout[] layouts, ulong frames)
    { _file = file; _leaveOpen = leaveOpen; _tracks = tracks; _layouts = layouts; _frames = frames; }

    /// <summary>The validated track table, in contiguous one-based track order.</summary>
    public ReadOnlySpan<ChdCdTrack> Tracks { get { ThrowIfDisposed(); return _tracks; } }

    /// <summary>Where each track's stored frames sit in CHD logical storage, in the order of <see cref="Tracks"/>.</summary>
    public ReadOnlySpan<ChdCdTrackLayout> TrackLayouts { get { ThrowIfDisposed(); return _layouts; } }

    /// <summary>The CHD logical frames, including every track's alignment frames.</summary>
    public ulong FrameCount { get { ThrowIfDisposed(); return _frames; } }

    /// <summary>Creates a CD view after validating metadata, geometry and four-frame track padding.</summary>
    /// <param name="file">The open CHD; a failed open also disposes it unless leaveOpen is true.</param>
    /// <param name="leaveOpen">Whether view disposal or failed view creation preserves the file.</param>
    /// <returns>The validated view.</returns>
    /// <exception cref="NotSupportedException">The image declares GD-ROM track semantics or legacy binary CHCD metadata.</exception>
    /// <exception cref="ChdException">The track table or CD geometry is invalid.</exception>
    /// <exception cref="InvalidOperationException">Another read of the file is in progress. As with any failure, the
    /// file is then disposed unless leaveOpen is true, so open the view before sharing the file.</exception>
    public static ChdCdImage Open(ChdFile file, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(file);
        try
        {
            ChdHeader header = file.Header;
            if (header.UnitBytes != FrameBytes || header.HunkBytes % FrameBytes != 0 || header.LogicalBytes % FrameBytes != 0)
                throw new ChdException(ChdError.InvalidData, "CD frame geometry must use 2,448-byte units and complete hunks");
            var indexed = new ChdCdTrack[99];
            Span<uint> occurrences = stackalloc uint[2];
            occurrences.Clear();
            Span<byte> metadata = stackalloc byte[ChdCdTrack.MaximumMetadataBytes];
            int count = 0;
            int family = -1;
            foreach (ChdMetadataInfo entry in file.EnumerateMetadata())
            {
                if (entry.Tag == ChdMetadataTag.GdTrack || entry.Tag == ChdMetadataTag.GdTrackLegacy)
                    throw new NotSupportedException("GD-ROM frame and padding semantics require a separate image projection.");
                if (entry.Tag == LegacyBinaryCdTrack)
                    throw new NotSupportedException("Legacy binary CHCD CD metadata is not supported; only CHTR and CHT2 text track metadata is.");
                int tagIndex = entry.Tag == ChdMetadataTag.CdTrack ? 0 : entry.Tag == ChdMetadataTag.CdTrackV2 ? 1 : -1;
                if (tagIndex < 0)
                    continue;
                if (family >= 0 && family != tagIndex)
                    throw new ChdException(ChdError.InvalidMetadata, "Mixed CHTR and CHT2 track families have conflicting native ordinal interpretation");
                family = tagIndex;
                uint occurrence = occurrences[tagIndex]++;
                if (entry.Length > metadata.Length || !file.TryGetMetadata(entry.Tag, occurrence, metadata, out var info) ||
                    !ChdCdTrack.TryParse(entry.Tag, metadata[..(int)info.Length], out var track) ||
                    indexed[(int)track.Number - 1].Number != 0)
                    throw new ChdException(ChdError.InvalidMetadata, $"CD track metadata {entry.Tag} entry {occurrence} is malformed, duplicate, or exceeds its bound");
                indexed[(int)track.Number - 1] = track;
                count++;
            }
            if (count == 0)
                throw new ChdException(ChdError.InvalidMetadata, "CD text track metadata is required");
            var tracks = new ChdCdTrack[count];
            var layouts = new ChdCdTrackLayout[count];
            ulong frames = 0;
            for (int index = 0; index < count; index++)
            {
                ChdCdTrack track = indexed[index];
                if (track.Number != index + 1)
                    throw new ChdException(ChdError.InvalidMetadata, $"CD track numbers must be contiguous from one; track {index + 1} is missing");
                // PGSUB is not compared: MAME never reads it, and stored pregap frames carry the track's subcode.
                if (track.PregapStored && track.PregapType != track.Type)
                    throw new ChdException(ChdError.InvalidMetadata,
                        $"CD track {track.Number} stores its pregap as {track.PregapType}, not the track type {track.Type}");
                tracks[index] = track;
                uint alignment = (4 - track.Frames % 4) % 4;
                layouts[index] = new(track.Number, frames, track.Frames, alignment);
                frames = checked(frames + track.Frames + alignment);
            }
            if (checked(frames * FrameBytes) != header.LogicalBytes)
                throw new ChdException(ChdError.InvalidData,
                    $"The padded CD track table covers {frames} frames, but the CHD stores {header.LogicalBytes / FrameBytes}");
            return new(file, leaveOpen, tracks, layouts, frames);
        }
        catch
        {
            if (!leaveOpen)
            {
                try
                { file.Dispose(); }
                catch (Exception) { /* Preserve the validation fault if source closure also fails. */ }
            }
            throw;
        }
    }

    /// <summary>Reads a complete stored 2,448-byte frame, including alignment frames when addressed directly.</summary>
    /// <param name="chdFrame">The zero-based frame index in CHD logical storage.</param>
    /// <param name="destination">At least 2,448 bytes; any remaining bytes are unchanged.</param>
    /// <exception cref="InvalidOperationException">Another read of the file is in progress.</exception>
    public void ReadFrame(ulong chdFrame, Span<byte> destination)
    {
        ThrowIfDisposed();
        if (chdFrame >= _frames)
            throw new ArgumentOutOfRangeException(nameof(chdFrame));
        if (destination.Length < FrameBytes)
            throw new ArgumentException("A stored CD frame requires 2,448 destination bytes.", nameof(destination));
        if (_file.ReadAt(checked((long)(chdFrame * FrameBytes)), destination[..FrameBytes]) != FrameBytes)
            throw new ChdException(ChdError.ReadError, $"read complete stored CD frame {chdFrame}");
    }

    /// <summary>Copies an explicit slice of a track-local stored frame without transformations.</summary>
    /// <param name="trackNumber">The one-based track number.</param>
    /// <param name="storedTrackFrame">The stored frame within that track, including stored pregap but excluding alignment.</param>
    /// <param name="format">The explicit stored-byte projection.</param>
    /// <param name="destination">Space for the projection; excess bytes remain unchanged.</param>
    /// <returns>The exact number of bytes copied.</returns>
    /// <exception cref="NotSupportedException">The layout cannot supply the requested slice without interpretation or synthesis.</exception>
    /// <exception cref="InvalidOperationException">Another read of the file is in progress.</exception>
    public int ReadSector(uint trackNumber, uint storedTrackFrame, ChdCdSectorFormat format, Span<byte> destination)
    {
        ThrowIfDisposed();
        if (trackNumber == 0 || trackNumber > _tracks.Length)
            throw new ArgumentOutOfRangeException(nameof(trackNumber));
        if ((uint)format > (uint)ChdCdSectorFormat.XaForm2UserData)
            throw new ArgumentOutOfRangeException(nameof(format));
        int trackIndex = (int)trackNumber - 1;
        ChdCdTrack track = _tracks[trackIndex];
        if (storedTrackFrame >= track.Frames)
            throw new ArgumentOutOfRangeException(nameof(storedTrackFrame));
        (int offset, int length, bool xa) = Projection(track.Type, track.Subtype, format);
        if (destination.Length < length)
            throw new ArgumentException($"The requested CD projection requires {length} destination bytes.", nameof(destination));
        Span<byte> frame = stackalloc byte[FrameBytes];
        ReadFrame(checked(_layouts[trackIndex].FirstFrame + storedTrackFrame), frame);
        if (xa)
            ValidateXa(frame, track.Type, format);
        frame.Slice(offset, length).CopyTo(destination);
        return length;
    }

    /// <summary>Explicitly reverses each 16-bit audio sample's two bytes in place. By chdman's convention (see
    /// <see cref="ChdCdTrackType.Audio"/>) this converts stored audio to the little-endian order of Redump BINs and WAV, and back.</summary>
    /// <param name="samples">An even-length byte span; stored audio is never swapped implicitly.</param>
    public static void SwapAudioSamples16(Span<byte> samples)
    {
        if ((samples.Length & 1) != 0)
            throw new ArgumentException("16-bit audio samples require an even number of bytes.", nameof(samples));
        for (int index = 0; index < samples.Length; index += 2)
            (samples[index], samples[index + 1]) = (samples[index + 1], samples[index]);
    }

    /// <summary>Invalidates the view and closes the owned file once.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!_leaveOpen)
            _file.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _file.ThrowIfDisposed();
    }

    private static (int Offset, int Length, bool Xa) Projection(ChdCdTrackType type, ChdCdSubcodeType subtype, ChdCdSectorFormat format)
    {
        switch (format)
        {
            case ChdCdSectorFormat.Raw2352 when type is ChdCdTrackType.Mode1Raw or ChdCdTrackType.Mode2Raw or ChdCdTrackType.Audio:
                return (0, 2352, false);
            case ChdCdSectorFormat.Subcode96 when subtype != ChdCdSubcodeType.None:
                return (ChdCdTrack.GetDataBytes(type), 96, false);
            case ChdCdSectorFormat.UserData:
                if (type is ChdCdTrackType.Mode1 or ChdCdTrackType.Mode2Form1)
                    return (0, 2048, false);
                if (type == ChdCdTrackType.Mode1Raw)
                    return (16, 2048, false);
                if (type == ChdCdTrackType.Mode2Form2)
                    return (0, 2324, false);
                break;
            case ChdCdSectorFormat.Mode2Data:
                if (type == ChdCdTrackType.Mode2Raw)
                    return (16, 2336, false);
                if (type is ChdCdTrackType.Mode2 or ChdCdTrackType.Mode2FormMixed)
                    return (0, 2336, false);
                break;
            case ChdCdSectorFormat.XaForm1UserData:
            case ChdCdSectorFormat.XaForm2UserData:
                if (type is ChdCdTrackType.Mode2Raw or ChdCdTrackType.Mode2FormMixed)
                    return (type == ChdCdTrackType.Mode2Raw ? 24 : 8, format == ChdCdSectorFormat.XaForm1UserData ? 2048 : 2324, true);
                break;
        }
        throw new NotSupportedException($"Track layout {type}/{subtype} cannot supply {format} from stored bytes; choose an explicit supported projection.");
    }

    private static void ValidateXa(ReadOnlySpan<byte> frame, ChdCdTrackType type, ChdCdSectorFormat format)
    {
        int offset = type == ChdCdTrackType.Mode2Raw ? 16 : 0;
        ReadOnlySpan<byte> subheader = frame.Slice(offset, 8);
        bool form2 = (subheader[2] & 0x20) != 0;
        if (!subheader[..4].SequenceEqual(subheader[4..]) ||
            form2 != (format == ChdCdSectorFormat.XaForm2UserData))
            throw new ChdException(ChdError.InvalidData, "XA subheaders must agree and declare the explicitly requested form");
    }
}
