using System.Text;
using System.Text.Json;
using Romd.Media.Cd;
using Romd.Media.Chd;
using Romd.Media.Nintendo;
using Romd.Media.PlayStation;
using Romd.Media.Rvz;

namespace Romd.Media.Inspection;

public enum MediaWorkerStatus { Ready, Inspected, Encoded, Unsupported, Malformed, LimitExceeded, IoFailure }

public sealed record MediaWorkerLimits(long AddressSpaceBytes, long DataBytes, long CpuSeconds, long CoreBytes);
public sealed record MediaWorkerIdentity(int ProcessId, int SessionId, long GcHeapLimitBytes,
    string DecoderBuild, string WorkerBuild, MediaWorkerLimits? LinuxLimits, long GcAvailableBytes, long GcRegionRangeBytes, string? JobName = null);
public sealed record MediaInspection(string ArtifactSha256, long ArtifactBytes, string LogicalSha256,
    string Sha1, string Md5, string Crc32, long LogicalBytes, string LogicalPrefix, string EncodingProfile, NintendoDiscKind? DiscKind = null,
    RvzDescriptor? RvzParameters = null, MediaNintendoLayout? NintendoLayout = null, PspUmdObservedFacts? Psp = null);
public sealed record MediaNintendoLayout(int Partitions, int StandardKeyPartitions, int KoreanKeyPartitions);
public sealed record MediaIsoInspection(NintendoIsoInspectionStatus Status, string Profile, NintendoIsoLayout? Layout = null);
/// <summary>Logical PS2 boot observation only; no DVD provenance or game identification.</summary>
public sealed record MediaPs2Inspection(Ps2DvdIdentificationStatus Status, string Profile, Ps2DvdObservedFacts? Facts = null);
/// <summary>Encoding metadata only. No decoded hashes or verification observation are implied.</summary>
public sealed record MediaEncoding(string SourceSha256, long SourceBytes, long LogicalBytes,
    string ArtifactSha256, string ArtifactMd5, string ArtifactSha1, long ArtifactBytes,
    string RecipeVersion, string EncoderBuild, string EncodingProfile, string PriorityKind, int PriorityValue);
/// <summary>Joined tool process scalars only; diagnostic tails never travel on this wire.</summary>
public sealed record ChdToolExecution(int ProcessId, int ExitCode, long StandardOutputBytes, long StandardErrorBytes, long CpuMicroseconds = 0);
public sealed record MediaWorkerReply(MediaWorkerStatus Status, MediaWorkerIdentity Identity, MediaInspection? Inspection = null,
    MediaIsoInspection? IsoInspection = null, MediaEncoding? Encoding = null, ChdNativeIdentity? NativeIdentity = null,
    ChdInspection? ChdInspection = null, ChdRejection? ChdRejection = null, ChdDvdArtifactMetadata? ChdEncoding = null,
    ChdmanToolIdentity? ToolIdentity = null, ChdDvdEncodingRejection? ChdEncodingRejection = null, ChdToolExecution? ToolExecution = null,
    MediaPs2Inspection? Ps2Inspection = null, ChdDvdExtractionMetadata? ChdExtraction = null,
    ChdCdInspection? CdInspection = null, ChdCdEncodedArtifact? CdEncoding = null,
    ChdCdVerification? CdVerification = null, ChdCdJobFailure? CdFailure = null);

/// <summary>One small result document. Artifact and reconstructed bytes never travel through stdout.</summary>
public static partial class MediaWorkerProtocol
{
    public const string Version = "romd-media/v13";
    public const int MaximumOutputBytes = 8192;
    // CD carries 99 track digest rows and up to 99 exact 512-byte metadata payloads encoded as hex.
    public const int MaximumCdOutputBytes = 192 << 10;
    public const int MaximumNativeIdentityBytes = 2048;
    public const int MaximumToolIdentityBytes = 2048;

