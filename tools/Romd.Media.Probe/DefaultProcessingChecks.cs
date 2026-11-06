using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ErrorOr;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Hashing;
using Romd.Infrastructure.Storage;
using Romd.Media.Inspection;
using Romd.Media.Rvz;

namespace Romd.Media.Probe;

/// <summary>Qualifies two live admitted children before permission; it makes no CPU-overlap or throughput claim.</summary>
internal static class DefaultProcessingChecks
{
    internal const string Scope = "Two live admitted production children pre-permit at default processing capacity; tiny GameCube encoding and separate verification, no simultaneous CPU or real-disc throughput claim.";
    internal enum Point { BothIdentified, ThirdDeferred }
    internal sealed record LaneEvidence(string SourcePath, MediaProcessIdentity Identified, Digests Artifact,
        long OutputMaximumBytes, MediaWorkerResult Encode, MediaWorkerResult Verify);
    internal sealed record Evidence(string Scope, MediaProcessingOptions Limits, Digests Original,
        IReadOnlyList<MediaProcessIdentity> IdentifiedChildren, double ThirdAdmissionSeconds,
        IReadOnlyList<LaneEvidence> Lanes, int RecoveredSlots, bool ChildHoldsEnded);

    internal static async Task<Evidence> RunAsync(string recoveryIso, MediaProcessingOptions limits,
        MediaProcessingRuntime runtime, LocalTempFileFactory staging, CancellationToken ct,
        Func<Point, IReadOnlyList<MediaProcessIdentity>, CancellationToken, Task>? observe = null,
        Func<CancellationToken, Task<MediaProcessingRuntime.MediaProcessingReservation>>? acquireThird = null)
    {
        var defaults = new MediaProcessingOptions();
        Check.That(limits.MaxConcurrent == defaults.MaxConcurrent && limits.AdmissionTimeoutSeconds == defaults.AdmissionTimeoutSeconds &&
            limits.WallTimeoutSeconds == defaults.WallTimeoutSeconds && limits.CpuTimeoutSeconds == defaults.CpuTimeoutSeconds &&
            limits.MemoryBytes == defaults.MemoryBytes && limits.LinuxDataBytes == defaults.LinuxDataBytes &&
            limits.LinuxAddressSpaceBytes == defaults.LinuxAddressSpaceBytes && limits.MaxArtifactBytes == defaults.MaxArtifactBytes &&
            limits.MaxLogicalBytes == defaults.MaxLogicalBytes, "processing capacity phase uses unchanged defaults");
        byte[] bytes = await ReadRecoveryAsync(recoveryIso, ct);
        await using var oracle = new MemoryStream(bytes, writable: false);
        var original = await Digests.StreamAsync(oracle, ct, bytes.Length);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var barrier = new IdentifiedBarrier();
        var supervisor = new MediaWorkerSupervisor(limits, () => new MediaWorkerProcessLifetime(),
            setup: barrier.OnSetupAsync, runtime: runtime);
        var captures = new[] { new Capture(supervisor), new Capture(supervisor) };
        var verifiers = captures.Select(capture => new RvzVerifier(limits, capture, runtime)).ToArray();
        var sources = new List<ITempFile>(2);
        var artifacts = new List<ITempFile>(2);
        var pending = new List<Task<ErrorOr<RvzEncodedArtifact>>>(2);
        Exception? primaryFailure = null;
        double thirdSeconds = 0;
        var lanes = new List<LaneEvidence>(2);
        try
        {
            for (int lane = 0; lane < 2; lane++)
            {
                var preparation = ProbeMediaFacts.ManagedPreparation(new HashingService(), verifiers[lane], new ProbeMediaFacts.EmptyReferences());
                IRomPreparationAdmission? admission = null;
                try
                {
                    await using var input = new MemoryStream(bytes, writable: false);
                    var source = await staging.CreateAsync(input, cancellation.Token,
                        magic => preparation.RequiresDatHashes(magic, recoveryIso),
                        async (magic, token) => admission = await preparation.AdmitAsync(magic, recoveryIso, token),
                        magic => RomPreparationDetection.SelectPrefixLength(magic, recoveryIso));
                    sources.Add(source);
                    Check.That(admission is not null, "tiny source inspection admitted before copy");
                    var prepared = await admission!.PrepareAsync(source, recoveryIso, cancellation.Token);
                    Check.That(!prepared.IsError, "tiny source preparation succeeds");
                    await using var measured = (Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile)prepared.Value;
                    Check.That(measured.Eligibility is { Status: RomIsoEligibilityStatus.Supported, DiscKind: RomIsoDiscKind.GameCube },
                        "independent source inspection establishes GameCube kind");
                    MatchLogical(measured.Verification, original);
                }
                finally { admission?.Dispose(); }
            }
            Check.That(sources[0].Path != sources[1].Path, "concurrent children own distinct source custody leases");
            barrier.Enable();
            var encoders = verifiers.Select(verifier => new RvzEncodingService(verifier, staging, limits)).ToArray();
            for (int lane = 0; lane < 2; lane++)
                pending.Add(encoders[lane].EncodeAsync(sources[lane], original.Size, RomIsoDiscKind.GameCube, cancellation.Token));
            await Task.WhenAny(barrier.BothIdentified, pending[0], pending[1])
                .WaitAsync(TimeSpan.FromSeconds(30), cancellation.Token);
            Check.That(barrier.BothIdentified.IsCompletedSuccessfully, "both actual children reach the pre-permit barrier");
            var identified = barrier.Snapshot();
            AssertLive(identified);
            if (observe is not null)
                await observe(Point.BothIdentified, identified, ct);
            ct.ThrowIfCancellationRequested();
            long began = Stopwatch.GetTimestamp();
            bool deferred = false;
            try
            {
                using var unexpected = await (acquireThird is null
                    ? runtime.AcquireAsync(ct: cancellation.Token) : acquireThird(cancellation.Token));
                Check.That(false, "a third processing reservation must defer while both children are held");
            }
            catch (ResourceCapacityException) { deferred = true; }
            thirdSeconds = Stopwatch.GetElapsedTime(began).TotalSeconds;
            Check.That(deferred && thirdSeconds >= limits.AdmissionTimeoutSeconds,
                "third admission defers after at least the configured five-second wait");
            AssertLive(identified);
            if (observe is not null)
                await observe(Point.ThirdDeferred, identified, ct);
            ct.ThrowIfCancellationRequested();
            barrier.Release();
            var encoded = await Task.WhenAll(pending);
            foreach (var result in encoded)
                if (!result.IsError)
                    artifacts.Add(result.Value.Artifact);
            for (int lane = 0; lane < 2; lane++)
            {
                Check.That(!encoded[lane].IsError, "tiny production encoding succeeds; actual=" +
                    (encoded[lane].IsError ? encoded[lane].FirstError.Code : "success"));
                var artifact = encoded[lane].Value;
                var capture = captures[lane];
                var encode = capture.Encoding!;
                Check.That(encode.IsSuccess && encode.Reply is { Encoding: not null, Inspection: null, IsoInspection: null } &&
                    encode.Execution!.ExitCode == 0, "encoded metadata supplies no verification evidence");
                var facts = encode.Reply!.Encoding!;
                Check.That(OperatingSystem.IsWindows() ? facts.PriorityKind == "windows-priority" && facts.PriorityValue == 0x4000
                    : facts.PriorityKind == "unix-nice" && facts.PriorityValue >= 10, "actual encoding priority is reduced");
                var bound = (RvzCanonicalOutputBound.Success)RvzCanonicalBounds.GetGameCubeOutputBound(original.Size, limits.MaxLogicalBytes, ct);
                Check.That(capture.OutputMaximumBytes == bound.MaximumOutputBytes && facts.ArtifactBytes <= capture.OutputMaximumBytes &&
                    artifact.Artifact.FileSize == artifact.Measurement.Size && artifact.Artifact.FileSha256 == artifact.Measurement.Sha256,
                    "full output bound reserved and sealed facts match the artifact");
                await using var raw = artifact.Artifact.OpenRead();
                var hashes = await Digests.StreamAsync(raw, ct, artifact.Artifact.FileSize);
                Check.That(hashes.Size == artifact.Measurement.Size && hashes.Sha256 == artifact.Measurement.Sha256.ToString() &&
                    hashes.Md5 == artifact.Measurement.Md5.ToString() && hashes.Sha1 == artifact.Measurement.Sha1.ToString() &&
                    facts.SourceSha256 == sources[lane].FileSha256.ToString() && facts.SourceBytes == sources[lane].FileSize &&
                    facts.ArtifactSha256 == hashes.Sha256 && facts.ArtifactMd5 == hashes.Md5 && facts.ArtifactSha1 == hashes.Sha1 &&
                    artifact.EncodingProfile == MediaWorkerProtocol.CanonicalEncodingProfile &&
                    artifact.RecipeVersion == RvzCanonicalRecipe.Id && artifact.EncoderBuild == RvzEncoder.EncoderBuild,
                    "all encoded raw facts and recipe/build match delivered bytes");
                AssertStopped(encode);
                var verified = await verifiers[lane].VerifyAsync(artifact.Artifact, ct);
                Check.That(!verified.IsError, "separate child verifies the encoded artifact");
                MatchLogical(verified.Value.Verification, original);
                var verify = capture.Inspection!;
                AssertStopped(verify);
                Check.That(verify.Execution!.ExitCode == 0 && verify.Execution.ProcessId != encode.Execution!.ProcessId &&
                    verified.Value.Verification.ArtifactSha256 == artifact.Artifact.FileSha256 &&
                    verified.Value.Verification.ArtifactSize == artifact.Artifact.FileSize,
                    "fresh verification binds the exact artifact in a different child");
                var child = identified.Single(identity => identity.ProcessId == encode.Execution!.ProcessId);
                Check.That(!File.Exists(sources[lane].Path + ".quarantine") &&
                    !File.Exists(artifact.Artifact.Path + ".quarantine"), "both child-use holds ended after confirmed stop");
                lanes.Add(new(sources[lane].Path, child, hashes, capture.OutputMaximumBytes, encode, verify));
            }
            // Disable the barrier before proving ordinary subsequent admission.
            barrier.Release();
            var recovered = new List<MediaProcessingRuntime.MediaProcessingReservation>(2);
            try
            {
                for (int slot = 0; slot < limits.MaxConcurrent; slot++)
                    recovered.Add(await runtime.AcquireAsync(ct: ct));
            }
            finally { foreach (var reservation in recovered) reservation.Dispose(); }
            return new(Scope, limits.ValidatedCopy(), original, identified, thirdSeconds, lanes, recovered.Count, true);
        }
        catch (OperationCanceledException error) when (ct.IsCancellationRequested && error.CancellationToken != ct)
        {
            primaryFailure = new OperationCanceledException("Processing qualification cancelled.", error, ct);
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            throw;
        }
        catch (Exception error) { primaryFailure = error; throw; }
        finally
        {
            Exception? cleanupFailure = null;
            try
            { await cancellation.CancelAsync(); }
            catch (Exception error) { cleanupFailure = error; }
            barrier.Release();
            // Every task is joined, including those not observed after an assertion failed.
            foreach (var operation in pending)
            {
                try
                {
                    var result = await operation;
                    if (!result.IsError && !artifacts.Contains(result.Value.Artifact))
                        artifacts.Add(result.Value.Artifact);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception error) { cleanupFailure ??= error; }
            }
            foreach (var identity in barrier.Snapshot())
            {
                try
                { AssertIdentityStopped(identity); }
                catch (Exception error) { cleanupFailure ??= error; }
            }
            foreach (var artifact in artifacts.Concat(sources))
            {
                try
                { await artifact.DisposeAsync(); }
                catch (Exception error) { cleanupFailure ??= error; }
            }
            foreach (var verifier in verifiers)
                verifier.Dispose();
            if (cleanupFailure is not null)
            {
                if (primaryFailure is null)
                    ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
                else
                {
                    // A read-only exception Data implementation must not replace the primary failure.
                    try
                    { primaryFailure.Data["ProcessingCleanupFailureType"] = cleanupFailure.GetType().Name; }
                    catch (Exception) { }
                }
            }
        }
    }

