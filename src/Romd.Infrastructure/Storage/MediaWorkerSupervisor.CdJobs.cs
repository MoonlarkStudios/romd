using System.Security.Cryptography;
using Romd.Admin.Application.Storage.Files;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;

namespace Romd.Infrastructure.Storage;

public sealed partial class MediaWorkerSupervisor
{
    public async Task<MediaWorkerResult> EncodeChdCdAsync(ITempFile request, IReadOnlyList<ITempFile> originalsAndToolCue,
        IWritableTempFile output, ChdmanCandidate candidate, CancellationToken ct = default)
    {
        var context = await ReadCdContextAsync(request, originalsAndToolCue, false, ct);
        return await RunAsync("encode-chd-cd", request, ct, output, toolCandidate: candidate, cdJob: context);
    }
    public async Task<MediaWorkerResult> VerifyChdCdAsync(ITempFile artifact, ITempFile request,
        IReadOnlyList<ITempFile> originalsAndToolCue, CancellationToken ct = default)
    {
        if (_chdCandidate is null)
            return MediaWorkerResult.Failed(MediaWorkerFailure.Startup);
        var context = await ReadCdContextAsync(request, originalsAndToolCue, true, ct);
        return await RunAsync("verify-chd-cd", artifact, ct, cdJob: context);
    }

    public async Task<MediaWorkerResult> ReverifyChdCdAsync(ITempFile artifact, ITempFile request,
        IReadOnlyList<ITempFile> storedFiles, CancellationToken ct = default)
    {
        if (_chdCandidate is null)
            return MediaWorkerResult.Failed(MediaWorkerFailure.Startup);
        var spec = ChdCdJson.Read<ChdCdRetainedRequest>(await ReadAsync(request, ChdCdRequest.MaximumBytes, ct), ChdCdRequest.MaximumBytes);
        spec.Validate();
        if (storedFiles.Count != spec.StoredFiles.Count || storedFiles.Select(f => f.Path).Distinct(StringComparer.Ordinal).Count() != storedFiles.Count ||
            spec.StoredFiles.Any(f => !storedFiles.Any(staged => staged.Path == f.Path && staged.FileSize == f.Bytes && staged.FileSha256.ToString() == f.Sha256)) ||
            artifact.FileSha256.ToString() != spec.ArtifactSha256 || artifact.FileSize != spec.ArtifactBytes)
            throw new InvalidDataException("Retained CD dependencies require exact staged custody.");
        return await RunAsync("reverify-chd-cd", artifact, ct, cdJob: new(request, null, null, [.. storedFiles, request], spec));
    }

    internal sealed record CdJobContext(ITempFile Request, ChdCdRequest? Specification, ChdCdCue? Cue, IReadOnlyList<ITempFile> AdditionalSources, ChdCdRetainedRequest? Retained = null);

    private static async Task<CdJobContext> ReadCdContextAsync(ITempFile request, IReadOnlyList<ITempFile> sources,
        bool verification, CancellationToken ct)
    {
        if (request.FileSize is < 1 or > ChdCdRequest.MaximumBytes || sources.Count is < 3 or > 117)
            throw new InvalidDataException("CD request/source count exceeds its budget.");
        byte[] json = await ReadAsync(request, ChdCdRequest.MaximumBytes, ct);
        var spec = ChdCdJson.Read<ChdCdRequest>(json, ChdCdRequest.MaximumBytes);
        spec.Validate();
        var paths = sources.ToDictionary(f => f.Path, StringComparer.Ordinal);
        if (paths.Count != spec.Files.Count + 1 || !paths.TryGetValue(spec.ToolCuePath, out var toolCue) || toolCue.FileSize > ChdCdRequest.MaximumBytes ||
            spec.Files.Any(f => !paths.TryGetValue(f.Path, out var input) || input.FileSize != f.Bytes || input.FileSha256.ToString() != f.Sha256))
            throw new InvalidDataException("Every CD dependency must have exact staged custody.");
        var cue = ChdCdCue.Parse(await ReadAsync(paths[spec.Files.Single(f => f.Role == "cue").Path], ChdCdCue.MaximumBytes, ct));
        byte[] rendered = cue.RenderToolCue(spec.Files.Where(f => f.Role == "bin").ToDictionary(f => f.Name, f => f.Path, StringComparer.Ordinal), spec.ToolCuePath);
        byte[] stagedCue = await ReadAsync(toolCue, ChdCdRequest.MaximumBytes, ct);
        if (!rendered.AsSpan().SequenceEqual(stagedCue))
            throw new InvalidDataException("Tool descriptor must reference only the staged raw BINs.");
        return new(request, spec, cue, verification ? [.. sources, request] : sources.ToArray());
    }