    public static byte[] Write(MediaWorkerReply reply)
    {
        if (reply.CdEncoding is not null || reply.CdVerification is not null || reply.CdFailure is not null)
            return WriteCdJob(reply);
        if (reply.ChdEncoding is not null && reply.ChdExtraction is not null)
            throw new ArgumentException("A CHD transformation reply must contain exactly one payload variant.", nameof(reply));
        using var output = new MemoryStream();
        using (var json = new Utf8JsonWriter(output))
        {
            json.WriteStartObject();
            json.WriteString("version", Version);
            json.WriteString("status", StatusText(reply.Status));
            json.WriteNumber("pid", reply.Identity.ProcessId);
            json.WriteNumber("session", reply.Identity.SessionId);
            json.WriteNumber("gcHeapLimitBytes", reply.Identity.GcHeapLimitBytes);
            json.WriteNumber("gcAvailableBytes", reply.Identity.GcAvailableBytes);
            json.WriteNumber("gcRegionRangeBytes", reply.Identity.GcRegionRangeBytes);
            json.WriteString("job", reply.Identity.JobName);
            json.WriteString("decoderBuild", reply.Identity.DecoderBuild);
            json.WriteString("workerBuild", reply.Identity.WorkerBuild);
            json.WritePropertyName("linuxLimits");
            if (reply.Identity.LinuxLimits is { } limits)
            {
                json.WriteStartObject();
                json.WriteNumber("addressSpaceBytes", limits.AddressSpaceBytes);
                json.WriteNumber("dataBytes", limits.DataBytes);
                json.WriteNumber("cpuSeconds", limits.CpuSeconds);
                json.WriteNumber("coreBytes", limits.CoreBytes);
                json.WriteEndObject();
            }
            else
                json.WriteNullValue();
            json.WritePropertyName("inspection");
            if (reply.Inspection is { } result)
            {
                json.WriteStartObject();
                json.WriteString("artifactSha256", result.ArtifactSha256);
                json.WriteNumber("artifactBytes", result.ArtifactBytes);
                json.WriteString("logicalSha256", result.LogicalSha256);
                json.WriteString("sha1", result.Sha1);
                json.WriteString("md5", result.Md5);
                json.WriteString("crc32", result.Crc32);
                json.WriteNumber("logicalBytes", result.LogicalBytes);
                json.WriteString("logicalPrefix", result.LogicalPrefix);
                json.WritePropertyName("pspUmd");
                if (result.Psp is { } psp)
                    JsonSerializer.SerializeToElement(new
                    {
                        logicalBytes = psp.LogicalBytes,
                        volumeBlocks = psp.VolumeBlocks,
                        discId = psp.DiscId,
                        discVersion = psp.DiscVersion
                    }).WriteTo(json);
                else
                    json.WriteNullValue();
                json.WritePropertyName("nintendoLayout");
                if (result.NintendoLayout is { } observedLayout)
                {
                    json.WriteStartObject();
                    json.WriteNumber("partitions", observedLayout.Partitions);
                    json.WriteNumber("standardKeyPartitions", observedLayout.StandardKeyPartitions);
                    json.WriteNumber("koreanKeyPartitions", observedLayout.KoreanKeyPartitions);
                    json.WriteEndObject();
                }
                else
                    json.WriteNullValue();
                json.WriteString("encodingProfile", result.EncodingProfile);
                json.WriteString("discKind", result.DiscKind == NintendoDiscKind.GameCube ? "gamecube" : result.DiscKind == NintendoDiscKind.Wii ? "wii" : null);
                json.WritePropertyName("rvzParameters");
                if (result.RvzParameters is { } parameters)
                {
                    json.WriteStartObject();
                    json.WriteNumber("version", parameters.Version);
                    json.WriteNumber("discType", parameters.DiscType);
                    json.WriteNumber("compression", parameters.Compression);
                    json.WriteNumber("compressionLevel", parameters.CompressionLevel);
                    json.WriteNumber("blockBytes", parameters.ChunkSize);
                    json.WriteBoolean("hasWiiPartitions", parameters.HasWiiPartitions);
                    json.WriteEndObject();
                }
                else
                    json.WriteNullValue();
                json.WriteEndObject();
            }
            else
                json.WriteNullValue();
            json.WritePropertyName("isoInspection");
            if (reply.IsoInspection is { } iso)
            {
                json.WriteStartObject();
                json.WriteString("status", iso.Status == NintendoIsoInspectionStatus.Eligible ? "eligible" :
                    iso.Status == NintendoIsoInspectionStatus.Unsupported ? "unsupported" : "invalid");
                json.WriteString("profile", iso.Profile);
                json.WritePropertyName("layout");
                if (iso.Layout is { } layout)
                {
                    json.WriteStartObject();
                    json.WriteString("kind", layout.Kind == NintendoDiscKind.GameCube ? "gamecube" :
                        layout.Kind == NintendoDiscKind.Wii ? "wii" : "invalid");
                    json.WriteNumber("logicalBytes", layout.LogicalBytes);
                    json.WriteNumber("wiiPartitionCount", layout.WiiPartitionCount);
                    json.WriteNumber("standardKeyPartitions", layout.StandardKeyPartitions);
                    json.WriteNumber("koreanKeyPartitions", layout.KoreanKeyPartitions);
                    json.WriteEndObject();
                }
                else
                    json.WriteNullValue();
                json.WriteEndObject();
            }
            else
                json.WriteNullValue();
            json.WritePropertyName("ps2Inspection");
            if (reply.Ps2Inspection is { } ps2)
            {
                json.WriteStartObject();
                json.WriteString("status", ps2.Status switch
                {
                    Ps2DvdIdentificationStatus.Identified => "identified",
                    Ps2DvdIdentificationStatus.NotIdentified => "not-identified",
                    Ps2DvdIdentificationStatus.Malformed => "malformed",
                    Ps2DvdIdentificationStatus.LimitExceeded => "limit-exceeded",
                    _ => "invalid"
                });
                json.WriteString("profile", ps2.Profile);
                json.WritePropertyName("facts");
                if (ps2.Facts is { } boot)
                    JsonSerializer.SerializeToElement(new
                    {
                        logicalBytes = boot.LogicalBytes,
                        volumeBlocks = boot.VolumeBlocks,
                        primaryDescriptorBlock = boot.PrimaryDescriptorBlock,
                        rootDirectoryBlock = boot.RootDirectoryBlock,
                        rootDirectoryBytes = boot.RootDirectoryBytes,
                        systemCnfBlock = boot.SystemCnfBlock,
                        systemCnfBytes = boot.SystemCnfBytes,
                        bootExecutableBlock = boot.BootExecutableBlock,
                        bootExecutableBytes = boot.BootExecutableBytes,
                        observedBoot2Value = boot.ObservedBoot2Value,
                        bootPath = boot.BootPath,
                        bootSerial = boot.BootSerial,
                        entryPoint = boot.EntryPoint,
                        programHeaderCount = boot.ProgramHeaderCount
                    }).WriteTo(json);
                else
                    json.WriteNullValue();
                json.WriteEndObject();
            }
            else
                json.WriteNullValue();
            json.WritePropertyName("encoding");
            if (reply.Encoding is { } encoding)
            {
                json.WriteStartObject();
                json.WriteString("sourceSha256", encoding.SourceSha256);
                json.WriteNumber("sourceBytes", encoding.SourceBytes);
                json.WriteNumber("logicalBytes", encoding.LogicalBytes);
                json.WriteString("artifactSha256", encoding.ArtifactSha256);
                json.WriteString("artifactMd5", encoding.ArtifactMd5);
                json.WriteString("artifactSha1", encoding.ArtifactSha1);
                json.WriteNumber("artifactBytes", encoding.ArtifactBytes);
                json.WriteString("recipeVersion", encoding.RecipeVersion);
                json.WriteString("encoderBuild", encoding.EncoderBuild);
                json.WriteString("encodingProfile", encoding.EncodingProfile);
                json.WriteString("priorityKind", encoding.PriorityKind);
                json.WriteNumber("priorityValue", encoding.PriorityValue);
                json.WriteEndObject();
            }
            else
                json.WriteNullValue();
            json.WritePropertyName("nativeIdentity");
            if (reply.NativeIdentity is { } native)
                WriteNativeIdentity(native).WriteTo(json);
            else
                json.WriteNullValue();
            json.WritePropertyName("chdInspection");
            if (reply.ChdInspection is { } chd)
                JsonSerializer.SerializeToElement(new
                {
                    version = chd.Version,
                    header = new
                    {
                        artifactBytes = chd.Header.ArtifactBytes,
                        logicalBytes = chd.Header.LogicalBytes,
                        hunkBytes = chd.Header.HunkBytes,
                        unitBytes = chd.Header.UnitBytes,
                        hunkCount = chd.Header.HunkCount,
                        codec0 = chd.Header.Codec0,
                        codec1 = chd.Header.Codec1,
                        codec2 = chd.Header.Codec2,
                        codec3 = chd.Header.Codec3
                    },
                    storedRawSha1 = chd.StoredRawSha1,
                    computedRawSha1 = chd.ComputedRawSha1,
                    storedOverallSha1 = chd.StoredOverallSha1,
                    computedOverallSha1 = chd.ComputedOverallSha1,
                    dvdMetadata = chd.DvdMetadata is { } metadata ? new
                    {
                        profile = metadata.Profile,
                        entryCount = metadata.EntryCount,
                        tag = metadata.Tag,
                        flags = metadata.Flags,
                        payloadBytes = metadata.PayloadBytes,
                        payloadByte = metadata.PayloadByte,
                        normalizedSha256 = metadata.NormalizedSha256
                    } : null
                }).WriteTo(json);
            else
                json.WriteNullValue();
            json.WritePropertyName("cdInspection");
            if (reply.CdInspection is { } cd)
                WriteCdInspection(cd).WriteTo(json);
            else
                json.WriteNullValue();
            json.WritePropertyName("chdRejection");
            if (reply.ChdRejection is { } rejection)
                JsonSerializer.SerializeToElement(new
                {
                    stage = rejection.Stage.ToString().ToLowerInvariant(),
                    cause = rejection.Cause,
                    path = rejection.Path,
                    codecSlot = rejection.CodecSlot,
                    codec = rejection.Codec,
                    nativeError = rejection.NativeError
                }).WriteTo(json);
            else
                json.WriteNullValue();
            json.WritePropertyName("chdEncoding");
            if (reply.ChdEncoding is { } artifact)
                JsonSerializer.SerializeToElement(new
                {
                    sourceSha256 = artifact.SourceSha256,
                    sourceBytes = artifact.SourceBytes,
                    logicalBytes = artifact.LogicalBytes,
                    artifactSha256 = artifact.ArtifactSha256,
                    artifactMd5 = artifact.ArtifactMd5,
                    artifactSha1 = artifact.ArtifactSha1,
                    artifactBytes = artifact.ArtifactBytes,
                    header = new
                    {
                        artifactBytes = artifact.Header.ArtifactBytes,
                        logicalBytes = artifact.Header.LogicalBytes,
                        hunkBytes = artifact.Header.HunkBytes,
                        unitBytes = artifact.Header.UnitBytes,
                        hunkCount = artifact.Header.HunkCount,
                        codec0 = artifact.Header.Codec0,
                        codec1 = artifact.Header.Codec1,
                        codec2 = artifact.Header.Codec2,
                        codec3 = artifact.Header.Codec3
                    },
                    recipeVersion = artifact.RecipeVersion,
                    encodingProfile = artifact.EncodingProfile,
                    priorityKind = artifact.PriorityKind,
                    priorityValue = artifact.PriorityValue
                }).WriteTo(json);
            else if (reply.ChdExtraction is { } extracted)
                JsonSerializer.SerializeToElement(new
                {
                    sourceSha256 = extracted.SourceSha256,
                    sourceBytes = extracted.SourceBytes,
                    logicalSha256 = extracted.LogicalSha256,
                    sha1 = extracted.Sha1,
                    md5 = extracted.Md5,
                    crc32 = extracted.Crc32,
                    logicalBytes = extracted.LogicalBytes,
                    profile = extracted.Profile,
                    priorityKind = extracted.PriorityKind,
                    priorityValue = extracted.PriorityValue
                }).WriteTo(json);
            else
                json.WriteNullValue();
            json.WritePropertyName("toolIdentity");
            if (reply.ToolIdentity is { } toolIdentity)
                WriteToolIdentity(toolIdentity).WriteTo(json);
            else
                json.WriteNullValue();
            json.WritePropertyName("chdEncodingRejection");
            if (reply.ChdEncodingRejection is { } encodingRejection)
            {
                json.WriteStartObject();
                json.WriteString("stage", encodingRejection.Stage.ToString().ToLowerInvariant());
                json.WriteString("cause", encodingRejection.Cause.ToString());
                json.WriteString("path", encodingRejection.Path);
                json.WritePropertyName("candidateRejection");
                if (encodingRejection.CandidateRejection is { } candidateRejection)
                    JsonSerializer.SerializeToElement(new
                    {
                        stage = candidateRejection.Stage.ToString().ToLowerInvariant(),
                        cause = candidateRejection.Cause,
                        path = candidateRejection.Path,
                        codecSlot = candidateRejection.CodecSlot,
                        codec = candidateRejection.Codec,
                        nativeError = candidateRejection.NativeError
                    }).WriteTo(json);
                else
                    json.WriteNullValue();
                json.WriteEndObject();
            }
            else
                json.WriteNullValue();
            json.WritePropertyName("toolExecution");
            if (reply.ToolExecution is { } execution)
                JsonSerializer.SerializeToElement(new
                {
                    processId = execution.ProcessId,
                    exitCode = execution.ExitCode,
                    stdoutBytes = execution.StandardOutputBytes,
                    stderrBytes = execution.StandardErrorBytes,
                    cpuMicroseconds = execution.CpuMicroseconds
                }).WriteTo(json);
            else
                json.WriteNullValue();
            json.WriteEndObject();
        }
        return output.ToArray();
    }

