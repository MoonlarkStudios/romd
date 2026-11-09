using System.Text.Json;
using Moonlark.Libchdr;
using Romd.Media.PlayStation;

namespace Romd.Media.Inspection;

public static partial class MediaWorkerProtocol
{
    private static JsonElement WriteCdInspection(ChdCdInspection cd) => JsonSerializer.SerializeToElement(new
    {
        profile = cd.Profile,
        projectionProfile = cd.ProjectionProfile,
        artifactSha256 = cd.ArtifactSha256,
        header = new
        {
            version = cd.Version,
            hasParent = cd.HasParent,
            artifactBytes = cd.Header.ArtifactBytes,
            logicalBytes = cd.Header.LogicalBytes,
            hunkBytes = cd.Header.HunkBytes,
            unitBytes = cd.Header.UnitBytes,
            hunkCount = cd.Header.HunkCount,
            codec0 = cd.Header.Codec0,
            codec1 = cd.Header.Codec1,
            codec2 = cd.Header.Codec2,
            codec3 = cd.Header.Codec3
        },
        storedRawSha1 = cd.StoredRawSha1,
        computedRawSha1 = cd.ComputedRawSha1,
        storedOverallSha1 = cd.StoredOverallSha1,
        computedOverallSha1 = cd.ComputedOverallSha1,
        ps1Boot = JsonSerializer.SerializeToElement(cd.Ps1Boot, ChdCdJson.Options),
        saturnBoot = JsonSerializer.SerializeToElement(cd.SaturnBoot, ChdCdJson.Options),
        metadata = cd.Metadata.Select(entry => new { tag = entry.Tag, flags = entry.Flags, payload = entry.PayloadHex }),
        tracks = cd.Tracks.Select(track => new
        {
            number = track.Number,
            type = track.Type switch
            {
                ChdCdTrackType.Mode1Raw => "mode1-raw",
                ChdCdTrackType.Mode2Raw => "mode2-raw",
                ChdCdTrackType.Audio => "audio",
                _ => "unsupported"
            },
            firstFrame = track.FirstFrame,
            frames = track.Frames,
            alignmentFrames = track.AlignmentFrames,
            pregapFrames = track.PregapFrames,
            pregapStored = track.PregapStored,
            postgapFrames = track.PostgapFrames,
            binBytes = track.BinBytes,
            sha256 = track.Sha256,
            sha1 = track.Sha1,
            md5 = track.Md5,
            crc32 = track.Crc32
        })
    });

