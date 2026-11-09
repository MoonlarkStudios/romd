using System.Security.Cryptography;
using System.Text.Json;
using Moonlark.Libchdr;
using Romd.Media.Chd;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Romd.Media.Saturn;
using Romd.Storage;

namespace Romd.Media.Cd;

public sealed record ChdCdRetainedTrack(int Number, long FirstFrame, int Frames, ChdCdTrackType Type);
public sealed record ChdCdRetainedRequest(string Profile, string ArtifactSha256, long ArtifactBytes,
    long LogicalFrames, uint HunkBytes, IReadOnlyList<uint> Codecs, string RawSha1, string OverallSha1,
    IReadOnlyList<ChdCdRetainedTrack> Tracks, IReadOnlyList<ChdCdMemberVerification> Members,
    IReadOnlyList<ChdCdSourceFile> StoredFiles, Ps1CdObservedFacts? Boot, SaturnCdObservedFacts? SaturnBoot = null)
{
    public const string CurrentProfile = "chd-cd-retained-files/v2";
    [System.Text.Json.Serialization.JsonIgnore]
    public CdBootObservation ObservedBoot => CdBootObservation.FromWire(Boot, SaturnBoot);
    public void Validate()
    {
        if (Profile != CurrentProfile || !ChdCdJson.Hash(ArtifactSha256, 64) || ArtifactBytes is < 124 or > ChdCdLimits.MaximumArtifactBytes ||
            LogicalFrames is < 1 or > ChdCdLimits.MaximumFrames || HunkBytes is < 2448 or > ChdCdLimits.MaximumHunkBytes || HunkBytes % 2448 != 0 ||
            Codecs is not { Count: 4 } || !ChdCdJson.Hash(RawSha1, 40) || !ChdCdJson.Hash(OverallSha1, 40) ||
            Tracks is not { Count: >= 1 and <= 99 } || Members is not { Count: >= 2 and <= 116 } || StoredFiles is not { Count: >= 1 and <= 17 })
            throw new InvalidDataException("Incomplete retained CD request.");
        _ = ObservedBoot;
        long next = 0;
        for (int i = 0; i < Tracks.Count; i++)
        {
            var track = Tracks[i];
            if (track is null || track.Number != i + 1 || track.FirstFrame != next || track.Frames is < 1 or > 450000 ||
                track.Type is not (ChdCdTrackType.Mode1Raw or ChdCdTrackType.Mode2Raw or ChdCdTrackType.Audio))
                throw new InvalidDataException("Invalid retained CD geometry.");
            next = checked(next + (track.Frames + 3L) / 4 * 4);
        }
        if (next != LogicalFrames || Members.Any(m => m is null || !ChdCdCue.PortableName(m.Name) || !Valid(m.Original) || m.Tracks is null ||
            (m.Role == "bin" ? m.Reconstructed != m.Original || m.Tracks.Count is < 1 or > 99 ||
                m.Tracks.Any(n => n < 1 || n > Tracks.Count) || !m.Tracks.SequenceEqual(Enumerable.Range(m.Tracks[0], m.Tracks.Count)) ||
                m.Original.Bytes != m.Tracks.Sum(n => (long)Tracks[n - 1].Frames * 2352) :
                m.Role is not ("cue" or "sidecar") || m.Reconstructed is not null || m.Tracks.Count != 0 ||
                m.Original.Bytes > (m.Role == "cue" ? ChdCdCue.MaximumBytes : 16 << 20))) ||
            Members.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Members.Count ||
            Members.Count(m => m.Role == "cue") != 1 || Members.Count(m => m.Role == "sidecar") > 16 ||
            !Members.SelectMany(m => m.Tracks).Order().SequenceEqual(Enumerable.Range(1, Tracks.Count)) ||
            StoredFiles.Count != Members.Count(m => m.Role != "bin") ||
            StoredFiles.Any(f => f is null || !ChdCdJson.Absolute(f.Path) || Members.Count(m => m.Name == f.Name && m.Role == f.Role &&
                m.Role != "bin" && m.Original.Bytes == f.Bytes && m.Original.Sha256 == f.Sha256) != 1) ||
            StoredFiles.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != StoredFiles.Count ||
            StoredFiles.Select(f => f.Path).Distinct(StringComparer.Ordinal).Count() != StoredFiles.Count)
            throw new InvalidDataException("Retained CD request must cover every whole BIN and exact stored dependency.");
    }
    private static bool Valid(ChdCdFileHashes? h) => h is not null && h.Bytes is >= 1 and <= 1058400000 &&
        ChdCdJson.Hash(h.Sha256, 64) && ChdCdJson.Hash(h.Sha1, 40) && ChdCdJson.Hash(h.Md5, 32) && ChdCdJson.Hash(h.Crc32, 8);

    public bool MatchesBoot(Ps1CdObservedFacts? observed) => MatchesBoot(CdBootObservation.FromWire(observed, null));
    public bool MatchesBoot(CdBootObservation observed) => ObservedBoot.Profile is null || ObservedBoot == observed;

    public bool MatchesVerification(ChdCdVerification proof) => proof.Profile == CurrentProfile && MatchesBoot(proof.ObservedBoot) &&
        proof.Members is not null && proof.Members.Count == Members.Count && proof.Members.Zip(Members).All(pair =>
            pair.First is { } actual && actual.Name == pair.Second.Name && actual.Role == pair.Second.Role &&
            actual.Original == pair.Second.Original && actual.Reconstructed == pair.Second.Reconstructed &&
            actual.Tracks is not null && actual.Tracks.SequenceEqual(pair.Second.Tracks));

    public bool Matches(ChdCdInspection inspection) => MatchesBoot(inspection.ObservedBoot) && inspection.ArtifactSha256 == ArtifactSha256 &&
        inspection.Header.ArtifactBytes == ArtifactBytes && inspection.Header.LogicalBytes == (ulong)LogicalFrames * 2448 &&
        inspection.Header.HunkBytes == HunkBytes && Codecs.SequenceEqual(new[] { inspection.Header.Codec0, inspection.Header.Codec1, inspection.Header.Codec2, inspection.Header.Codec3 }) &&
        inspection.StoredRawSha1 == RawSha1 && inspection.ComputedRawSha1 == RawSha1 &&
        inspection.StoredOverallSha1 == OverallSha1 && inspection.ComputedOverallSha1 == OverallSha1 &&
        inspection.Tracks.Select(t => new ChdCdRetainedTrack((int)t.Number, (long)t.FirstFrame, (int)t.Frames, t.Type)).SequenceEqual(Tracks);
}