    public static bool TryRead(ReadOnlyMemory<byte> output, out MediaWorkerReply? reply)
    {
        reply = null;
        if (output.Length is > 0 and <= MaximumCdJobOutputBytes && IsCdJob(output))
            return TryReadCdJob(output, out reply);
        if (output.Length is 0 or > MaximumCdOutputBytes)
            return false;
        try
        {
            using var document = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (!HasFields(root, "version", "status", "pid", "session", "gcHeapLimitBytes", "gcAvailableBytes", "gcRegionRangeBytes", "job", "decoderBuild", "workerBuild", "linuxLimits", "inspection", "isoInspection", "ps2Inspection", "encoding", "nativeIdentity", "chdInspection", "chdRejection", "chdEncoding", "toolIdentity", "chdEncodingRejection", "toolExecution", "cdInspection") ||
                Text(root, "version") != Version || !TryStatus(Text(root, "status"), out var status) ||
                !Number(root, "pid", out long pid) || pid is < 1 or > int.MaxValue ||
                !Number(root, "session", out long session) || session is < 0 or > int.MaxValue ||
                !Number(root, "gcHeapLimitBytes", out long heap) || heap <= 0 ||
                !Number(root, "gcAvailableBytes", out long available) || available <= 0 || available > heap ||
                !Number(root, "gcRegionRangeBytes", out long range) || range < 0 ||
                (root.GetProperty("job").ValueKind != JsonValueKind.Null && !Label(Text(root, "job"), 128)) ||
                !Label(Text(root, "decoderBuild"), 256) || !Label(Text(root, "workerBuild"), 256))
                return false;
            MediaWorkerLimits? limits = null;
            var linux = root.GetProperty("linuxLimits");
            if (linux.ValueKind != JsonValueKind.Null)
            {
                if (!HasFields(linux, "addressSpaceBytes", "dataBytes", "cpuSeconds", "coreBytes") ||
                    !Number(linux, "addressSpaceBytes", out long address) || address <= 0 ||
                    !Number(linux, "dataBytes", out long data) || data <= 0 ||
                    !Number(linux, "cpuSeconds", out long cpu) || cpu <= 0 ||
                    !Number(linux, "coreBytes", out long core) || core != 0)
                    return false;
                limits = new(address, data, cpu, core);
            }
            ChdNativeIdentity? nativeIdentity = null;
            var native = root.GetProperty("nativeIdentity");
            if (native.ValueKind != JsonValueKind.Null && !TryReadNativeIdentity(native, out nativeIdentity))
                return false;
            ChdInspection? chdInspection = null;
            var chd = root.GetProperty("chdInspection");
            if (chd.ValueKind != JsonValueKind.Null && !TryReadChdInspection(chd, out chdInspection))
                return false;
            ChdCdInspection? cdInspection = null;
            var cd = root.GetProperty("cdInspection");
            if (output.Length > MaximumOutputBytes && cd.ValueKind == JsonValueKind.Null)
                return false;
            if (cd.ValueKind != JsonValueKind.Null && !TryReadCdInspection(cd, out cdInspection))
                return false;
            ChdRejection? chdRejection = null;
            var rejection = root.GetProperty("chdRejection");
            if (rejection.ValueKind != JsonValueKind.Null && !TryReadChdRejection(rejection, status, nativeIdentity, out chdRejection))
                return false;
            if (cdInspection is not null && (status != MediaWorkerStatus.Inspected || nativeIdentity is null ||
                    chdInspection is not null || chdRejection is not null ||
                    new[] { "inspection", "isoInspection", "ps2Inspection", "encoding", "chdEncoding", "toolIdentity", "chdEncodingRejection", "toolExecution" }
                        .Any(field => root.GetProperty(field).ValueKind != JsonValueKind.Null)) ||
                chdInspection is not null && (status != MediaWorkerStatus.Inspected || nativeIdentity is null || chdRejection is not null) ||
                chdRejection is not null && chdInspection is not null ||
                nativeIdentity is not null && status != MediaWorkerStatus.Ready && chdInspection is null && cdInspection is null && chdRejection is null)
                return false;
            ChdmanToolIdentity? toolIdentity = null;
            var tool = root.GetProperty("toolIdentity");
            if (tool.ValueKind != JsonValueKind.Null && !TryReadToolIdentity(tool, out toolIdentity))
                return false;
            ChdDvdArtifactMetadata? chdEncoding = null;
            ChdDvdExtractionMetadata? chdExtraction = null;
            var artifact = root.GetProperty("chdEncoding");
            if (artifact.ValueKind != JsonValueKind.Null && !TryReadChdEncoding(artifact, out chdEncoding) &&
                !TryReadChdExtraction(artifact, out chdExtraction))
                return false;
            ChdToolExecution? toolExecution = null;
            var toolRun = root.GetProperty("toolExecution");
            if (toolRun.ValueKind != JsonValueKind.Null)
            {
                if (!HasFields(toolRun, "processId", "exitCode", "stdoutBytes", "stderrBytes", "cpuMicroseconds") ||
                    !Number(toolRun, "processId", out long toolPid) || toolPid is < 1 or > int.MaxValue || toolPid == pid ||
                    !Number(toolRun, "exitCode", out long toolExit) || toolExit is < 0 or > 255 ||
                    !Number(toolRun, "stdoutBytes", out long stdout) || stdout is < 0 or > (16L << 30) ||
                    !Number(toolRun, "stderrBytes", out long stderr) || stderr is < 0 or > (16L << 30) ||
                    !Number(toolRun, "cpuMicroseconds", out long cpuMicroseconds) || cpuMicroseconds is < 0 or > 14_500_000_000)
                    return false;
                toolExecution = new((int)toolPid, (int)toolExit, stdout, stderr, cpuMicroseconds);
            }
            ChdDvdEncodingRejection? chdEncodingRejection = null;
            var rejectedEncoding = root.GetProperty("chdEncodingRejection");
            if (rejectedEncoding.ValueKind != JsonValueKind.Null &&
                !TryReadChdEncodingRejection(rejectedEncoding, status, toolIdentity, toolExecution, out chdEncodingRejection))
                return false;
            bool chdEncode = chdEncoding is not null || chdExtraction is not null || toolIdentity is not null || chdEncodingRejection is not null || toolExecution is not null;
            if (chdEncode && (toolIdentity is not null && !toolIdentity.Rid.StartsWith("linux-", StringComparison.Ordinal) ||
                    nativeIdentity is not null || chdInspection is not null || chdRejection is not null ||
                    root.GetProperty("inspection").ValueKind != JsonValueKind.Null || root.GetProperty("isoInspection").ValueKind != JsonValueKind.Null ||
                    root.GetProperty("encoding").ValueKind != JsonValueKind.Null ||
                    (status == MediaWorkerStatus.Encoded ? chdEncoding is null && chdExtraction is null || toolIdentity is null || toolExecution is null ||
                        chdEncodingRejection is not null || toolExecution.ExitCode != 0 ||
                        toolExecution.StandardOutputBytes + toolExecution.StandardErrorBytes > ChdmanToolRunner.MaximumDiagnosticBytes :
                        chdEncoding is not null || chdExtraction is not null || chdEncodingRejection is null)))
                return false;
            MediaIsoInspection? isoInspection = null;
            var iso = root.GetProperty("isoInspection");
            if (iso.ValueKind != JsonValueKind.Null)
            {
                if (status != MediaWorkerStatus.Inspected || nativeIdentity is not null || chdInspection is not null || chdRejection is not null ||
                    !HasFields(iso, "status", "profile", "layout") ||
                    Text(iso, "profile") != NintendoIsoInspector.ProfileVersion)
                    return false;
                NintendoIsoInspectionStatus isoStatus;
                NintendoIsoLayout? isoLayout = null;
                var layout = iso.GetProperty("layout");
                if (Text(iso, "status") == "unsupported")
                {
                    isoStatus = NintendoIsoInspectionStatus.Unsupported;
                    if (layout.ValueKind != JsonValueKind.Null)
                        return false;
                }
                else if (Text(iso, "status") == "eligible")
                {
                    isoStatus = NintendoIsoInspectionStatus.Eligible;
                    if (!HasFields(layout, "kind", "logicalBytes", "wiiPartitionCount", "standardKeyPartitions", "koreanKeyPartitions") ||
                        !Number(layout, "logicalBytes", out long layoutBytes) ||
                        !Number(layout, "wiiPartitionCount", out long partitions) || partitions is < 0 or > 64 ||
                        !Number(layout, "standardKeyPartitions", out long standard) || standard is < 0 or > 64 ||
                        !Number(layout, "koreanKeyPartitions", out long korean) || korean is < 0 or > 64)
                        return false;
                    NintendoDiscKind kind;
                    if (Text(layout, "kind") == "gamecube" && layoutBytes >= 0x2460 && partitions == 0 && standard == 0 && korean == 0)
                        kind = NintendoDiscKind.GameCube;
                    else if (Text(layout, "kind") == "wii" && layoutBytes >= 0x40020 && partitions > 0 && standard + korean == partitions)
                        kind = NintendoDiscKind.Wii;
                    else
                        return false;
                    isoLayout = new(kind, layoutBytes, (int)partitions, (int)standard, (int)korean);
                }
                else
                    return false;
                isoInspection = new(isoStatus, NintendoIsoInspector.ProfileVersion, isoLayout);
            }
            MediaInspection? inspection = null;
            var measured = root.GetProperty("inspection");
            if (status == MediaWorkerStatus.Inspected && cdInspection is null)
            {
                if (!HasFields(measured, "artifactSha256", "artifactBytes", "logicalSha256", "sha1", "md5", "crc32", "logicalBytes", "logicalPrefix", "nintendoLayout", "encodingProfile", "discKind", "rvzParameters", "pspUmd") ||
                    !IdentityHash(Text(measured, "artifactSha256"), 64) || !IdentityHash(Text(measured, "logicalSha256"), 64) ||
                    !IdentityHash(Text(measured, "sha1"), 40) || !IdentityHash(Text(measured, "md5"), 32) || !Hash(Text(measured, "crc32"), 8) ||
                    !Number(measured, "artifactBytes", out long artifactBytes) || artifactBytes < (isoInspection is null ? 4 : 0) ||
                    !Number(measured, "logicalBytes", out long logicalBytes) || (isoInspection is null ? logicalBytes <= 0x80 : logicalBytes < 0) ||
                    !Label(Text(measured, "encodingProfile"), 128) ||
                    (chdInspection is not null ? Text(measured, "encodingProfile") != "chd-import/v1" ||
                        artifactBytes != chdInspection.Header.ArtifactBytes || (ulong)logicalBytes != chdInspection.Header.LogicalBytes ||
                        Text(measured, "sha1") != chdInspection.ComputedRawSha1 :
                     isoInspection is null ? !RvzInspectionProfile.IsValid(Text(measured, "encodingProfile")!) :
                        Text(measured, "encodingProfile") != "identity/v1" || artifactBytes != logicalBytes ||
                        Text(measured, "artifactSha256") != Text(measured, "logicalSha256") ||
                        isoInspection.Layout is { } isoLayout && isoLayout.LogicalBytes != logicalBytes))
                    return false;
                string? prefix = Text(measured, "logicalPrefix");
                if (!Hash(prefix, checked((int)Math.Min(logicalBytes, 32) * 2)))
                    return false;
                MediaNintendoLayout? nintendo = null;
                var layoutValue = measured.GetProperty("nintendoLayout");
                if (layoutValue.ValueKind != JsonValueKind.Null)
                {
                    if (!HasFields(layoutValue, "partitions", "standardKeyPartitions", "koreanKeyPartitions") ||
                        !Number(layoutValue, "partitions", out long partitions) || partitions is < 0 or > 64 ||
                        !Number(layoutValue, "standardKeyPartitions", out long standard) || standard is < 0 or > 64 ||
                        !Number(layoutValue, "koreanKeyPartitions", out long korean) || korean is < 0 or > 64 || standard + korean != partitions ||
                        logicalBytes < 32)
                        return false;
                    nintendo = new((int)partitions, (int)standard, (int)korean);
                }
                RvzDescriptor? rvzParameters = null;
                var capturedRvz = measured.GetProperty("rvzParameters");
                if (isoInspection is null && chdInspection is null)
                {
                    if (!TryReadRvzParameters(capturedRvz, out rvzParameters) ||
                        RvzInspectionProfile.Capture(rvzParameters!) != Text(measured, "encodingProfile"))
                        return false;
                }
                else if (capturedRvz.ValueKind != JsonValueKind.Null)
                    return false;
                NintendoDiscKind? measuredKind;
                if (measured.GetProperty("discKind").ValueKind == JsonValueKind.Null)
                    measuredKind = null;
                else if (Text(measured, "discKind") == "gamecube")
                    measuredKind = NintendoDiscKind.GameCube;
                else if (Text(measured, "discKind") == "wii")
                    measuredKind = NintendoDiscKind.Wii;
                else
                    return false;
                if ((measuredKind is not null) != (nintendo is not null))
                    return false;
                if (measuredKind is { } logicalKind)
                {
                    byte[] rawPrefix = Convert.FromHexString(prefix!);
                    int offset = logicalKind == NintendoDiscKind.GameCube ? 0x1c : 0x18;
                    uint magic = logicalKind == NintendoDiscKind.GameCube ? 0xc2339f3d : 0x5d1c9ea3;
                    if (rawPrefix.Length != 32 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(rawPrefix.AsSpan(offset)) != magic ||
                        logicalKind == NintendoDiscKind.GameCube && nintendo!.Partitions != 0 ||
                        logicalKind == NintendoDiscKind.Wii && nintendo!.Partitions == 0)
                        return false;
                }
                if (rvzParameters is not null && measuredKind is { } rvzKind &&
                    rvzParameters.DiscType != (rvzKind == NintendoDiscKind.GameCube ? 1u : 2u))
                    return false;
                if (isoInspection is not null && (isoInspection.Layout?.Kind != measuredKind ||
                    isoInspection.Layout is { } inspected && (nintendo is null || nintendo.Partitions != inspected.WiiPartitionCount ||
                        nintendo.StandardKeyPartitions != inspected.StandardKeyPartitions || nintendo.KoreanKeyPartitions != inspected.KoreanKeyPartitions)))
                    return false;
                if (chdInspection is not null && measuredKind is not null || chdRejection is not null)
                    return false;
                PspUmdObservedFacts? psp = null;
                var pspValue = measured.GetProperty("pspUmd");
                if (pspValue.ValueKind != JsonValueKind.Null)
                {
                    if (measuredKind is not null || isoInspection is null && chdInspection is null ||
                        !HasFields(pspValue, "logicalBytes", "volumeBlocks", "discId", "discVersion") ||
                        !Number(pspValue, "logicalBytes", out long pspBytes) || pspBytes != logicalBytes ||
                        !Number(pspValue, "volumeBlocks", out long volumeBlocks) || volumeBlocks is < 0 or > uint.MaxValue ||
                        Text(pspValue, "discId") is not { } discId || Text(pspValue, "discVersion") is not { } discVersion)
                        return false;
                    psp = new(pspBytes, (uint)volumeBlocks, discId, discVersion);
                    if (!PspUmdIdentifier.ValidFacts(psp))
                        return false;
                }
                inspection = new(Text(measured, "artifactSha256")!, artifactBytes, Text(measured, "logicalSha256")!,
                    Text(measured, "sha1")!, Text(measured, "md5")!, Text(measured, "crc32")!, logicalBytes, prefix!, Text(measured, "encodingProfile")!, measuredKind, rvzParameters, nintendo, psp);
            }
            else if (measured.ValueKind != JsonValueKind.Null)
                return false;
            MediaPs2Inspection? ps2Inspection = null;
            var ps2 = root.GetProperty("ps2Inspection");
            if (ps2.ValueKind != JsonValueKind.Null &&
                (status != MediaWorkerStatus.Inspected || inspection is null ||
                 isoInspection is null && chdInspection is null ||
                 !TryReadPs2Inspection(ps2, inspection, out ps2Inspection) ||
                 ps2Inspection!.Status == Ps2DvdIdentificationStatus.Identified && (inspection.DiscKind is not null || inspection.Psp is not null)))
                return false;
            MediaEncoding? encoding = null;
            var encoded = root.GetProperty("encoding");
            if (status == MediaWorkerStatus.Encoded && !chdEncode)
            {
                if (inspection is not null || isoInspection is not null || nativeIdentity is not null || chdInspection is not null || chdRejection is not null ||
                    !HasFields(encoded, "sourceSha256", "sourceBytes", "logicalBytes", "artifactSha256", "artifactMd5", "artifactSha1",
                        "artifactBytes", "recipeVersion", "encoderBuild", "encodingProfile", "priorityKind", "priorityValue") ||
                    !IdentityHash(Text(encoded, "sourceSha256"), 64) || !IdentityHash(Text(encoded, "artifactSha256"), 64) ||
                    !IdentityHash(Text(encoded, "artifactMd5"), 32) || !IdentityHash(Text(encoded, "artifactSha1"), 40) ||
                    !Number(encoded, "sourceBytes", out long sourceBytes) || sourceBytes < 4 ||
                    !Number(encoded, "logicalBytes", out long logicalBytes) || logicalBytes <= 0x80 ||
                    !Number(encoded, "artifactBytes", out long artifactBytes) || artifactBytes < 0x124 ||
                    Text(encoded, "recipeVersion") != RvzCanonicalRecipe.Id ||
                    !Label(Text(encoded, "encoderBuild"), 256) || !RvzInspectionProfile.IsValid(Text(encoded, "encodingProfile") ?? "") ||
                    Text(encoded, "encodingProfile") != CanonicalEncodingProfile ||
                    !Number(encoded, "priorityValue", out long priority) ||
                    !(Text(encoded, "priorityKind") == "unix-nice" && priority is >= 10 and <= 19 ||
                      Text(encoded, "priorityKind") == "windows-priority" && priority == 0x4000))
                    return false;
                encoding = new(Text(encoded, "sourceSha256")!, sourceBytes, logicalBytes, Text(encoded, "artifactSha256")!,
                    Text(encoded, "artifactMd5")!, Text(encoded, "artifactSha1")!, artifactBytes, RvzCanonicalRecipe.Id,
                    Text(encoded, "encoderBuild")!, Text(encoded, "encodingProfile")!, Text(encoded, "priorityKind")!, (int)priority);
            }
            else if (encoded.ValueKind != JsonValueKind.Null)
                return false;
            reply = new(status, new((int)pid, (int)session, heap, Text(root, "decoderBuild")!, Text(root, "workerBuild")!, limits,
                available, range, Text(root, "job")), inspection, isoInspection, encoding, nativeIdentity, chdInspection, chdRejection,
                chdEncoding, toolIdentity, chdEncodingRejection, toolExecution, ps2Inspection, chdExtraction, cdInspection);
            return true;
        }
        catch (JsonException) { return false; }
    }