    private static bool TryReadCdInspection(JsonElement value, out ChdCdInspection? inspection)
    {
        inspection = null;
        if (!HasFields(value, "profile", "projectionProfile", "artifactSha256", "header", "storedRawSha1", "computedRawSha1",
                "storedOverallSha1", "computedOverallSha1", "tracks", "metadata", "ps1Boot", "saturnBoot") ||
            Text(value, "profile") != ChdCdInspectionService.Profile || Text(value, "projectionProfile") != ChdCdInspectionService.ProjectionProfile ||
            !IdentityHash(Text(value, "artifactSha256"), 64) || !IdentityHash(Text(value, "storedRawSha1"), 40) ||
            !IdentityHash(Text(value, "storedOverallSha1"), 40) ||
            Text(value, "storedRawSha1") != Text(value, "computedRawSha1") ||
            Text(value, "storedOverallSha1") != Text(value, "computedOverallSha1"))
            return false;
        var header = value.GetProperty("header");
        if (!HasFields(header, "version", "hasParent", "artifactBytes", "logicalBytes", "hunkBytes", "unitBytes", "hunkCount", "codec0", "codec1", "codec2", "codec3") ||
            !Number(header, "version", out long version) || version != 5 || header.GetProperty("hasParent").ValueKind != JsonValueKind.False ||
            !Number(header, "artifactBytes", out long artifact) || artifact is < 124 or > ChdCdLimits.MaximumArtifactBytes ||
            !Number(header, "logicalBytes", out long logical) || logical is <= 0 or > ChdCdLimits.MaximumLogicalBytes || logical % ChdCdImage.FrameBytes != 0 ||
            !Number(header, "hunkBytes", out long hunk) || hunk is <= 0 or > ChdCdLimits.MaximumHunkBytes || hunk % ChdCdImage.FrameBytes != 0 ||
            !Number(header, "unitBytes", out long unit) || unit != ChdCdImage.FrameBytes ||
            !Number(header, "hunkCount", out long count) || count is <= 0 or > ChdCdLimits.MaximumFrames || count != logical / hunk + (logical % hunk == 0 ? 0 : 1) ||
            !Number(header, "codec0", out long codec0) || !CdCodec(codec0) ||
            !Number(header, "codec1", out long codec1) || !CdCodec(codec1) ||
            !Number(header, "codec2", out long codec2) || !CdCodec(codec2) ||
            !Number(header, "codec3", out long codec3) || !CdCodec(codec3))
            return false;
        var entries = value.GetProperty("tracks");
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() is < 1 or > ChdCdLimits.MaximumTracks)
            return false;
        var tracks = new List<ChdCdTrackInspection>(entries.GetArrayLength());
        long firstFrame = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            if (!HasFields(entry, "number", "type", "firstFrame", "frames", "alignmentFrames", "pregapFrames", "pregapStored",
                    "postgapFrames", "binBytes", "sha256", "sha1", "md5", "crc32") ||
                !Number(entry, "number", out long number) || number != tracks.Count + 1 ||
                !Number(entry, "firstFrame", out long start) || start != firstFrame ||
                !Number(entry, "frames", out long frames) || frames is <= 0 or > ChdCdLimits.MaximumFrames ||
                !Number(entry, "alignmentFrames", out long alignment) || alignment != (4 - frames % 4) % 4 ||
                !Number(entry, "pregapFrames", out long pregap) || pregap is < 0 or > ChdCdLimits.MaximumFrames ||
                entry.GetProperty("pregapStored").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !Number(entry, "postgapFrames", out long postgap) || postgap is < 0 or > ChdCdLimits.MaximumFrames ||
                !Number(entry, "binBytes", out long binBytes) || binBytes != frames * 2352 ||
                !IdentityHash(Text(entry, "sha256"), 64) || !IdentityHash(Text(entry, "sha1"), 40) ||
                !IdentityHash(Text(entry, "md5"), 32) || !Hash(Text(entry, "crc32"), 8))
                return false;
            bool stored = entry.GetProperty("pregapStored").GetBoolean();
            if (stored && (pregap == 0 || pregap > frames))
                return false;
            ChdCdTrackType? type = Text(entry, "type") switch
            {
                "mode1-raw" => ChdCdTrackType.Mode1Raw,
                "mode2-raw" => ChdCdTrackType.Mode2Raw,
                "audio" => ChdCdTrackType.Audio,
                _ => null
            };
            if (type is null)
                return false;
            firstFrame += frames + alignment;
            if (firstFrame > logical / ChdCdImage.FrameBytes)
                return false;
            tracks.Add(new((uint)number, type.Value, (ulong)start, (uint)frames, (uint)alignment,
                (uint)pregap, stored, (uint)postgap, binBytes, Text(entry, "sha256")!, Text(entry, "sha1")!,
                Text(entry, "md5")!, Text(entry, "crc32")!));
        }
        if (firstFrame * ChdCdImage.FrameBytes != logical)
            return false;
        var rawMetadata = value.GetProperty("metadata");
        if (rawMetadata.ValueKind != JsonValueKind.Array || rawMetadata.GetArrayLength() != tracks.Count)
            return false;
        var metadata = new List<ChdCdMetadataInspection>(tracks.Count);
        var seen = new HashSet<uint>();
        long metadataBytes = 0;
        foreach (var entry in rawMetadata.EnumerateArray())
        {
            if (!HasFields(entry, "tag", "flags", "payload") || !Number(entry, "tag", out long tag) || tag != ChdMetadataTag.CdTrackV2.Value ||
                !Number(entry, "flags", out long flags) || flags != 1 || Text(entry, "payload") is not { Length: >= 2 and <= 1024 } payload ||
                payload.Length % 2 != 0 || !Hash(payload, payload.Length))
                return false;
            byte[] bytes = Convert.FromHexString(payload);
            if (!ChdCdTrack.TryParse(new((uint)tag), bytes, out var parsed) || parsed.Number > tracks.Count || !seen.Add(parsed.Number))
                return false;
            var measured = tracks[(int)parsed.Number - 1];
            if (parsed.Type != measured.Type || parsed.Subtype != ChdCdSubcodeType.None || parsed.Frames != measured.Frames ||
                parsed.PregapFrames != measured.PregapFrames || parsed.PregapStored != measured.PregapStored || parsed.PostgapFrames != measured.PostgapFrames)
                return false;
            metadata.Add(new((uint)tag, (byte)flags, payload));
            metadataBytes += bytes.Length + 16;
        }
        if (metadataBytes + 124 > artifact)
            return false;
        if (!TryReadCdBoot(value.GetProperty("ps1Boot"), tracks, out var boot) ||
            !TryReadSaturnCdBoot(value.GetProperty("saturnBoot"), tracks, out var saturn) || boot is not null && saturn is not null)
            return false;
        inspection = new(ChdCdInspectionService.Profile, ChdCdInspectionService.ProjectionProfile, Text(value, "artifactSha256")!,
            (uint)version, false, new(artifact, (ulong)logical, (uint)hunk, ChdCdImage.FrameBytes, (uint)count, (uint)codec0, (uint)codec1, (uint)codec2, (uint)codec3),
            Text(value, "storedRawSha1")!, Text(value, "computedRawSha1")!, Text(value, "storedOverallSha1")!, Text(value, "computedOverallSha1")!, tracks.ToArray(), metadata.ToArray(), boot, saturn);
        return true;
    }

    private static bool TryReadCdBoot(JsonElement value, IReadOnlyList<ChdCdTrackInspection> tracks, out Ps1CdObservedFacts? boot)
    {
        boot = null;
        if (value.ValueKind == JsonValueKind.Null)
            return true;
        if (!HasFields(value, "profile", "firstTrackIndex01", "volumeBlocks", "rootBlock", "rootBytes", "bootPath",
                "bootBlock", "bootBytes", "entryPoint", "loadAddress", "loadBytes"))
            return false;
        if (System.Text.Encoding.UTF8.GetByteCount(value.GetRawText()) > 1024)
            return false;
        // HasFields closes the scalar record; typed JSON rejects nested or wrongly typed values.
        var observed = value.Deserialize<Ps1CdObservedFacts>(ChdCdJson.Options);
        var first = tracks[0];
        if (observed is null || !Ps1CdIdentifier.Valid(observed) || first.Type is not (ChdCdTrackType.Mode1Raw or ChdCdTrackType.Mode2Raw) ||
            observed.FirstTrackIndex01 != (first.PregapStored ? first.PregapFrames : 0) ||
            observed.FirstTrackIndex01 + observed.VolumeBlocks > tracks.Sum(track => (long)track.Frames) ||
            observed.FirstTrackIndex01 + observed.RootBlock + (observed.RootBytes + 2047L) / 2048 > first.Frames ||
            observed.FirstTrackIndex01 + observed.BootBlock + (observed.BootBytes + 2047L) / 2048 > first.Frames)
            return false;
        boot = observed;
        return true;
    }

    private static bool TryReadSaturnCdBoot(JsonElement value, IReadOnlyList<ChdCdTrackInspection> tracks, out SaturnCdObservedFacts? boot)
    {
        boot = null;
        if (value.ValueKind == JsonValueKind.Null)
            return true;
        if (!HasFields(value, "profile", "firstTrackIndex01", "productCode", "areaSymbols", "initialProgramBytes", "headerSha256") ||
            System.Text.Encoding.UTF8.GetByteCount(value.GetRawText()) > 1024)
            return false;
        var observed = value.Deserialize<SaturnCdObservedFacts>(ChdCdJson.Options);
        var first = tracks[0];
        if (observed is null || !new CdBootObservation(null, observed).Fits(first.Type, first.Frames, tracks.Sum(t => (long)t.Frames)) ||
            observed.FirstTrackIndex01 != (first.PregapStored ? first.PregapFrames : 0))
            return false;
        boot = observed;
        return true;
    }

    private static bool CdCodec(long value) => value is >= 0 and <= uint.MaxValue &&
        (ChdDvdLimits.IsDvdCodec((uint)value) || ChdDvdLimits.IsCdCodec((uint)value));
}