/// <summary>Measures retained bytes without original BINs. The manifest supplies expected identities, never measured results.</summary>
public static class ChdCdRetainedVerificationService
{
    public static async Task<ChdCdVerificationOutcome> VerifyAsync(string artifactPath, string requestPath, long requestBytes,
        string requestSha256, ChdNativeCandidate candidate, long artifactLimit, long logicalLimit, CancellationToken ct = default)
    {
        List<ChdCdRawFile> held = [];
        try
        {
            if (requestBytes is < 1 or > ChdCdRequest.MaximumBytes || !ChdCdJson.Hash(requestSha256, 64))
                throw new InvalidDataException("Invalid retained request identity.");
            var requestFile = new ChdCdRawFile(requestPath);
            held.Add(requestFile);
            if (requestFile.Length != requestBytes)
                throw new InvalidDataException("Retained request changed size.");
            byte[] json = new byte[(int)requestBytes];
            if (requestFile.ReadAt(0, json) != json.Length || Convert.ToHexStringLower(SHA256.HashData(json)) != requestSha256)
                throw new InvalidDataException("Retained request changed identity.");
            var spec = ChdCdJson.Read<ChdCdRetainedRequest>(json, ChdCdRequest.MaximumBytes);
            spec.Validate();
            if (spec.StoredFiles.Any(f => f.Path == artifactPath || f.Path == requestPath) || artifactPath == requestPath)
                throw new InvalidDataException("Retained inputs alias each other.");
            var stored = new Dictionary<string, ChdCdRawFile>(StringComparer.Ordinal);
            foreach (var file in spec.StoredFiles)
            {
                var raw = new ChdCdRawFile(file.Path);
                held.Add(raw);
                stored.Add(file.Name, raw);
                if (await ChdCdHashing.MeasureAsync(raw, ct) != spec.Members.Single(m => m.Name == file.Name).Original)
                    throw new InvalidDataException("Stored dependency differs from the retained identity.");
            }
            var cueMember = spec.Members.Single(m => m.Role == "cue");
            byte[] cueBytes = new byte[(int)cueMember.Original.Bytes];
            if (stored[cueMember.Name].ReadAt(0, cueBytes) != cueBytes.Length)
                throw new EndOfStreamException();
            var cue = ChdCdCue.Parse(cueBytes);
            if (!cue.TrackTypes.SequenceEqual(spec.Tracks.Select(t => t.Type)) ||
                !cue.Files.Select(f => f.Name).Order().SequenceEqual(spec.Members.Where(m => m.Role == "bin").Select(m => m.Name).Order()) ||
                cue.Files.Any(f => !f.Tracks.SequenceEqual(spec.Members.Single(m => m.Name == f.Name).Tracks)))
                throw new InvalidDataException("Retained CUE contradicts the whole-file selectors.");
            var result = await ChdCdInspectionService.InspectAsync(artifactPath, candidate, artifactLimit, logicalLimit, ct);
            if (result.Status != MediaWorkerStatus.Inspected || result.Inspection is null || result.NativeIdentity is null)
                return new(result.Status, Failure: new(ChdCdJobCause.InspectionRejected));
            if (!spec.Matches(result.Inspection))
                throw new InvalidDataException("Retained CD geometry or integrity changed.");
            await using var pool = new ChdReadPool(new MediaBlockCache(8 << 20), 1, 1);
            CdBootObservation boot = default;
            List<ChdCdMemberVerification> measuredMembers = [];
            foreach (var member in spec.Members.Where(m => m.Role == "bin"))
            {
                var device = await ChdBlockDevice.OpenAsync(new ChdCdRawFile(artifactPath), pool, ChdCdLimits.Preflight, spec.ArtifactSha256, ct: ct);
                await using var bin = await ChdCdBinContent.CreateAsync(device, member.Tracks.Select(n => spec.Tracks[n - 1])
                    .Select(t => new ChdCdBinExtent(t.FirstFrame, t.Frames, t.Type == ChdCdTrackType.Audio)).ToArray());
                var measured = await ChdCdHashing.MeasureAsync(bin, ct);
                if (measured != member.Original)
                    return new(MediaWorkerStatus.Malformed, Failure: new(ChdCdJobCause.ReconstructionMismatch));
                measuredMembers.Add(new(member.Name, member.Role, member.Tracks, member.Original, measured));
                if (member.Tracks[0] == 1)
                    boot = CdBootObservation.Inspect(new SynchronousView(bin), cue.TrackTypes[0], cue.FirstTrackIndex01, spec.Tracks.Sum(t => (long)t.Frames), ct);
            }
            if (!spec.MatchesBoot(boot) || boot != result.Inspection.ObservedBoot)
                throw new InvalidDataException("Retained CD boot observation changed or disagrees with verified native bytes.");
            foreach (var file in spec.StoredFiles)
            {
                var expected = spec.Members.Single(m => m.Name == file.Name).Original;
                using var named = new ChdCdRawFile(file.Path);
                var actual = await ChdCdHashing.MeasureAsync(stored[file.Name], ct);
                if (actual != expected || await ChdCdHashing.MeasureAsync(named, ct) != expected)
                    throw new InvalidDataException("Stored dependency changed during verification.");
                measuredMembers.Add(new(file.Name, file.Role, [], actual, null));
            }
            using var finalArtifact = new ChdCdRawFile(artifactPath);
            if (finalArtifact.Length != spec.ArtifactBytes || (await ChdCdHashing.MeasureAsync(finalArtifact, ct)).Sha256 != spec.ArtifactSha256)
                throw new InvalidDataException("Retained artifact changed during verification.");
            using var finalRequest = new ChdCdRawFile(requestPath);
            if (finalRequest.Length != requestBytes || (await ChdCdHashing.MeasureAsync(finalRequest, ct)).Sha256 != requestSha256)
                throw new InvalidDataException("Retained request changed during verification.");
            return new(MediaWorkerStatus.Inspected, new(ChdCdRetainedRequest.CurrentProfile, requestSha256,
                spec.Members.Select(m => measuredMembers.Single(actual => actual.Name == m.Name)).ToArray(), boot.Ps1, boot.Saturn), result.Inspection, result.NativeIdentity);
        }
        catch (Exception e) when (e is ArgumentException or JsonException or InvalidDataException or OverflowException)
        { return new(MediaWorkerStatus.Malformed, Failure: new(ChdCdJobCause.InvalidInput)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return new(MediaWorkerStatus.IoFailure, Failure: new(ChdCdJobCause.IoFailure)); }
        finally { foreach (var file in held) file.Dispose(); }
    }
    private sealed class SynchronousView(IRandomAccessContent content) : ISynchronousRawContent
    {
        public long Length => content.Length;
        public int ReadAt(long offset, Span<byte> destination)
        {
            byte[] bytes = new byte[destination.Length];
            int count = content.ReadAtAsync(offset, bytes).AsTask().GetAwaiter().GetResult();
            bytes.AsSpan(0, count).CopyTo(destination);
            return count;
        }
        public void Dispose() { } // The enclosing whole-BIN projection owns the native device.
    }
}
