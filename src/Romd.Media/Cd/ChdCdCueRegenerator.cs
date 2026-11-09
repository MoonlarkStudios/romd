using System.Globalization;
using System.Text;
using Moonlark.Libchdr;

namespace Romd.Media.Cd;

/// <summary>One measured raw track bound to its exact DAT BIN name. This profile requires separate track files.</summary>
public sealed record ChdCdCueTrackFile(string Name, ChdCdTrackInspection Track);

/// <summary>Deterministic candidate descriptor bytes. Formatting establishes neither DAT identity nor original preservation.</summary>
public static class ChdCdCueRegenerator
{
    public const string Profile = "redump-track-files-cue/v1";

    public static byte[] Regenerate(IReadOnlyList<ChdCdCueTrackFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count is < 1 or > ChdCdLimits.MaximumTracks)
            throw new InvalidDataException("A bounded complete track-file list is required.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var text = new StringBuilder();
        ulong total = 0;
        foreach (var file in files)
        {
            if (file is null || file.Track is not { } track || !ChdCdCue.PortableName(file.Name) ||
                !file.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || !names.Add(file.Name) ||
                track.Number != names.Count || track.Frames is 0 or > ChdCdLimits.MaximumFrames ||
                track.BinBytes != track.Frames * 2352L || track.PregapFrames >= ChdCdLimits.MaximumFrames ||
                track.PostgapFrames >= ChdCdLimits.MaximumFrames ||
                track.PregapStored && (track.PregapFrames == 0 || track.PregapFrames >= track.Frames))
                throw new InvalidDataException("Track files must have distinct portable DAT names, consecutive raw tracks and consistent bounded geometry.");
            total += track.Frames;
            if (total > ChdCdLimits.MaximumFrames)
                throw new InvalidDataException("The complete track set exceeds the CD frame budget.");
            string mode = track.Type switch
            {
                ChdCdTrackType.Mode1Raw => "MODE1/2352",
                ChdCdTrackType.Mode2Raw => "MODE2/2352",
                ChdCdTrackType.Audio => "AUDIO",
                _ => throw new NotSupportedException("Only independently measured raw or audio BIN projections can name a CUE track.")
            };
            text.Append("FILE \"").Append(file.Name).Append("\" BINARY\r\n  TRACK ")
                .Append(track.Number.ToString("00", CultureInfo.InvariantCulture)).Append(' ').Append(mode).Append("\r\n");
            if (track.PregapStored)
                text.Append("    INDEX 00 00:00:00\r\n");
            else if (track.PregapFrames != 0)
                text.Append("    PREGAP ").Append(Time(track.PregapFrames)).Append("\r\n");
            text.Append("    INDEX 01 ").Append(Time(track.PregapStored ? track.PregapFrames : 0)).Append("\r\n");
            if (track.PostgapFrames != 0)
                text.Append("    POSTGAP ").Append(Time(track.PostgapFrames)).Append("\r\n");
        }
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(text.ToString());
        _ = ChdCdCue.Parse(bytes);
        return bytes;
    }

    private static string Time(uint frames) => string.Create(CultureInfo.InvariantCulture,
        $"{frames / (75 * 60):00}:{frames / 75 % 60:00}:{frames % 75:00}");
}
