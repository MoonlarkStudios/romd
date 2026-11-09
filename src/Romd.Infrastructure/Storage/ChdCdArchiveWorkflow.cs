using System.Text.Json;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;

namespace Romd.Infrastructure.Storage;

/// <summary>An affirmatively stopped child failure retains its operational classification across the CD workflow.</summary>
internal sealed class RomCdWorkerFailureException(MediaWorkerFailure failure, MediaWorkerResult? result = null)
    : Exception($"Supervised CD processing failed: {failure}; stopped={result?.ProcessTreeStopped}; " +
        $"execution={result?.Execution}; reply={result?.Reply?.Status}; cd={result?.Reply?.CdFailure}; " +
        $"dvd={result?.Reply?.ChdEncodingRejection?.Stage}/{result?.Reply?.ChdEncodingRejection?.Cause}; tool={result?.Reply?.ToolExecution}.")
{
    public MediaWorkerFailure Failure { get; } = failure;
}

/// <summary>Independent original comparison and separate raw boot observation from the actual stopped inspection.</summary>
public sealed record RomCdArchiveComparison(RomMeasuredRepresentation Measured, string InspectorBuild, RomCdBoot? Boot);

/// <summary>Borrowed originals plus two owned immutable tool inputs. Dispose after all supervised operations finish.</summary>
public sealed class PreparedCdSources : IAsyncDisposable
{
    public ITempFile Request { get; }
    public IReadOnlyList<ITempFile> Inputs { get; }
    public ChdCdRequest Specification { get; }
    internal ChdCdCue Cue { get; }
    private readonly ITempFile _toolCue;
    internal PreparedCdSources(ITempFile request, ITempFile toolCue, IReadOnlyList<ITempFile> originals, ChdCdRequest spec, ChdCdCue cue)
    { Request = request; _toolCue = toolCue; Inputs = Array.AsReadOnly(originals.Append(toolCue).ToArray()); Specification = spec; Cue = cue; }
    internal void Quarantine()
    { Request.Quarantine(); foreach (var input in Inputs) input.Quarantine(); }
    public async ValueTask DisposeAsync()
    { try { await Request.DisposeAsync(); } finally { await _toolCue.DisposeAsync(); } }
}

/// <summary>Owned encoded artifact, independent archive proof and separate raw encoding/verification receipts.</summary>
public sealed record EncodedCdArchive(ITempFile Artifact, TempFileArtifactMeasurement ArtifactMeasurement, RomMeasuredRepresentation Measured, MediaWorkerResult EncodingRun,
    MediaWorkerResult VerificationRun) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Artifact.DisposeAsync();
}