    // The fixed encoder writes this container version with the static recipe. Producer build is a separate fact.
    public static string CanonicalEncodingProfile => FormattableString.Invariant(
        $"rvz-import/v1;codec={RvzCanonicalRecipe.Compression};level={RvzCanonicalRecipe.CompressionLevel};block={RvzCanonicalRecipe.ChunkSize};format=01000000;encoder=unknown");

    /// <summary>Tool build-record facts verified on this host, not an executed-image or release attestation.</summary>
    public static JsonElement WriteToolIdentity(ChdmanToolIdentity identity) => JsonSerializer.SerializeToElement(new
    {
        rid = identity.Rid,
        mameTag = identity.MameTag,
        mameSourceSha256 = identity.MameSourceSha256,
        recipeVersion = identity.RecipeVersion,
        binaryBytes = identity.BinaryBytes,
        binarySha256 = identity.BinarySha256
    });

    public static bool TryReadToolIdentity(JsonElement value, out ChdmanToolIdentity? identity)
    {
        identity = null;
        if (!HasFields(value, "rid", "mameTag", "mameSourceSha256", "recipeVersion", "binaryBytes", "binarySha256") ||
            Encoding.UTF8.GetByteCount(value.GetRawText()) > MaximumToolIdentityBytes ||
            !NativeRuntimeIdentifier.IsLabel(Text(value, "rid")) || !NativeRuntimeIdentifier.IsLabel(Text(value, "mameTag")) ||
            !IdentityHash(Text(value, "mameSourceSha256"), 64) ||
            !Number(value, "recipeVersion", out long recipe) || recipe is < 1 or > int.MaxValue ||
            !Number(value, "binaryBytes", out long binary) || binary is <= 0 or > (64L << 20) ||
            !IdentityHash(Text(value, "binarySha256"), 64))
            return false;
        identity = new(Text(value, "rid")!, Text(value, "mameTag")!, Text(value, "mameSourceSha256")!, (int)recipe,
            binary, Text(value, "binarySha256")!);
        return true;
    }

