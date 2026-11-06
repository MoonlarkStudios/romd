using System.IO.Abstractions;
using System.Text.Json;
using NSubstitute;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.PlayStation;

namespace Romd.Media.Probe;

/// <summary>Bounded synthetic-only Linux qualification. No database, real-media inference or production activation.</summary>
internal static class ChdCdRoundTripChecks
{
    internal static async Task RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length != 6)
            throw new ArgumentException("Usage: --chd-cd-roundtrip-probe <fixtures> <native> <tool> <new data> <new report>");
        if (!OperatingSystem.IsLinux())
            throw new InvalidOperationException("Genuine supervised CD encoding qualification requires Linux.");
        MediaProbePaths.Validate(args[1..]);
        if (Directory.Exists(args[4]) || File.Exists(args[4]) || File.Exists(args[5]) || args[4] == args[5] ||
            args[1..4].Any(path => MediaProbePaths.Overlaps(path, args[4])) || MediaProbePaths.Overlaps(args[4], args[5]))
            throw new InvalidDataException("Qualification requires fresh separate data/report paths outside inputs.");
        var native = ChdNativeCandidate.FromBuildRecord(args[2]);
        var tool = ChdmanCandidate.FromBuildRecord(args[3]);
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(args[4]);
        options.MaxUploadBytes.Returns(ChdCdLimits.MaximumArtifactBytes);
        var limits = new MediaProcessingOptions { MaxConcurrent = 1 };
        var runtime = new MediaProcessingRuntime(options, limits);
        var factory = new LocalTempFileFactory(options, new FileSystem(), new TempStorageOptions { MinimumFreeBytes = 0 });
        var supervisor = new MediaWorkerSupervisor(limits, runtime, native);
        var workflow = new ChdCdArchiveWorkflow(factory, supervisor, TimeProvider.System);
        List<object> rows = [];
        List<object> safety = [];
        await using var report = new FileStream(args[5], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await Save("in-progress", null);
        try
        {
            foreach (string shape in new[] { "single", "multi", "partial", "mode1" })
            {
                List<ITempFile> originals = [];
                try
                {
                    async Task<ITempFile> Stage(byte[] bytes)
                    { using var file = new MemoryStream(bytes, writable: false); var staged = await factory.CreateAsync(file, ct); originals.Add(staged); return staged; }
                    // Tiny CHDs can finish before the strict executed-image observer samples the tool.
                    // Use bounded larger originals; never waive image observation for fast exits.
                    bool mode1 = shape == "mode1";
                    int[] frames = [mode1 ? 32769 : 16385, 8191, 4097];
                    int trackCount = mode1 ? 1 : shape == "partial" ? 2 : 3;
                    var cueText = new System.Text.StringBuilder("REM ROMD synthetic whole-file qualification\r\n");
                    int fileFrame = 0;
                    for (int number = 1; number <= trackCount; number++)
                    {
                        if (shape != "single" || number == 1)
                        { cueText.Append($"FILE \"{(shape == "single" ? "cd-edge-single.bin" : $"cd-edge-track{number}.bin")}\" BINARY\r\n"); fileFrame = 0; }
                        cueText.Append($"  TRACK {number:00} {(number == 1 ? mode1 ? "MODE1/2352" : "MODE2/2352" : "AUDIO")}\r\n");
                        if (number == 2)
                            cueText.Append($"    INDEX 00 {Time(fileFrame)}\r\n");
                        if (number == 3)
                            cueText.Append("    PREGAP 00:00:02\r\n");
                        cueText.Append($"    INDEX 01 {Time(fileFrame + (number == 2 ? 2 : 0))}\r\n");
                        fileFrame += frames[number - 1];
                    }
                    var cue = await Stage(System.Text.Encoding.UTF8.GetBytes(cueText.ToString()));
                    var parsed = ChdCdCue.Parse(System.Text.Encoding.UTF8.GetBytes(cueText.ToString()));
                    Dictionary<string, ITempFile> bins = new(StringComparer.Ordinal);
                    foreach (var file in parsed.Files)
                    {
                        byte[] original = new byte[file.Tracks.Sum(n => frames[n - 1]) * 2352];
                        int offset = 0;
                        foreach (int n in file.Tracks)
                        {
                            string templatePath = Path.Combine(args[1], mode1 ? "cd-source.bin" : $"cd-edge-track{n}.bin");
                            if (new FileInfo(templatePath).Length is < 2352 or > (1 << 20))
                                throw new InvalidDataException("Synthetic template exceeds budget.");
                            byte[] template = File.ReadAllBytes(templatePath);
                            // cd-source.bin has 16 raw MODE1 sectors followed by 16 audio sectors.
                            if (mode1)
                                template = template[..(16 * 2352)];
                            int bytes = frames[n - 1] * 2352;
                            for (int copied = 0; copied < bytes;)
                            { int take = Math.Min(template.Length, bytes - copied); Buffer.BlockCopy(template, 0, original, offset + copied, take); copied += take; }
                            offset += bytes;
                        }
                        bins.Add(file.Name, await Stage(original));
                    }
                    var sidecar = await factory.CreateAsync(new MemoryStream(new byte[] { 0x53, 0x42, 0x49, 0, 0xfe }), ct);
                    originals.Add(sidecar);
                    await using var sources = await workflow.PrepareAsync($"cd-edge-{shape}.cue", cue, bins,
                        new Dictionary<string, ITempFile> { ["synthetic.sbi"] = sidecar }, ct);
                    await using var result = await workflow.EncodeAsync(sources, tool, 128 << 20, ct);
                    if (result.EncodingRun.Execution!.ProcessId == result.VerificationRun.Execution!.ProcessId ||
                        !result.EncodingRun.ProcessTreeStopped || !result.VerificationRun.ProcessTreeStopped)
                        throw new InvalidDataException("Independent joined encoder and decoder children required.");
                    foreach (var member in ((RomReconstructionManifest.CdTracks)result.Measured.Manifest).Members.OfType<RomCdBinMember>())
                        if (member.Identity.Sha256 != bins[member.Path].FileSha256 || member.Identity.Size != bins[member.Path].FileSize)
                            throw new InvalidDataException("Whole original member identity mismatch.");
                    if (((RomReconstructionManifest.CdTracks)result.Measured.Manifest).Tracks[0].Kind != (mode1 ? RomCdTrackKind.Mode1Raw : RomCdTrackKind.Mode2Raw))
                        throw new InvalidDataException("Encoded data track mode differs from the qualified layout.");
                    rows.Add(new
                    {
                        Shape = shape,
                        Encoding = result.EncodingRun,
                        Verification = result.VerificationRun,
                        Manifest = ChdCdFactReport.Create(result.Measured)
                    });
                    await Save("in-progress", null);
                    Console.WriteLine($"CD {shape}: encoded and independently verified every original member.");
                    if (shape == "single")
                        await CheckEncodingStopsAsync(sources, result.Artifact);
                }
                finally { foreach (var original in originals) await original.DisposeAsync(); }
            }
            await Save("passed", null);
        }
        catch (Exception error) { await Save("failed", error.GetType().Name + ": " + error.Message); throw; }
        async Task CheckEncodingStopsAsync(PreparedCdSources sources, ITempFile verifiedArtifact)
        {
            var smallReservation = await factory.ReserveOutputAsync(124, ct);
            if (smallReservation.IsError)
                throw new InvalidDataException("Cannot reserve bounded failure output.");
            await using (var small = smallReservation.Value)
            {
                var rejected = await supervisor.EncodeChdCdAsync(sources.Request, sources.Inputs, small, tool, ct);
                long written = File.Exists(small.Path) ? new FileInfo(small.Path).Length : 0;
                if (rejected.IsSuccess || !rejected.ProcessTreeStopped || rejected.Reply?.CdEncoding is not null || written > 124)
                    throw new InvalidDataException("Undersized output escaped its cap or produced archive evidence.");
                safety.Add(new { Case = "output-cap", MaximumBytes = 124, ObservedBytes = written, Result = rejected });
            }
            var reservation = await factory.ReserveOutputAsync(128 << 20, ct);
            if (reservation.IsError)
                throw new InvalidDataException("Cannot reserve cancellation output.");
            await using (var output = reservation.Value)
            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var encoding = supervisor.EncodeChdCdAsync(sources.Request, sources.Inputs, output, tool, stop.Token);
                long observed = 0;
                int child = 0;
                try
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    while (!encoding.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(60))
                    {
                        observed = File.Exists(output.Path) ? new FileInfo(output.Path).Length : 0;
                        if (observed > 124)
                        {
                            var record = MediaRunRecord.Read(Path.Combine(args[4], "temp", "media-runs-v1", "run-0.json"), 0, 1);
                            child = record?.Identity?.ProcessId ?? 0;
                            if (child > 0)
                                break;
                        }
                        await Task.Delay(20, ct);
                    }
                    if (child <= 0 || encoding.IsCompleted)
                        throw new InvalidDataException("No active genuine CD output observed before cancellation.");
                    await stop.CancelAsync();
                    try
                    { await encoding; throw new InvalidDataException("CD encoding ignored cancellation."); }
                    catch (MediaWorkerStoppedCancellationException) { }
                    foreach (var input in sources.Inputs.Append(sources.Request))
                        if (!File.Exists(input.Path) || File.Exists(input.Path + ".quarantine"))
                            throw new InvalidDataException("CD cancellation did not retain inputs and release joined holds.");
                    if (File.Exists(output.Path + ".quarantine"))
                        throw new InvalidDataException("CD output remained active after confirmed cancellation.");
                    safety.Add(new
                    {
                        Case = "cancel-active-encoding",
                        ProcessId = child,
                        ObservedOutputBytes = observed,
                        ProcessTreeStopped = true,
                        InputsRetained = true,
                        HoldsReleased = true
                    });
                }
                finally
                {
                    await stop.CancelAsync();
                    try
                    { await encoding; }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                }
            }
            // A fresh decoder proves admission can be reused after both failure and cancellation.
            await workflow.VerifyExistingAsync(verifiedArtifact, sources, ct);
            await Save("in-progress", null);
            Console.WriteLine("CD safety: output cap enforced; active encoding cancelled and joined; fresh verification passed.");
        }
        async Task Save(string status, string? error)
        {
            report.Position = 0;
            report.SetLength(0);
            await JsonSerializer.SerializeAsync(report, new
            {
                Status = status,
                Profile = "chd-cd-synthetic-roundtrip/v1",
                RecordedUtc = DateTimeOffset.UtcNow,
                Native = native.ExpectedIdentity,
                Tool = tool.ExpectedIdentity,
                Recipe = ChdCdEncodingRecipe.Id,
                Rows = rows,
                Safety = safety,
                Error = error,
                Scope = "Synthetic Linux round trips only. Original descriptors/sidecars retained. No real-media, production recipe, import, delivery, runtime or grouping qualification."
            },
                new JsonSerializerOptions { WriteIndented = true }, CancellationToken.None);
            await report.FlushAsync(CancellationToken.None);
        }
        static string Time(int frames) => FormattableString.Invariant($"{frames / 4500:00}:{frames / 75 % 60:00}:{frames % 75:00}");
    }
}
