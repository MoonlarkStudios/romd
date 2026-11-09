using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Infrastructure.Storage;
using Romd.Media.Chd;
using Romd.Media.Inspection;

namespace Romd.Media.Probe;

/// <summary>Exercises real supervised CHD children against independent synthetic DVD bytes.</summary>
internal static class ChdInspectionChecks
{
    private sealed record Configuration(string NativeDirectory, string InputChd, string OracleIso, string DataDirectory);

    public static async Task RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length != 3)
            throw new ArgumentException("Usage: --chd-inspection-probe <absolute configuration.json> <absolute new report.json>");
        MediaProbePaths.Validate([args[1], args[2]]);
        byte[] bytes = BoundedRuntimeFile.Read(args[1], 16384);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        Check.That(MediaRunRecord.Unique(document.RootElement), "unique CHD probe configuration fields");
        var config = JsonSerializer.Deserialize<Configuration>(bytes, new JsonSerializerOptions
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true })
            ?? throw new InvalidDataException("Missing CHD probe configuration.");
        var candidate = ChdNativeCandidate.FromBuildRecord(config.NativeDirectory);
        ChdNativeIdentity identity = candidate.ExpectedIdentity;
        MediaProbePaths.Validate([candidate.BinaryPath, candidate.ManifestPath, config.InputChd, config.OracleIso, config.DataDirectory]);
        Check.That(!Directory.Exists(config.DataDirectory), "new owned CHD probe storage");
        byte[] original;
        await using (var input = File.OpenRead(config.InputChd))
        {
            long length = input.Length;
            Check.That(length is >= 124 and <= (1 << 20), "small synthetic CHD input");
            original = new byte[checked((int)length)];
            await input.ReadExactlyAsync(original, ct);
            Check.That(await input.ReadAsync(new byte[1], ct) == 0 && input.Length == length,
                "exact bounded CHD snapshot");
        }
        await using var output = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var rows = new List<object>();
        var limits = new MediaProcessingOptions { MaxConcurrent = 1, WallTimeoutSeconds = 30, CpuTimeoutSeconds = 30 };
        var storage = Substitute.For<IRomdOptions>();
        storage.DataDirectory.Returns(config.DataDirectory);
        storage.MaxUploadBytes.Returns(16L << 20);
        var runtime = new MediaProcessingRuntime(storage, limits);
        var factory = new LocalTempFileFactory(storage, new FileSystem());
        var supervisor = new MediaWorkerSupervisor(limits, runtime, candidate);
        var oracle = await BoundedDigestsAsync(config.OracleIso, 16 << 20, ct);
        using var snapshot = new MemoryStream(original, writable: false);
        var artifact = await Digests.StreamAsync(snapshot, ct, original.Length);
        string controls = Path.Combine(config.DataDirectory, "probe-controls");
        Directory.CreateDirectory(controls);
        await SaveAsync("in-progress", null);
        try
        {
            var managed = await supervisor.HandshakeAsync(ct);
            Record("managed-handshake", managed);
            Check.That(managed.IsSuccess && supervisor.IsAvailable && !supervisor.IsChdAvailable, "independent managed readiness");
            var handshake = await supervisor.HandshakeChdAsync(ct);
            Record("chd-handshake", handshake);
            Check.That(handshake.IsSuccess && supervisor.IsChdAvailable && handshake.Reply!.NativeIdentity == identity,
                "exact verified native child handshake");
            await InspectAsync("dvd", config.InputChd, null, null);

            foreach (var control in new[] { ("raw-missing", 64, "MissingRawSha1"),
                         ("raw-mismatch", 64, "RawSha1Mismatch"), ("overall-mismatch", 84, "OverallSha1Mismatch") })
            {
                byte[] changed = (byte[])original.Clone();
                if (control.Item1 == "raw-missing")
                    Array.Clear(changed, control.Item2, 20);
                else
                    changed[control.Item2] ^= 1;
                string path = Path.Combine(controls, control.Item1 + ".chd");
                await File.WriteAllBytesAsync(path, changed, ct);
                await InspectAsync(control.Item1, path, ChdRejectionStage.Integrity, control.Item3);
                Check.That(!supervisor.IsChdAvailable && supervisor.IsAvailable, "CHD rejection preserves managed readiness");
                var restored = await supervisor.HandshakeChdAsync(ct);
                Record(control.Item1 + "-restored-handshake", restored);
                Check.That(restored.IsSuccess && restored.Reply!.NativeIdentity == identity,
                    "CHD readiness restored by fresh handshake");
            }

            // A copy of the verified build with one changed binary byte keeps its record but not its digest.
            string tampered = Path.Combine(controls, "native-tampered");
            Directory.CreateDirectory(Path.Combine(tampered, "native"));
            File.Copy(candidate.ManifestPath, Path.Combine(tampered, ChdNativeCandidate.RecordFileName));
            byte[] binary = await File.ReadAllBytesAsync(candidate.BinaryPath, ct);
            binary[^1] ^= 1;
            await File.WriteAllBytesAsync(Path.Combine(tampered, "native", identity.File), binary, ct);
            var wrong = ChdNativeCandidate.FromBuildRecord(tampered);
            var rejecting = new MediaWorkerSupervisor(limits, runtime, wrong);
            var rejected = await rejecting.HandshakeChdAsync(ct);
            Record("native-sha256-negative", rejected);
            Check.That(!rejected.IsSuccess && rejected.Reply?.ChdRejection is
            { Stage: ChdRejectionStage.NativeVerification, Cause: "BinarySha256Mismatch" }, "native bytes reject before load");
            byte[] header = (byte[])original.Clone();
            header[0] ^= 1;
            string headerPath = Path.Combine(controls, "header-before-native.chd");
            await File.WriteAllBytesAsync(headerPath, header, ct);
            await using (var input = File.OpenRead(headerPath))
            await using (var staged = await factory.CreateAsync(input, ct))
            {
                var result = await rejecting.InspectChdAsync(staged, ct);
                Record("header-before-native-negative", result);
                Check.That(!result.IsSuccess && result.Reply?.ChdRejection is
                { Stage: ChdRejectionStage.Header, Cause: "InvalidSignature" } && result.Reply.NativeIdentity is null,
                    "header rejection precedes deliberately wrong native candidate");
            }
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await Check.Throws<OperationCanceledException>(() => supervisor.HandshakeChdAsync(canceled.Token));
                Check.That(!supervisor.IsChdAvailable && supervisor.IsAvailable, "CHD cancellation fails its readiness closed");
                rows.Add(new { Step = "pre-cancel", Cancelled = true });
            }
            Record("restored-handshake", await supervisor.HandshakeChdAsync(ct));
            Check.That(supervisor.IsChdAvailable, "restored CHD readiness");
            await InspectAsync("restored-dvd", config.InputChd, null, null);
            Check.That(await BoundedDigestsAsync(config.InputChd, artifact.Size, ct) == artifact &&
                await BoundedDigestsAsync(config.OracleIso, oracle.Size, ct) == oracle, "original synthetic inputs unchanged");
            Check.That(!Directory.EnumerateFiles(Path.Combine(config.DataDirectory, "temp"), "run-*.json", SearchOption.AllDirectories).Any(),
                "joined inspections release supervised custody");
            await SaveAsync("passed", null);
        }
        catch (Exception error)
        {
            await SaveAsync("failed", error.ToString());
            throw;
        }

        async Task InspectAsync(string name, string path, ChdRejectionStage? stage, string? cause)
        {
            await using var input = File.OpenRead(path);
            await using var staged = await factory.CreateAsync(input, ct);
            var result = await supervisor.InspectChdAsync(staged, ct);
            Record(name, result);
            if (stage is { } expected)
            {
                Check.That(!result.IsSuccess && result.Reply?.ChdRejection?.Stage == expected &&
                    result.Reply.ChdRejection.Cause == cause && result.Reply.Inspection is null,
                    "exact typed integrity rejection: " + name);
                return;
            }
            var proof = result.Reply?.Inspection;
            var integrity = result.Reply?.ChdInspection;
            Check.That(result.IsSuccess && proof is not null && integrity is not null &&
                result.Reply!.NativeIdentity == identity, "successful inspection has exact native identity");
            Check.That(proof!.ArtifactBytes == artifact.Size && proof.ArtifactSha256 == artifact.Sha256 &&
                proof.LogicalBytes == oracle.Size && proof.LogicalSha256 == oracle.Sha256 && proof.Sha1 == oracle.Sha1 &&
                proof.Md5 == oracle.Md5 && proof.Crc32 == oracle.Crc && proof.EncodingProfile == "chd-import/v1" && proof.DiscKind is null,
                "artifact and four logical digests match independent oracle");
            Check.That(integrity!.StoredRawSha1 == oracle.Sha1 && integrity.ComputedRawSha1 == oracle.Sha1 &&
                integrity.StoredOverallSha1 == integrity.ComputedOverallSha1, "both stored CHD hashes required");
        }

        void Record(string step, MediaWorkerResult result)
        {
            Check.That(result.ProcessTreeStopped && result.Execution is { ProcessId: > 0 }, "fresh child joined: " + step);
            rows.Add(new { Step = step, Result = result });
            Console.WriteLine(step + ": " + (result.Failure?.ToString() ?? result.Reply?.Status.ToString()));
        }

        async Task SaveAsync(string status, string? error)
        {
            output.Position = 0;
            output.SetLength(0);
            await JsonSerializer.SerializeAsync(output, new
            {
                Status = status,
                RecordedUtc = DateTimeOffset.UtcNow,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture,
                RuntimeInformation.FrameworkDescription,
                Native = identity,
                Oracle = oracle,
                Artifact = artifact,
                Limits = limits,
                Rows = rows,
                Error = error,
                Scope = "Synthetic supervised CHD inspection only; no release, encoding, import publication or full worker qualification."
            },
                JsonOptions.Value, CancellationToken.None);
            await output.FlushAsync(CancellationToken.None);
        }
    }

    private static async Task<Digests> BoundedDigestsAsync(string path, long maximumBytes, CancellationToken ct)
    {
        await using var input = File.OpenRead(path);
        long length = input.Length;
        Check.That(length > 0 && length <= maximumBytes, "bounded synthetic digest input");
        var measured = await Digests.StreamAsync(input, ct, length);
        Check.That(measured.Size == length && input.Length == length, "synthetic digest input size unchanged");
        return measured;
    }
}