/// <summary>Stage a safe descriptor, encode in one child and verify every original file in a fresh child.
/// Production admission, process capacity, source ownership and fenced publication belong to the caller.</summary>
public sealed class ChdCdArchiveWorkflow(ITempFileFactory temps, IMediaWorkerSupervisor supervisor, TimeProvider clock)
{
    private int _running, _stopped = 1;
    public bool ProcessTreeStopped => Volatile.Read(ref _stopped) == 1;
    private void BeginOperation()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("A CD workflow cannot run overlapping supervised operations.");
        if (!ProcessTreeStopped)
        {
            Volatile.Write(ref _running, 0);
            throw new ResourceCapacityException("A previous CD operation has unresolved process custody.");
        }
    }
    private async Task<MediaWorkerResult> RunAsync(Func<Task<MediaWorkerResult>> run)
    {
        Volatile.Write(ref _stopped, 0);
        try
        {
            var result = await run();
            Volatile.Write(ref _stopped, result.ProcessTreeStopped ? 1 : 0);
            return result;
        }
        catch (MediaWorkerStoppedCancellationException) { Volatile.Write(ref _stopped, 1); throw; }
    }
    public async Task<PreparedCdSources> PrepareAsync(string cueName, ITempFile cue,
        IReadOnlyDictionary<string, ITempFile> bins, IReadOnlyDictionary<string, ITempFile>? sidecars = null, CancellationToken ct = default, string? requiredBootProfile = null)
    {
        if (!ChdCdCue.PortableName(cueName) || cue.FileSize is < 1 or > ChdCdCue.MaximumBytes)
            throw new InvalidDataException("An original bounded CUE is required.");
        byte[] originalCue = new byte[checked((int)cue.FileSize)];
        await using (var stream = cue.OpenRead())
        {
            await stream.ReadExactlyAsync(originalCue, ct);
            if (await stream.ReadAsync(new byte[1], ct) != 0 || Sha256.FromSpan(System.Security.Cryptography.SHA256.HashData(originalCue)) != cue.FileSha256)
                throw new InvalidDataException("Original CUE changed identity.");
        }
        var parsed = ChdCdCue.Parse(originalCue);
        var binPaths = bins.ToDictionary(pair => pair.Key, pair => pair.Value.Path, StringComparer.Ordinal);
        _ = parsed.RenderToolCue(binPaths, cue.Path); // Validate before reserving any generated output.
        var reservedCue = await temps.ReserveCdCueAsync(ChdCdRequest.MaximumBytes, ct);
        if (reservedCue.IsError)
            throw new InvalidDataException(reservedCue.FirstError.Description);
        await using var toolOutput = reservedCue.Value;
        byte[] rendered = parsed.RenderToolCue(binPaths, toolOutput.Path);
        ITempFile? toolCue = null, request = null;
        try
        {
            await using (var stream = new FileStream(toolOutput.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            { await stream.WriteAsync(rendered, ct); await stream.FlushAsync(ct); }
            var sealedCue = await toolOutput.SealAsync(new(Sha256.FromSpan(System.Security.Cryptography.SHA256.HashData(rendered)),
                Md5.FromSpan(System.Security.Cryptography.MD5.HashData(rendered)), Sha1.FromSpan(System.Security.Cryptography.SHA1.HashData(rendered)), rendered.Length), ct);
            if (sealedCue.IsError)
                throw new InvalidDataException(sealedCue.FirstError.Description);
            toolCue = sealedCue.Value;
            var originals = parsed.Files.Select(f => (Name: f.Name, File: bins[f.Name], Role: "bin"))
                .Append((cueName, cue, "cue")).Concat((sidecars ?? new Dictionary<string, ITempFile>()).Select(pair => (pair.Key, pair.Value, "sidecar"))).ToArray();
            var spec = new ChdCdRequest(ChdCdRequest.CurrentProfile, toolCue.Path,
                originals.Select(f => new ChdCdSourceFile(f.Item1, f.Item2.Path, f.Item2.FileSize, f.Item2.FileSha256.ToString(), f.Item3)).ToArray(), requiredBootProfile);
            spec.Validate();
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(spec, ChdCdJson.Options);
            if (json.Length > ChdCdRequest.MaximumBytes)
                throw new InvalidDataException("CD request exceeds its budget.");
            request = await temps.CreateAsync(new MemoryStream(json, writable: false), ct);
            return new(request, toolCue, originals.Select(f => f.Item2).ToArray(), spec, parsed);
        }
        catch
        {
            try
            { if (request is not null) await request.DisposeAsync(); }
            finally { if (toolCue is not null) await toolCue.DisposeAsync(); }
            throw;
        }
    }

    public async Task<EncodedCdArchive> EncodeAsync(PreparedCdSources sources, ChdmanCandidate tool, long maximumOutputBytes, CancellationToken ct = default, Func<CancellationToken, Task>? beforeVerification = null)
    {
        BeginOperation();
        try
        {
            var reserved = await temps.ReserveOutputAsync(maximumOutputBytes, ct);
            if (reserved.IsError)
                throw new InvalidDataException(reserved.FirstError.Description);
            await using var output = reserved.Value;
            ITempFile? artifact = null;
            try
            {
                var encoded = await RunAsync(() => supervisor.EncodeChdCdAsync(sources.Request, sources.Inputs, output, tool, ct));
                if (!encoded.ProcessTreeStopped)
                { sources.Quarantine(); output.Quarantine(); }
                if (encoded.ProcessTreeStopped && !encoded.IsSuccess)
                    throw new RomCdWorkerFailureException(encoded.Failure ?? MediaWorkerFailure.Protocol, encoded);
                if (!encoded.ProcessTreeStopped || !encoded.IsSuccess || encoded.Reply?.CdEncoding is not { } metadata || encoded.Reply.ToolIdentity is null ||
                    !tool.Matches(encoded.Reply.ToolIdentity) || metadata.RequestSha256 != sources.Request.FileSha256.ToString())
                    throw new InvalidDataException($"Supervised CD encoding failed: {encoded.Failure}; {encoded.Reply?.CdFailure}; {encoded.Execution}");
                var measured = metadata.Artifact;
                var artifactMeasurement = new TempFileArtifactMeasurement(Sha256.Parse(measured.Sha256), Md5.Parse(measured.Md5), Sha1.Parse(measured.Sha1), measured.Bytes);
                var sealedResult = await output.SealAsync(artifactMeasurement, ct);
                if (sealedResult.IsError)
                    throw new InvalidDataException(sealedResult.FirstError.Description);
                artifact = sealedResult.Value;
                if (beforeVerification is not null)
                    await beforeVerification(ct);
                var (measuredRepresentation, verified) = await VerifyCoreAsync(artifact, sources, ct);
                if (verified.Reply?.CdVerification?.ObservedBoot != metadata.ObservedBoot || verified.Reply?.CdInspection?.Header != metadata.Header || !ChdCdEncodingRecipe.Matches(verified.Reply.CdInspection.Header))
                    throw new InvalidDataException("Independent CD verification disagrees with the encoded recipe observation.");
                return new(artifact, artifactMeasurement, measuredRepresentation, encoded, verified);
            }
            catch
            {
                // The workflow owns output disposal. Persist every hold before unwinding its writable reservation on an uncertified throw.
                if (!ProcessTreeStopped)
                {
                    try
                    { sources.Quarantine(); }
                    catch (Exception) { }
                    try
                    { output.Quarantine(); }
                    catch (Exception) { }
                    try
                    { artifact?.Quarantine(); }
                    catch (Exception) { }
                }
                if (artifact is not null)
                    await artifact.DisposeAsync();
                throw;
            }
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    public async Task<RomCdArchiveComparison> VerifyExistingAsync(ITempFile artifact, PreparedCdSources sources, CancellationToken ct = default)
    {
        BeginOperation();
        try
        {
            var result = await VerifyCoreAsync(artifact, sources, ct);
            var reply = result.Run.Reply!;
            return new(result.Measured, reply.Identity.DecoderBuild, RomCdMeasurementMapper.MapBoot(reply.CdInspection!.ObservedBoot));
        }
        catch
        {
            if (!ProcessTreeStopped)
            {
                try
                { sources.Quarantine(); }
                catch (Exception) { }
                try
                { artifact.Quarantine(); }
                catch (Exception) { }
            }
            throw;
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    private async Task<(RomMeasuredRepresentation Measured, MediaWorkerResult Run)> VerifyCoreAsync(ITempFile artifact, PreparedCdSources sources, CancellationToken ct)
    {
        var result = await RunAsync(() => supervisor.VerifyChdCdAsync(artifact, sources.Request, sources.Inputs, ct));
        if (!result.ProcessTreeStopped)
        { artifact.Quarantine(); sources.Quarantine(); }
        if (result.ProcessTreeStopped && !result.IsSuccess)
            throw new RomCdWorkerFailureException(result.Failure ?? MediaWorkerFailure.Protocol, result);
        if (!result.ProcessTreeStopped || !result.IsSuccess || result.Reply is not { CdVerification: { } proof, CdInspection: { } inspection, NativeIdentity: { } native } reply ||
            proof.RequestSha256 != sources.Request.FileSha256.ToString() || inspection.ArtifactSha256 != artifact.FileSha256.ToString() ||
            inspection.Header.ArtifactBytes != artifact.FileSize || proof.Members.Count != sources.Specification.Files.Count)
            throw new InvalidDataException("Supervised whole-file CD verification failed: " + result.Failure);
        for (int i = 0; i < proof.Members.Count; i++)
        {
            var measured = proof.Members[i];
            var expected = sources.Specification.Files[i];
            if (measured.Name != expected.Name || measured.Role != expected.Role || measured.Original.Bytes != expected.Bytes || measured.Original.Sha256 != expected.Sha256)
                throw new InvalidDataException("Whole-file verification does not bind every exact staged original.");
            if (measured.Role == "bin")
            {
                if (measured.Reconstructed is null || !measured.Tracks.SequenceEqual(sources.Cue.Files.Single(f => f.Name == measured.Name).Tracks))
                    throw new InvalidDataException("Whole-file verification changed a BIN selector.");
            }
        }
        return (RomCdMeasurementMapper.Map(inspection, proof, native,
            reply.Identity.DecoderBuild + ";worker:" + reply.Identity.WorkerBuild, clock.GetUtcNow()), result);
    }
}