    private static bool TryReadChdEncoding(JsonElement value, out ChdDvdArtifactMetadata? artifact)
    {
        artifact = null;
        if (!HasFields(value, "sourceSha256", "sourceBytes", "logicalBytes", "artifactSha256", "artifactMd5", "artifactSha1",
                "artifactBytes", "header", "recipeVersion", "encodingProfile", "priorityKind", "priorityValue") ||
            !IdentityHash(Text(value, "sourceSha256"), 64) || !IdentityHash(Text(value, "artifactSha256"), 64) ||
            !IdentityHash(Text(value, "artifactMd5"), 32) || !IdentityHash(Text(value, "artifactSha1"), 40) ||
            !Number(value, "sourceBytes", out long source) || source is < 2048 or > (16L << 30) || source % 2048 != 0 ||
            !Number(value, "logicalBytes", out long logical) || logical != source ||
            !Number(value, "artifactBytes", out long bytes) || bytes is < 124 or > (16L << 30) ||
            Text(value, "recipeVersion") != ChdDvdCanonicalRecipe.Id ||
            Text(value, "encodingProfile") != ChdDvdCanonicalRecipe.EncoderProfile ||
            Text(value, "priorityKind") != "unix-nice" ||
            !Number(value, "priorityValue", out long priority) || priority is < 10 or > 19)
            return false;
        var header = value.GetProperty("header");
        if (!HasFields(header, "artifactBytes", "logicalBytes", "hunkBytes", "unitBytes", "hunkCount", "codec0", "codec1", "codec2", "codec3") ||
            !Number(header, "artifactBytes", out long headerBytes) || headerBytes != bytes ||
            !Number(header, "logicalBytes", out long headerLogical) || headerLogical != logical ||
            !Number(header, "hunkBytes", out long hunk) || hunk != ChdDvdCanonicalRecipe.HunkBytes ||
            !Number(header, "unitBytes", out long unit) || unit != ChdDvdCanonicalRecipe.UnitBytes ||
            !Number(header, "hunkCount", out long count) || count != logical / hunk + (logical % hunk == 0 ? 0 : 1) ||
            !Number(header, "codec0", out long codec0) || codec0 != (uint)ChdDvdCanonicalRecipe.Codec ||
            !Number(header, "codec1", out long codec1) || codec1 != 0 ||
            !Number(header, "codec2", out long codec2) || codec2 != 0 ||
            !Number(header, "codec3", out long codec3) || codec3 != 0)
            return false;
        artifact = new(Text(value, "sourceSha256")!, source, logical, Text(value, "artifactSha256")!,
            Text(value, "artifactMd5")!, Text(value, "artifactSha1")!, bytes,
            new(bytes, (ulong)logical, ChdDvdCanonicalRecipe.HunkBytes, ChdDvdCanonicalRecipe.UnitBytes, (uint)count,
                (uint)ChdDvdCanonicalRecipe.Codec, 0, 0, 0),
            ChdDvdCanonicalRecipe.Id, ChdDvdCanonicalRecipe.EncoderProfile, "unix-nice", (int)priority);
        return true;
    }

    private static bool TryReadChdExtraction(JsonElement value, out ChdDvdExtractionMetadata? extraction)
    {
        extraction = null;
        if (!HasFields(value, "sourceSha256", "sourceBytes", "logicalSha256", "sha1", "md5", "crc32", "logicalBytes",
                "profile", "priorityKind", "priorityValue") ||
            !IdentityHash(Text(value, "sourceSha256"), 64) || !IdentityHash(Text(value, "logicalSha256"), 64) ||
            !IdentityHash(Text(value, "sha1"), 40) || !IdentityHash(Text(value, "md5"), 32) || !Hash(Text(value, "crc32"), 8) ||
            !Number(value, "sourceBytes", out long source) || source is < 124 or > ChdDvdLimits.MaximumArtifactBytes ||
            !Number(value, "logicalBytes", out long logical) || logical is < 2048 or > ChdDvdLimits.MaximumLogicalBytes || logical % 2048 != 0 ||
            Text(value, "profile") != ChdDvdEncodingService.ExtractionProfile || Text(value, "priorityKind") != "unix-nice" ||
            !Number(value, "priorityValue", out long priority) || priority is < 10 or > 19)
            return false;
        extraction = new(Text(value, "sourceSha256")!, source, Text(value, "logicalSha256")!, Text(value, "sha1")!,
            Text(value, "md5")!, Text(value, "crc32")!, logical, ChdDvdEncodingService.ExtractionProfile, "unix-nice", (int)priority);
        return true;
    }

