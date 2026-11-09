using System.Text;
using System.Text.Json;
using Romd.Media.PlayStation;

namespace Romd.Media.Inspection;

public static partial class MediaWorkerProtocol
{
    public const string CdJobVersion = "romd-media-cd/v5";
    // Also bounds up to 116 named source/comparison rows and the nested worker envelope.
    public const int MaximumCdJobOutputBytes = 512 << 10;
    private static bool IsCdJob(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("version", out var version) &&
                version.ValueKind == JsonValueKind.String && version.GetString() == CdJobVersion;
        }
        catch (JsonException) { return false; }
    }
    private static byte[] WriteCdJob(MediaWorkerReply reply)
    {
        if (reply.Inspection is not null || reply.IsoInspection is not null || reply.Encoding is not null || reply.ChdInspection is not null ||
            reply.ChdRejection is not null || reply.ChdEncoding is not null || reply.ChdExtraction is not null || reply.ChdEncodingRejection is not null || reply.Ps2Inspection is not null)
            throw new ArgumentException("CD jobs cannot carry singleton/DVD/RVZ evidence.");
        using var worker = JsonDocument.Parse(Write(new(MediaWorkerStatus.Ready, reply.Identity)));
        byte[] result = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = CdJobVersion,
            status = StatusText(reply.Status),
            worker = worker.RootElement,
            encoding = reply.CdEncoding,
            verification = reply.CdVerification,
            inspection = reply.CdInspection is { } cd ? WriteCdInspection(cd) : (JsonElement?)null,
            native = reply.NativeIdentity is { } native ? WriteNativeIdentity(native) : (JsonElement?)null,
            tool = reply.ToolIdentity is { } tool ? WriteToolIdentity(tool) : (JsonElement?)null,
            execution = reply.ToolExecution,
            failure = reply.CdFailure
        }, ChdCdJson.Options);
        if (result.Length > MaximumCdJobOutputBytes)
            throw new InvalidDataException("CD job reply exceeds its budget.");
        return result;
    }
    private static bool TryReadCdJob(ReadOnlyMemory<byte> bytes, out MediaWorkerReply? reply)
    {
        reply = null;
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            ChdCdJson.Unique(root);
            if (!HasFields(root, "version", "status", "worker", "encoding", "verification", "inspection", "native", "tool", "execution", "failure") ||
                Text(root, "version") != CdJobVersion || !TryStatus(Text(root, "status"), out var status) || status == MediaWorkerStatus.Ready)
                return false;
            var worker = root.GetProperty("worker");
            if (worker.ValueKind != JsonValueKind.Object || Text(worker, "version") != Version ||
                !TryRead(Encoding.UTF8.GetBytes(worker.GetRawText()), out var baseReply) || baseReply is null || baseReply.Status != MediaWorkerStatus.Ready ||
                new[] { "inspection", "isoInspection", "ps2Inspection", "encoding", "nativeIdentity", "chdInspection", "chdRejection", "chdEncoding",
                    "toolIdentity", "chdEncodingRejection", "toolExecution", "cdInspection" }.Any(name => worker.GetProperty(name).ValueKind != JsonValueKind.Null))
                return false;
            ChdCdEncodedArtifact? encoded = Read<ChdCdEncodedArtifact>("encoding");
            ChdCdVerification? verified = Read<ChdCdVerification>("verification");
            ChdCdJobFailure? failure = Read<ChdCdJobFailure>("failure");
            ChdToolExecution? execution = Read<ChdToolExecution>("execution");
            ChdCdInspection? inspection = null;
            ChdNativeIdentity? native = null;
            ChdmanToolIdentity? tool = null;
            if (root.GetProperty("inspection").ValueKind != JsonValueKind.Null && !TryReadCdInspection(root.GetProperty("inspection"), out inspection) ||
                root.GetProperty("native").ValueKind != JsonValueKind.Null && !TryReadNativeIdentity(root.GetProperty("native"), out native) ||
                root.GetProperty("tool").ValueKind != JsonValueKind.Null && !TryReadToolIdentity(root.GetProperty("tool"), out tool))
                return false;
            if (failure is not null)
            {
                if (!Enum.IsDefined(failure.Cause) || failure.ToolCause is { } cause && !Enum.IsDefined(cause) ||
                    status is MediaWorkerStatus.Inspected or MediaWorkerStatus.Encoded || encoded is not null || verified is not null || inspection is not null || native is not null)
                    return false;
            }
            else if (status == MediaWorkerStatus.Encoded)
            {
                if (encoded is null || verified is not null || inspection is not null || native is not null || tool is null || !tool.Rid.StartsWith("linux-", StringComparison.Ordinal) ||
                    !ChdCdJson.Hash(encoded.RequestSha256, 64) || encoded.RequestBytes is < 1 or > ChdCdRequest.MaximumBytes ||
                    !ValidCdHashes(encoded.Artifact, ChdCdLimits.MaximumArtifactBytes) || encoded.Artifact.Bytes < 124 ||
                    encoded.Header is null || encoded.Header.ArtifactBytes != encoded.Artifact.Bytes || !ChdCdEncodingRecipe.Matches(encoded.Header) ||
                    !encoded.ObservedBoot.Valid || encoded.Recipe != ChdCdEncodingRecipe.ForBoot(encoded.ObservedBoot.Profile) ||
                    encoded.PriorityKind != "unix-nice" || encoded.PriorityValue is < 10 or > 19 || execution is null || execution.ExitCode != 0)
                    return false;
            }
            else if (status == MediaWorkerStatus.Inspected)
            {
                if (verified is null || encoded is not null || inspection is null || native is null || tool is not null || execution is not null ||
                    !verified.ObservedBoot.Valid || verified.ObservedBoot.Profile is not null && verified.ObservedBoot != inspection.ObservedBoot ||
                    verified.Profile is not (ChdCdVerification.CurrentProfile or ChdCdRetainedRequest.CurrentProfile) ||
                    verified.Profile == ChdCdRetainedRequest.CurrentProfile && verified.ObservedBoot != inspection.ObservedBoot || !ChdCdJson.Hash(verified.RequestSha256, 64) ||
                    verified.Members is null || verified.Members.Count is < 2 or > 116 ||
                    verified.Members.Any(m => m is null || !ChdCdCue.PortableName(m.Name) || !ValidCdHashes(m.Original, 1058400000) || m.Tracks is null ||
                        (m.Role == "bin" ? m.Reconstructed != m.Original || m.Tracks.Count is < 1 or > 99 ||
                            m.Tracks.Any(n => n < 1 || n > inspection.Tracks.Count) || !m.Tracks.SequenceEqual(Enumerable.Range(m.Tracks[0], m.Tracks.Count)) ||
                            m.Original.Bytes != m.Tracks.Sum(n => inspection.Tracks[n - 1].BinBytes) :
                            m.Role is not ("cue" or "sidecar") || m.Reconstructed is not null || m.Tracks.Count != 0 ||
                            m.Original.Bytes > (m.Role == "cue" ? ChdCdCue.MaximumBytes : 16 << 20))) ||
                    verified.Members.Count(m => m.Role == "cue") != 1 || verified.Members.Count(m => m.Role == "sidecar") > 16 ||
                    verified.Members.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != verified.Members.Count ||
                    !verified.Members.SelectMany(m => m.Tracks).Order().SequenceEqual(Enumerable.Range(1, inspection.Tracks.Count)))
                    return false;
            }
            else
                return false;
            if (execution is not null && (tool is null || !tool.Rid.StartsWith("linux-", StringComparison.Ordinal) || execution.ProcessId <= 0 || execution.ProcessId == baseReply.Identity.ProcessId ||
                execution.ExitCode is < 0 or > 255 || execution.StandardOutputBytes is < 0 or > (256 << 10) || execution.StandardErrorBytes < 0 ||
                execution.StandardErrorBytes > (256 << 10) - execution.StandardOutputBytes || execution.CpuMicroseconds is < 0 or > 14_500_000_000))
                return false;
            reply = new(status, baseReply.Identity, NativeIdentity: native, CdInspection: inspection, ToolIdentity: tool,
                ToolExecution: execution, CdEncoding: encoded, CdVerification: verified, CdFailure: failure);
            return true;
            T? Read<T>(string name) where T : class => root.GetProperty(name).ValueKind == JsonValueKind.Null ? null :
                ChdCdJson.Read<T>(Encoding.UTF8.GetBytes(root.GetProperty(name).GetRawText()), MaximumCdJobOutputBytes);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException or InvalidOperationException or OverflowException) { return false; }
    }
    private static bool ValidCdHashes(ChdCdFileHashes? value, long maximum) => value is not null && value.Bytes >= 1 && value.Bytes <= maximum &&
        ChdCdJson.Hash(value.Sha256, 64) && ChdCdJson.Hash(value.Sha1, 40) && ChdCdJson.Hash(value.Md5, 32) && ChdCdJson.Hash(value.Crc32, 8);
}