    private static async Task<byte[]> ReadAsync(ITempFile file, int maximum, CancellationToken ct)
    {
        if (file.FileSize is < 1 || file.FileSize > maximum)
            throw new InvalidDataException("CD input exceeds its budget.");
        byte[] bytes = new byte[checked((int)file.FileSize)];
        await using var stream = file.OpenRead();
        await stream.ReadExactlyAsync(bytes, ct);
        if (await stream.ReadAsync(new byte[1], ct) != 0 || Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.FileSha256.ToString())
            throw new InvalidDataException("CD input changed identity.");
        return bytes;
    }

    private MediaWorkerResult ValidateCdJobResult(string operation, MediaWorkerReply reply, CdJobContext job,
        ITempFile source, IWritableTempFile? output, ChdmanCandidate? candidate, int processId, int cpuSeconds)
    {
        MediaWorkerResult Bad() => MediaWorkerResult.Failed(MediaWorkerFailure.Protocol);
        if (reply.CdFailure is { } rejected)
        {
            if (reply.Status is MediaWorkerStatus.Inspected or MediaWorkerStatus.Encoded || reply.CdEncoding is not null || reply.CdVerification is not null ||
                reply.ToolIdentity is { } tool && (operation != "encode-chd-cd" || candidate is null || !candidate.Matches(tool)))
                return Bad();
            if (rejected.ToolCause == ChdDvdEncodingCause.CpuBudgetExceeded)
                return reply.ToolExecution is { } measured && measured.CpuMicroseconds >= cpuSeconds * 1_000_000L - 20_000
                    ? new(reply, MediaWorkerFailure.CpuDeadline) : Bad();
            return new(reply, reply.Status switch
            {
                MediaWorkerStatus.Unsupported => MediaWorkerFailure.Unsupported,
                MediaWorkerStatus.Malformed => MediaWorkerFailure.Malformed,
                MediaWorkerStatus.LimitExceeded => MediaWorkerFailure.LimitExceeded,
                _ => MediaWorkerFailure.IoFailure
            });
        }
        if (operation == "reverify-chd-cd")
        {
            var expected = job.Retained;
            if (expected is null || reply.Status != MediaWorkerStatus.Inspected || reply.CdVerification is not { } retained ||
                retained.Profile != ChdCdRetainedRequest.CurrentProfile || retained.RequestSha256 != job.Request.FileSha256.ToString() ||
                reply.CdInspection is not { } cd || retained.ObservedBoot != cd.ObservedBoot || !expected.Matches(cd) ||
                reply.NativeIdentity is null || !_chdCandidate!.Matches(reply.NativeIdentity) ||
                !expected.MatchesVerification(retained))
                return Bad();
            return new(reply, null);
        }
        if (operation == "encode-chd-cd")
        {
            if (reply.Status != MediaWorkerStatus.Encoded || reply.CdEncoding is not { } encoded || reply.CdVerification is not null ||
                job.Specification!.RequiredBootProfile != encoded.ObservedBoot.Profile ||
                candidate is null || reply.ToolIdentity is null || !candidate.Matches(reply.ToolIdentity) ||
                encoded.RequestSha256 != job.Request.FileSha256.ToString() || encoded.RequestBytes != job.Request.FileSize ||
                output is null || encoded.Artifact.Bytes > output.MaximumBytes || encoded.Header.LogicalBytes > (ulong)_options.MaxLogicalBytes ||
                reply.ToolExecution is not { } execution || execution.ProcessId == processId || execution.ExitCode != 0)
                return Bad();
        }
        else
        {
            if (reply.Status != MediaWorkerStatus.Inspected || reply.CdVerification is not { } verification || reply.CdInspection is not { } inspection ||
                job.Specification!.RequiredBootProfile != verification.ObservedBoot.Profile ||
                reply.NativeIdentity is null || !_chdCandidate!.Matches(reply.NativeIdentity) || reply.CdEncoding is not null ||
                inspection.ArtifactSha256 != source.FileSha256.ToString() || inspection.Header.ArtifactBytes != source.FileSize ||
                inspection.Header.LogicalBytes > (ulong)_options.MaxLogicalBytes || verification.RequestSha256 != job.Request.FileSha256.ToString() ||
                verification.Members.Count != job.Specification!.Files.Count || !inspection.Tracks.Select(t => t.Type).SequenceEqual(job.Cue!.TrackTypes))
                return Bad();
            for (int i = 0; i < verification.Members.Count; i++)
            {
                var actual = verification.Members[i];
                var expected = job.Specification!.Files[i];
                if (actual.Name != expected.Name || actual.Role != expected.Role || actual.Original.Sha256 != expected.Sha256 || actual.Original.Bytes != expected.Bytes ||
                    expected.Role == "bin" && !actual.Tracks.SequenceEqual(job.Cue!.Files.Single(f => f.Name == expected.Name).Tracks))
                    return Bad();
            }
        }
        return new(reply, null);
    }
}