    private static async Task<byte[]> ReadRecoveryAsync(string path, CancellationToken ct)
    {
        Check.That(Path.IsPathFullyQualified(path), "absolute tiny recovery fixture");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous);
        long length = input.Length;
        Check.That(length is > 0x80 and <= (8L << 20), "bounded tiny recovery fixture");
        byte[] bytes = new byte[checked((int)length)];
        await input.ReadExactlyAsync(bytes, ct);
        byte[] extra = new byte[1];
        Check.That(await input.ReadAsync(extra, ct) == 0, "tiny recovery fixture did not grow during observation");
        return bytes;
    }

    private static void MatchLogical(RomVerification proof, Digests original) => Check.That(
        proof.ContentSize == original.Size && proof.ContentSha256.ToString() == original.Sha256 && proof.Md5.ToString() == original.Md5 &&
        proof.Sha1.ToString() == original.Sha1 && proof.Crc32.ToString() == original.Crc, "all five logical source/output facts match");

    private static void AssertLive(IReadOnlyList<MediaProcessIdentity> identities)
    {
        Check.That(identities.Count == 2 && identities.Select(identity => identity.ProcessId).Distinct().Count() == 2,
            "two distinct exact child identities");
        foreach (var identity in identities)
            Check.That(MediaProcessIdentity.Observe(identity.ProcessId).Identity == identity &&
                (OperatingSystem.IsWindows() || identity.GroupId == identity.ProcessId), "identified child still alive before permission");
    }

    internal static void AssertStopped(MediaWorkerResult result)
    {
        Check.That(result.ProcessTreeStopped && result.Execution is { } execution &&
            execution.StandardOutputBytes <= MediaWorkerProtocol.MaximumOutputBytes && execution.StandardErrorBytes <= 16384,
            "bounded child execution has an affirmative stop certificate");
        int pid = result.Execution!.ProcessId;
        Check.That(MediaProcessIdentity.Observe(pid).Presence == MediaProcessPresence.Gone, "exact owned child is absent after return");
        if (!OperatingSystem.IsWindows())
            Check.That(Kill(-pid, 0) != 0 && Marshal.GetLastPInvokeError() == 3, "owned child group extinct after return");
    }

    internal static void AssertIdentityStopped(MediaProcessIdentity identity)
    {
        Check.That(MediaProcessIdentity.Observe(identity.ProcessId).Presence == MediaProcessPresence.Gone,
            "identified owned child absent after failure cleanup");
        if (!OperatingSystem.IsWindows())
            Check.That(Kill(-identity.GroupId, 0) != 0 && Marshal.GetLastPInvokeError() == 3,
                "identified owned group extinct after failure cleanup");
    }

    private sealed class IdentifiedBarrier
    {
        private readonly object _gate = new();
        private readonly List<MediaProcessIdentity> _children = new(2);
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _permit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _enabled;
        internal Task BothIdentified => _both.Task;
        internal void Enable() { lock (_gate) _enabled = true; }
        internal void Release() => _permit.TrySetResult();
        internal MediaProcessIdentity[] Snapshot() { lock (_gate) return _children.ToArray(); }
        internal async Task OnSetupAsync(Process process, MediaWorkerSetupPoint point, CancellationToken ct)
        {
            if (point != MediaWorkerSetupPoint.Identified)
                return;
            lock (_gate)
            {
                if (!_enabled || _permit.Task.IsCompleted)
                    return;
                var identity = MediaProcessIdentity.Read(process.Id);
                Check.That(identity is not null && _children.Count < 2, "bounded identified barrier owns the exact child");
                _children.Add(identity!);
                if (_children.Count == 2)
                    _both.TrySetResult();
            }
            await _permit.Task.WaitAsync(ct);
        }
    }

    private sealed class Capture(MediaWorkerSupervisor inner) : IMediaWorkerSupervisor
    {
        internal MediaWorkerResult? Encoding { get; private set; }
        internal MediaWorkerResult? Inspection { get; private set; }
        internal long OutputMaximumBytes { get; private set; }
        public bool IsAvailable => inner.IsAvailable;
        public Task<MediaWorkerResult> HandshakeAsync(CancellationToken ct = default) => inner.HandshakeAsync(ct);
        public async Task<MediaWorkerResult> InspectAsync(ITempFile source, CancellationToken ct = default) => Inspection = await inner.InspectAsync(source, ct);
        public async Task<MediaWorkerResult> InspectIsoAsync(ITempFile source, CancellationToken ct = default) => Inspection = await inner.InspectIsoAsync(source, ct);
        public async Task<MediaWorkerResult> EncodeAsync(ITempFile source, IWritableTempFile output, long logicalBytes,
            RomIsoDiscKind discKind, CancellationToken ct = default)
        {
            OutputMaximumBytes = output.MaximumBytes;
            return Encoding = await inner.EncodeAsync(source, output, logicalBytes, discKind, ct);
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)] private static extern int Kill(int pid, int signal);
}