    private static bool TryReadChdEncodingRejection(JsonElement value, MediaWorkerStatus status, ChdmanToolIdentity? identity,
        ChdToolExecution? execution, out ChdDvdEncodingRejection? rejection)
    {
        rejection = null;
        if (!HasFields(value, "stage", "cause", "path", "candidateRejection") ||
            !Label(Text(value, "path"), 1024) || !Path.IsPathFullyQualified(Text(value, "path")!) ||
            !Enum.TryParse(Text(value, "cause"), out ChdDvdEncodingCause cause) ||
            !Enum.IsDefined(cause) || cause.ToString() != Text(value, "cause"))
            return false;
        ChdDvdEncodingStage stage;
        switch (Text(value, "stage"))
        {
            case "source":
                stage = ChdDvdEncodingStage.Source;
                break;
            case "toolverification":
                stage = ChdDvdEncodingStage.ToolVerification;
                break;
            case "toollaunch":
                stage = ChdDvdEncodingStage.ToolLaunch;
                break;
            case "toolimage":
                stage = ChdDvdEncodingStage.ToolImage;
                break;
            case "toolexecution":
                stage = ChdDvdEncodingStage.ToolExecution;
                break;
            case "output":
                stage = ChdDvdEncodingStage.Output;
                break;
            default:
                return false;
        }
        var nested = value.GetProperty("candidateRejection");
        if (cause == ChdDvdEncodingCause.CandidateRejected)
        {
            if (stage != ChdDvdEncodingStage.ToolVerification || identity is not null || execution is not null ||
                !TryReadChdRejection(nested, status, null, out var candidate) || candidate is null ||
                candidate.Stage != ChdRejectionStage.NativeVerification || candidate.Cause == "ResidentBuildMismatch" ||
                candidate.Path != Text(value, "path"))
                return false;
            rejection = new(stage, cause, candidate.Path, candidate);
            return true;
        }
        if (nested.ValueKind != JsonValueKind.Null || stage == ChdDvdEncodingStage.ToolVerification && identity is not null ||
            stage is ChdDvdEncodingStage.ToolImage or ChdDvdEncodingStage.ToolExecution && identity is null ||
            // A sealed image is refused before any process starts, so it has no execution facts.
            stage == ChdDvdEncodingStage.ToolImage && execution is not null ||
            execution is not null && identity is null)
            return false;
        MediaWorkerStatus? expected = (stage, cause) switch
        {
            (ChdDvdEncodingStage.Source, ChdDvdEncodingCause.InvalidArguments or ChdDvdEncodingCause.LimitExceeded) => MediaWorkerStatus.LimitExceeded,
            (ChdDvdEncodingStage.Source, ChdDvdEncodingCause.InvalidPath or ChdDvdEncodingCause.SourceLengthMismatch or ChdDvdEncodingCause.SourceHashMismatch) => MediaWorkerStatus.Malformed,
            (ChdDvdEncodingStage.Source, ChdDvdEncodingCause.UnsupportedSource) => MediaWorkerStatus.Unsupported,
            (ChdDvdEncodingStage.Source, ChdDvdEncodingCause.SourceIo or ChdDvdEncodingCause.SourceChanged) => MediaWorkerStatus.IoFailure,
            (ChdDvdEncodingStage.Output, ChdDvdEncodingCause.OutputExists or ChdDvdEncodingCause.OutputIo) => MediaWorkerStatus.IoFailure,
            (ChdDvdEncodingStage.Output, ChdDvdEncodingCause.OutputMissing or ChdDvdEncodingCause.InvalidOutputHeader or ChdDvdEncodingCause.OutputLengthMismatch) => MediaWorkerStatus.Malformed,
            (ChdDvdEncodingStage.Output, ChdDvdEncodingCause.OutputLimitExceeded) => MediaWorkerStatus.LimitExceeded,
            (ChdDvdEncodingStage.ToolLaunch, ChdDvdEncodingCause.LinuxRequired) => MediaWorkerStatus.Unsupported,
            (ChdDvdEncodingStage.ToolLaunch, ChdDvdEncodingCause.ToolPriorityMismatch) => MediaWorkerStatus.IoFailure,
            (ChdDvdEncodingStage.ToolImage, ChdDvdEncodingCause.ToolImageMismatch) => MediaWorkerStatus.IoFailure,
            (ChdDvdEncodingStage.ToolExecution, ChdDvdEncodingCause.CpuBudgetExceeded) => MediaWorkerStatus.LimitExceeded,
            (ChdDvdEncodingStage.ToolExecution, ChdDvdEncodingCause.DiagnosticLimitExceeded) => MediaWorkerStatus.LimitExceeded,
            (ChdDvdEncodingStage.ToolExecution, ChdDvdEncodingCause.ToolExitFailure) => MediaWorkerStatus.Malformed,
            (ChdDvdEncodingStage.ToolExecution, ChdDvdEncodingCause.DrainFailure) => MediaWorkerStatus.IoFailure,
            (ChdDvdEncodingStage.ToolVerification or ChdDvdEncodingStage.ToolLaunch, ChdDvdEncodingCause.LaunchFailure) => MediaWorkerStatus.IoFailure,
            (ChdDvdEncodingStage.Source or ChdDvdEncodingStage.Output or ChdDvdEncodingStage.ToolVerification or ChdDvdEncodingStage.ToolLaunch or ChdDvdEncodingStage.ToolExecution,
                ChdDvdEncodingCause.UnsafePath) => MediaWorkerStatus.IoFailure,
            _ => null
        };
        if (expected is null || expected != status ||
            cause == ChdDvdEncodingCause.DiagnosticLimitExceeded && execution is not null &&
                execution.StandardOutputBytes + execution.StandardErrorBytes <= ChdmanToolRunner.MaximumDiagnosticBytes ||
            cause == ChdDvdEncodingCause.ToolExitFailure && execution is not null && execution.ExitCode == 0 ||
            cause == ChdDvdEncodingCause.CpuBudgetExceeded &&
                (execution is null || execution.ExitCode is not (137 or 152) || execution.CpuMicroseconds <= 0))
            return false;
        rejection = new(stage, cause, Text(value, "path")!);
        return true;
    }

    /// <summary>Native build-record facts, measured resident facts once loaded; never a release attestation.</summary>
    public static JsonElement WriteNativeIdentity(ChdNativeIdentity identity) => JsonSerializer.SerializeToElement(new
    {
        rid = identity.Rid,
        file = identity.File,
        binaryBytes = identity.BinaryBytes,
        binarySha256 = identity.BinarySha256,
        upstreamCommit = identity.UpstreamCommit,
        upstreamVersion = identity.UpstreamVersion,
        recipeVersion = identity.RecipeVersion,
        familyVersion = identity.FamilyVersion,
        buildId = identity.BuildId,
        hasRawSectors = identity.HasRawSectors,
        hasSubcode = identity.HasSubcode,
        verifiesBlockCrc = identity.VerifiesBlockCrc
    });

    public static bool TryReadNativeIdentity(JsonElement value, out ChdNativeIdentity? identity)
    {
        identity = null;
        if (!HasFields(value, "rid", "file", "binaryBytes", "binarySha256", "upstreamCommit", "upstreamVersion", "recipeVersion",
                "familyVersion", "buildId", "hasRawSectors", "hasSubcode", "verifiesBlockCrc") ||
            Encoding.UTF8.GetByteCount(value.GetRawText()) > MaximumNativeIdentityBytes ||
            !NativeRuntimeIdentifier.IsLabel(Text(value, "rid")) || !FileName(Text(value, "file")) ||
            !Number(value, "binaryBytes", out long binary) || binary is <= 0 or > (32L << 20) ||
            !IdentityHash(Text(value, "binarySha256"), 64) || !IdentityHash(Text(value, "upstreamCommit"), 40) ||
            !Label(Text(value, "upstreamVersion"), 128) || !Number(value, "recipeVersion", out long recipe) || recipe is < 1 or > int.MaxValue ||
            !Label(Text(value, "familyVersion"), 128) || !IdentityHash(Text(value, "buildId"), 64) ||
            value.GetProperty("hasRawSectors").ValueKind != JsonValueKind.True ||
            value.GetProperty("hasSubcode").ValueKind != JsonValueKind.True ||
            value.GetProperty("verifiesBlockCrc").ValueKind != JsonValueKind.True)
            return false;
        identity = new(Text(value, "rid")!, Text(value, "file")!, binary, Text(value, "binarySha256")!, Text(value, "upstreamCommit")!,
            Text(value, "upstreamVersion")!, (int)recipe, Text(value, "familyVersion")!, Text(value, "buildId")!, true, true, true);
        return true;
    }

