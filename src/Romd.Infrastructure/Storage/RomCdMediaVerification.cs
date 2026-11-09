using System.Text.Json;
using ErrorOr;
using Moonlark.Libchdr;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Media.PlayStation;

namespace Romd.Infrastructure.Storage;

/// <summary>Exact retained CD verification; every original member is independently hashed without encoding or publication.</summary>
public sealed class RomCdMediaVerification(LocalTempFileFactory staging,
    IFileStorageService files, IMediaWorkerSupervisor supervisor, MediaProcessingRuntime runtime, TimeProvider clock)
{
    internal async Task<ErrorOr<IRomMediaVerificationLease>> AcquireAsync(Guid jobId, RomMediaVerificationSource selected,
        RomRepresentationFacts facts, CancellationToken ct)
    {
        if (facts.Manifest is not RomReconstructionManifest.CdTracks manifest || facts.Container is not RomContainerFacts.Chd)
            return RomMediaVerificationErrors.Mismatch;
        var stored = facts.Descriptors.ToDictionary(descriptor => manifest.Members[descriptor.Ordinal].Path, descriptor => descriptor.FileId, StringComparer.Ordinal);
        var processing = await runtime.AcquireAsync(jobId, "media-verification-cd", ct);
        try
        {
            var aggregate = await staging.ReserveRetainedCdAsync(jobId, [facts.ArtifactSize, .. manifest.Members.OfType<RomCdStoredMember>().Select(m => m.Identity.Size)], ct);
            try
            {
                processing.TrackAggregate(aggregate);
                return new Lease(jobId, selected, facts, stored, aggregate, processing, runtime, files, supervisor, clock);
            }
            catch { await aggregate.DisposeAsync(); throw; }
        }
        catch { processing.Dispose(); throw; }
    }
    private sealed class Lease(Guid jobId, RomMediaVerificationSource selected, RomRepresentationFacts facts,
        IReadOnlyDictionary<string, int> stored, LocalTempFileFactory.RetainedCdStaging staging,
        MediaProcessingRuntime.MediaProcessingReservation processing, MediaProcessingRuntime runtime,
        IFileStorageService files, IMediaWorkerSupervisor supervisor, TimeProvider clock) : IRomMediaVerificationLease
    {
        private RomReconstructionManifest.CdTracks Manifest => (RomReconstructionManifest.CdTracks)facts.Manifest;
        private readonly List<ITempFile> _inputs = [];
        private bool _unknownCustody, _released;
        private int _used;
        public bool ProcessTreeStopped => !_unknownCustody && processing.ProcessesStopped;
        public async Task<ErrorOr<RomMediaVerificationMeasurement>> VerifyAsync(CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _used, 1) != 0)
                throw new InvalidOperationException("Retained CD verification leases are single-use.");
            using var active = processing.Activate();
            bool stopped = true;
            try
            {
                await StageAsync(selected.FileId, facts.ArtifactSha256, 0);
                var members = Manifest.Members.OfType<RomCdStoredMember>().ToArray();
                for (int i = 0; i < members.Length; i++)
                    await StageAsync(stored[members[i].Path], members[i].Identity.Sha256, i + 1);
                var spec = Specification(facts, members.Select((m, i) => new ChdCdSourceFile(m.Path, _inputs[i + 1].Path, m.Identity.Size,
                    m.Identity.Sha256.ToString(), m.Role == RomCdStoredRole.Cue ? "cue" : "sidecar")).ToArray());
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(spec, ChdCdJson.Options);
                await using var requestStream = new MemoryStream(json, writable: false);
                var request = await staging.MaterializeAsync(_inputs.Count, requestStream, null, ct);
                _inputs.Add(request);
                ct.ThrowIfCancellationRequested();
                stopped = false;
                var result = await supervisor.ReverifyChdCdAsync(_inputs[0], request, _inputs.Skip(1).Take(members.Length).ToArray(), ct);
                stopped = result.ProcessTreeStopped;
                if (!stopped)
                    return RomMediaVerificationErrors.Custody;
                if (!result.IsSuccess || result.Reply is not { CdInspection: { } inspection, NativeIdentity: { } native, CdVerification: { } proof } ||
                    proof.Profile != ChdCdRetainedRequest.CurrentProfile || proof.RequestSha256 != request.FileSha256.ToString() ||
                    !spec.MatchesVerification(proof) || !spec.Matches(inspection) || proof.ObservedBoot != inspection.ObservedBoot)
                    return MediaInspectionErrors.Failed(result.Failure ?? MediaWorkerFailure.Protocol);
                var measured = RomCdMeasurementMapper.Map(inspection, proof, native, result.Reply.Identity.DecoderBuild + ";worker:" + result.Reply.Identity.WorkerBuild, clock.GetUtcNow());
                if (!measured.MatchesRetained(facts) || !measured.Measurement.Decoder.Equals(selected.Decoder))
                    return RomMediaVerificationErrors.Mismatch;
                var boot = proof.ObservedBoot;
                var artifact = _inputs[0];
                var hashes = artifact.DatHashes!;
                return new RomMediaVerificationMeasurement.Cd(measured, result.Reply.Identity.DecoderBuild, RomCdMeasurementMapper.MapBoot(boot),
                    new(artifact.FileSha256, hashes.Md5, hashes.Sha1, artifact.FileSize));
            }
            catch (MediaWorkerStoppedCancellationException) { stopped = true; throw; }
            catch (Exception) when (!stopped)
            {
                ct.ThrowIfCancellationRequested();
                return RomMediaVerificationErrors.Custody;
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or FormatException or OverflowException) { return RomMediaVerificationErrors.Mismatch; }
            finally
            {
                if (!stopped)
                {
                    _unknownCustody = true;
                    processing.Retain();
                    foreach (var input in _inputs)
                        try
                        { input.Quarantine(); }
                        catch (Exception) { }
                }
            }
            async Task StageAsync(int fileId, Romd.Domain.Hashing.Sha256 sha256, int ordinal)
            {
                await using var source = await files.RetrieveByIdAsync(fileId, ct) ?? throw new InvalidDataException("Pinned retained CD dependency is unavailable.");
                _inputs.Add(await staging.MaterializeAsync(ordinal, source, sha256, ct));
            }
        }
        public async Task<ErrorOr<Success>> ReleaseProcessingAsync(CancellationToken ct = default)
        {
            if (_released)
                return Result.Success;
            if (!ProcessTreeStopped || (await runtime.EnsureJobStoppedAsync(jobId, ct)).IsError)
                return RomMediaVerificationErrors.Custody;
            processing.Dispose();
            if (!processing.Released)
                return RomMediaVerificationErrors.Custody;
            _released = true;
            return Result.Success;
        }
        public async ValueTask DisposeAsync()
        {
            List<Exception> errors = [];
            try
            {
                foreach (var input in _inputs)
                    try
                    { await input.DisposeAsync(); }
                    catch (Exception e) { errors.Add(e); }
                try
                { await staging.DisposeAsync(); }
                catch (Exception e) { errors.Add(e); }
            }
            finally { if (!ProcessTreeStopped) processing.Retain(); processing.Dispose(); }
            if (errors.Count > 0)
                throw new AggregateException(errors);
        }
    }
    internal static ChdCdRetainedRequest Specification(RomRepresentationFacts facts, IReadOnlyList<ChdCdSourceFile> stored)
    {
        if (facts.Manifest is not RomReconstructionManifest.CdTracks manifest || facts.Container is not RomContainerFacts.Chd container)
            throw new InvalidDataException("Retained CD geometry is required.");
        var boots = facts.CdBootInspections.Select(observation => observation.Boot).Distinct().ToArray();
        if (boots.Length > 1)
            throw new InvalidDataException("Retained positive boot observations disagree.");
        var boot = RomCdMeasurementMapper.BootObservation(boots.SingleOrDefault());
        ChdCdFileHashes Hashes(RomCdFileIdentity identity) => new(identity.Size, identity.Sha256.ToString(), identity.Sha1.ToString(), identity.Md5.ToString(), identity.Crc32.ToString());
        return new(ChdCdRetainedRequest.CurrentProfile, facts.ArtifactSha256.ToString(), facts.ArtifactSize, manifest.LogicalFrames,
            container.HunkBytes, container.Codecs, container.StoredRawSha1.ToString(), container.StoredOverallSha1.ToString(),
            manifest.Tracks.Select(track => new ChdCdRetainedTrack(track.Number, track.FirstFrame, track.Frames, track.Kind switch
            { RomCdTrackKind.Mode1Raw => ChdCdTrackType.Mode1Raw, RomCdTrackKind.Mode2Raw => ChdCdTrackType.Mode2Raw, _ => ChdCdTrackType.Audio })).ToArray(),
            manifest.Members.Select(member => member is RomCdBinMember bin ? new ChdCdMemberVerification(member.Path, "bin", bin.Tracks, Hashes(member.Identity), Hashes(member.Identity)) :
                new(member.Path, ((RomCdStoredMember)member).Role == RomCdStoredRole.Cue ? "cue" : "sidecar", [], Hashes(member.Identity), null)).ToArray(), stored,
            boot.Ps1, boot.Saturn);
    }
}