    // A bare file name inside the build's native directory: never a separator, rooted path or parent reference.
    private static bool FileName(string? value) => Label(value, 255) && value is not ("." or "..") &&
        value!.IndexOfAny(['/', '\\']) < 0 && !Path.IsPathRooted(value) && Path.GetFileName(value) == value;

    private static bool TryReadDvdMetadata(JsonElement value, out ChdDvdMetadataObservation? observation)
    {
        observation = null;
        if (!HasFields(value, "profile", "entryCount", "tag", "flags", "payloadBytes", "payloadByte", "normalizedSha256") ||
            Text(value, "profile") != ChdDvdMetadataObserver.Profile ||
            !Number(value, "entryCount", out long count) || count != 1 ||
            !Number(value, "tag", out long tag) || tag != 0x44564420 ||
            !Number(value, "flags", out long flags) || flags != 1 ||
            !Number(value, "payloadBytes", out long bytes) || bytes != 1 ||
            !Number(value, "payloadByte", out long payload) || payload != 0 ||
            Text(value, "normalizedSha256") != ChdDvdMetadataObserver.ExpectedNormalizedSha256)
            return false;
        observation = new(ChdDvdMetadataObserver.Profile, 1, 0x44564420, 1, 1, 0,
            ChdDvdMetadataObserver.ExpectedNormalizedSha256);
        return true;
    }

    private static bool TryReadPs2Inspection(JsonElement value, MediaInspection measurement, out MediaPs2Inspection? inspection)
    {
        inspection = null;
        if (!HasFields(value, "status", "profile", "facts") || Text(value, "profile") != Ps2DvdIdentifier.ProfileVersion)
            return false;
        Ps2DvdIdentificationStatus status;
        switch (Text(value, "status"))
        {
            case "identified":
                status = Ps2DvdIdentificationStatus.Identified;
                break;
            case "not-identified":
                status = Ps2DvdIdentificationStatus.NotIdentified;
                break;
            case "malformed":
                status = Ps2DvdIdentificationStatus.Malformed;
                break;
            case "limit-exceeded":
                status = Ps2DvdIdentificationStatus.LimitExceeded;
                break;
            default:
                return false;
        }
        var facts = value.GetProperty("facts");
        if (status != Ps2DvdIdentificationStatus.Identified)
        {
            if (facts.ValueKind != JsonValueKind.Null)
                return false;
            inspection = new(status, Ps2DvdIdentifier.ProfileVersion);
            return true;
        }
        if (!HasFields(facts, "logicalBytes", "volumeBlocks", "primaryDescriptorBlock", "rootDirectoryBlock",
                "rootDirectoryBytes", "systemCnfBlock", "systemCnfBytes", "bootExecutableBlock", "bootExecutableBytes",
                "observedBoot2Value", "bootPath", "bootSerial", "entryPoint", "programHeaderCount") ||
            !Number(facts, "logicalBytes", out long logical) || logical != measurement.LogicalBytes ||
                logical < 18L * 2048 || logical > ChdDvdLimits.MaximumLogicalBytes || logical % 2048 != 0 ||
            !Number(facts, "volumeBlocks", out long volume) || volume is < 18 or > uint.MaxValue || volume * 2048 > logical ||
            !Number(facts, "primaryDescriptorBlock", out long primary) || primary is < 16 or >= 80 || primary + 1 >= volume ||
            !Number(facts, "rootDirectoryBlock", out long root) || root <= primary + 1 || root > uint.MaxValue ||
            !Number(facts, "rootDirectoryBytes", out long rootBytes) || rootBytes is <= 0 or > (256 << 10) ||
            !Number(facts, "systemCnfBlock", out long cnf) || cnf <= primary + 1 || cnf > uint.MaxValue ||
            !Number(facts, "systemCnfBytes", out long cnfBytes) || cnfBytes is <= 0 or > (16 << 10) ||
            !Number(facts, "bootExecutableBlock", out long boot) || boot <= primary + 1 || boot > uint.MaxValue ||
            !Number(facts, "bootExecutableBytes", out long bootBytes) || bootBytes is < 84 or > uint.MaxValue ||
            root * 2048 + rootBytes > volume * 2048 || cnf * 2048 + cnfBytes > volume * 2048 ||
            boot * 2048 + bootBytes > volume * 2048 ||
            Overlaps(root, rootBytes, cnf, cnfBytes) || Overlaps(root, rootBytes, boot, bootBytes) || Overlaps(cnf, cnfBytes, boot, bootBytes) ||
            !Number(facts, "entryPoint", out long entry) || entry is <= 0 or > uint.MaxValue || entry % 4 != 0 ||
            !Number(facts, "programHeaderCount", out long headers) || headers is < 1 or > 128 || 52 + headers * 32 > bootBytes)
            return false;
        string? serial = Text(facts, "bootSerial"), path = Text(facts, "bootPath"), observed = Text(facts, "observedBoot2Value");
        if (serial is null || serial.Length != 11 || serial[4] != '_' || serial[8] != '.' ||
            serial.AsSpan(0, 4).ContainsAnyExceptInRange('A', 'Z') ||
            serial.AsSpan(5, 3).ContainsAnyExceptInRange('0', '9') || serial.AsSpan(9, 2).ContainsAnyExceptInRange('0', '9') ||
            path != "cdrom0:\\" + serial + ";1" || !string.Equals(observed, path, StringComparison.OrdinalIgnoreCase))
            return false;
        inspection = new(status, Ps2DvdIdentifier.ProfileVersion, new(logical, (uint)volume, (uint)primary,
            (uint)root, (uint)rootBytes, (uint)cnf, (uint)cnfBytes, (uint)boot, (uint)bootBytes,
            observed!, path, serial, (uint)entry, (ushort)headers));
        return true;

        static bool Overlaps(long a, long aBytes, long b, long bBytes) => a * 2048 < b * 2048 + bBytes && b * 2048 < a * 2048 + aBytes;
    }

    private static bool TryReadRvzParameters(JsonElement value, out RvzDescriptor? parameters)
    {
        parameters = null;
        if (!HasFields(value, "version", "discType", "compression", "compressionLevel", "blockBytes", "hasWiiPartitions") ||
            !Number(value, "version", out long version) || version is < 0x30000 or > uint.MaxValue ||
            !Number(value, "discType", out long discType) || discType is < 0 or > uint.MaxValue ||
            !Number(value, "compression", out long compression) || compression is < 0 or > uint.MaxValue ||
            !Number(value, "compressionLevel", out long level) || level is < int.MinValue or > int.MaxValue ||
            !Number(value, "blockBytes", out long block) || block is < 32768 or > 8388608 ||
            value.GetProperty("hasWiiPartitions").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        var captured = new RvzDescriptor((uint)version, (uint)discType, (uint)compression, (int)level, (int)block, value.GetProperty("hasWiiPartitions").GetBoolean());
        if (!RvzInspectionProfile.IsValid(RvzInspectionProfile.Capture(captured)))
            return false;
        parameters = captured;
        return true;
    }

    private static bool TryReadChdInspection(JsonElement value, out ChdInspection? inspection)
    {
        inspection = null;
        if (!HasFields(value, "version", "header", "storedRawSha1", "computedRawSha1", "storedOverallSha1", "computedOverallSha1", "dvdMetadata") ||
            !Number(value, "version", out long version) || version != 5 ||
            !IdentityHash(Text(value, "storedRawSha1"), 40) || !IdentityHash(Text(value, "computedRawSha1"), 40) ||
            !IdentityHash(Text(value, "storedOverallSha1"), 40) || !IdentityHash(Text(value, "computedOverallSha1"), 40) ||
            Text(value, "storedRawSha1") != Text(value, "computedRawSha1") ||
            Text(value, "storedOverallSha1") != Text(value, "computedOverallSha1") ||
            !TryReadDvdMetadata(value.GetProperty("dvdMetadata"), out var metadata))
            return false;
        var header = value.GetProperty("header");
        if (!HasFields(header, "artifactBytes", "logicalBytes", "hunkBytes", "unitBytes", "hunkCount", "codec0", "codec1", "codec2", "codec3") ||
            !Number(header, "artifactBytes", out long artifact) || artifact is < 124 or > ChdDvdLimits.MaximumArtifactBytes ||
            !Number(header, "logicalBytes", out long logical) || logical is <= 0 or > ChdDvdLimits.MaximumLogicalBytes || logical % 2048 != 0 ||
            !Number(header, "hunkBytes", out long hunk) || hunk is <= 0 or > ChdDvdLimits.MaximumHunkBytes || hunk % 2048 != 0 ||
            !Number(header, "unitBytes", out long unit) || unit != 2048 ||
            !Number(header, "hunkCount", out long count) || count is <= 0 or > ChdDvdLimits.MaximumHunkCount || count != logical / hunk + (logical % hunk == 0 ? 0 : 1) ||
            !Number(header, "codec0", out long codec0) || !DvdCodec(codec0) ||
            !Number(header, "codec1", out long codec1) || !DvdCodec(codec1) ||
            !Number(header, "codec2", out long codec2) || !DvdCodec(codec2) ||
            !Number(header, "codec3", out long codec3) || !DvdCodec(codec3))
            return false;
        inspection = new(5, new(artifact, (ulong)logical, (uint)hunk, 2048, (uint)count,
            (uint)codec0, (uint)codec1, (uint)codec2, (uint)codec3), Text(value, "storedRawSha1")!,
            Text(value, "computedRawSha1")!, Text(value, "storedOverallSha1")!, Text(value, "computedOverallSha1")!, metadata);
        return true;
    }

    private static bool DvdCodec(long value) => value is >= 0 and <= uint.MaxValue && ChdDvdLimits.IsDvdCodec((uint)value);

    private static bool TryReadChdRejection(JsonElement value, MediaWorkerStatus status, ChdNativeIdentity? native,
        out ChdRejection? rejection)
    {
        rejection = null;
        if (!HasFields(value, "stage", "cause", "path", "codecSlot", "codec", "nativeError") ||
            !Label(Text(value, "path"), 1024) || !Path.IsPathFullyQualified(Text(value, "path")!) ||
            !OptionalNumber(value, "codecSlot", 0, 3, out long? slot) ||
            !OptionalNumber(value, "codec", 0, uint.MaxValue, out long? codec) ||
            !OptionalNumber(value, "nativeError", 1, 27, out long? error))
            return false;
        ChdRejectionStage stage;
        switch (Text(value, "stage"))
        {
            case "header":
                stage = ChdRejectionStage.Header;
                break;
            case "nativeverification":
                stage = ChdRejectionStage.NativeVerification;
                break;
            case "nativeload":
                stage = ChdRejectionStage.NativeLoad;
                break;
            case "nativeopen":
                stage = ChdRejectionStage.NativeOpen;
                break;
            case "decode":
                stage = ChdRejectionStage.Decode;
                break;
            case "integrity":
                stage = ChdRejectionStage.Integrity;
                break;
            case "io":
                stage = ChdRejectionStage.Io;
                break;
            default:
                return false;
        }
        string? cause = Text(value, "cause");
        MediaWorkerStatus? expected = (stage, cause) switch
        {
            (ChdRejectionStage.Header, "UnsupportedVersion" or "ParentReference" or "UnsupportedCodec" or "UnsupportedDvdCodec" or "InvalidDvdGeometry" or "InvalidCdGeometry") => MediaWorkerStatus.Unsupported,
            (ChdRejectionStage.Header, "ArtifactLimitExceeded" or "LogicalLimitExceeded" or "HunkLimitExceeded" or "HunkCountOverflow" or "HunkCountLimitExceeded") => MediaWorkerStatus.LimitExceeded,
            (ChdRejectionStage.Header, "TruncatedHeader" or "InvalidSignature" or "InvalidHeaderLength" or "InvalidArtifactLength" or "ZeroLogicalBytes" or "ZeroHunkBytes" or "ZeroUnitBytes") => MediaWorkerStatus.Malformed,
            (ChdRejectionStage.NativeVerification, "BinaryMissing" or "BinaryNotRegularFile" or "BinarySizeMismatch" or "BinarySha256Mismatch" or "ManifestMissing" or "ManifestInvalid" or "ManifestPinMismatch" or "ManifestIdentityMismatch" or "ResidentBuildMismatch") => MediaWorkerStatus.Malformed,
            (ChdRejectionStage.NativeVerification, "NativeArtifactIo") => MediaWorkerStatus.IoFailure,
            (ChdRejectionStage.NativeVerification, "ProcessRidMismatch") => MediaWorkerStatus.Unsupported,
            (ChdRejectionStage.NativeLoad, "NativeLoadFailure") => MediaWorkerStatus.IoFailure,
            (ChdRejectionStage.NativeOpen, "NativeOpenFailure") => error == 2 ? MediaWorkerStatus.LimitExceeded : MediaWorkerStatus.Malformed,
            (ChdRejectionStage.Decode, "NativeDecodeFailure") => error == 2 ? MediaWorkerStatus.LimitExceeded : MediaWorkerStatus.Malformed,
            (ChdRejectionStage.Decode, "UnexpectedLogicalLength" or "InvalidDvdMetadata") => MediaWorkerStatus.Malformed,
            (ChdRejectionStage.Decode, "UnsupportedCdMetadata" or "UnchecksummedCdMetadata" or "UnsupportedCdSubcode" or "UnsupportedCdTrackType") => MediaWorkerStatus.Unsupported,
            (ChdRejectionStage.Decode, "CdMetadataLimitExceeded") => MediaWorkerStatus.LimitExceeded,
            (ChdRejectionStage.Integrity, "MissingRawSha1" or "MissingOverallSha1" or "RawSha1Mismatch" or "OverallSha1Mismatch" or "DecodedHunkMismatch" or "DecodedMetadataMismatch") => MediaWorkerStatus.Malformed,
            (ChdRejectionStage.Io, "SourceIo" or "SourceChanged") => MediaWorkerStatus.IoFailure,
            _ => null
        };
        if (expected is null || expected != status ||
            (stage is ChdRejectionStage.Header or ChdRejectionStage.NativeVerification or ChdRejectionStage.NativeLoad) && native is not null ||
            (stage is ChdRejectionStage.NativeOpen or ChdRejectionStage.Decode or ChdRejectionStage.Integrity) && native is null)
            return false;
        bool codecCause = stage == ChdRejectionStage.Header && (cause is "UnsupportedCodec" or "UnsupportedDvdCodec");
        bool nativeCause = cause is "NativeOpenFailure" or "NativeDecodeFailure";
        if (codecCause ? slot is null || codec is null : slot is not null || codec is not null)
            return false;
        if (nativeCause != (error is not null))
            return false;
        rejection = new(stage, cause!, Text(value, "path")!, (int?)slot, (uint?)codec, (int?)error);
        return true;
    }

    private static bool OptionalNumber(JsonElement value, string field, long minimum, long maximum, out long? result)
    {
        result = null;
        if (value.GetProperty(field).ValueKind == JsonValueKind.Null)
            return true;
        if (!Number(value, field, out long number) || number < minimum || number > maximum)
            return false;
        result = number;
        return true;
    }

    private static bool HasFields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!fields.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                return false;
        return seen.Count == fields.Length;
    }
    private static string? Text(JsonElement value, string field) => value.GetProperty(field).ValueKind == JsonValueKind.String
        ? value.GetProperty(field).GetString() : null;
    private static bool Number(JsonElement value, string field, out long number)
    {
        number = 0;
        return value.GetProperty(field).ValueKind == JsonValueKind.Number && value.GetProperty(field).TryGetInt64(out number);
    }
    private static bool Label(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
    private static bool Hash(string? value, int length) => value?.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IdentityHash(string? value, int length) => Hash(value, length) && value!.Any(c => c != '0');
    private static bool TryStatus(string? value, out MediaWorkerStatus status)
    {
        status = value switch
        {
            "ready" => MediaWorkerStatus.Ready,
            "inspected" => MediaWorkerStatus.Inspected,
            "encoded" => MediaWorkerStatus.Encoded,
            "unsupported" => MediaWorkerStatus.Unsupported,
            "malformed" => MediaWorkerStatus.Malformed,
            "limit" => MediaWorkerStatus.LimitExceeded,
            "io" => MediaWorkerStatus.IoFailure,
            _ => (MediaWorkerStatus)(-1)
        };
        return Enum.IsDefined(status);
    }
    private static string StatusText(MediaWorkerStatus status) => status switch
    {
        MediaWorkerStatus.Ready => "ready",
        MediaWorkerStatus.Inspected => "inspected",
        MediaWorkerStatus.Encoded => "encoded",
        MediaWorkerStatus.Unsupported => "unsupported",
        MediaWorkerStatus.Malformed => "malformed",
        MediaWorkerStatus.LimitExceeded => "limit",
        MediaWorkerStatus.IoFailure => "io",
        _ => "invalid"
    };
}
