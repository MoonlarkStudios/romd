using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.Cd;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;

namespace Romd.Media.Probe;

/// <summary>Separates isolated and fixed-arrival interactive latency from saturated bulk throughput.</summary>
internal static class ChdCdInteractiveReadChecks
{
    internal sealed record Case(ChdCdReadCatalog.Member Member, int RequestedBytes, string Boundary, InteractiveRangeRead.Range Range);

    internal static async Task RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length != 5 || !int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds is < 300 or > 1800)
            throw new ArgumentException("Usage: --chd-cd-interactive-read-probe <absolute qualification.json> <absolute new qualification-report.json> <absolute new interactive-report.json> <seconds-per-load-phase:300..1800>");
        MediaProbePaths.Validate([args[1], args[2], args[3]]);
        Check.That(!MediaProbePaths.Overlaps(args[2], args[3]) && !File.Exists(args[3]), "distinct fresh interactive reports");
        var qualified = await ChdCdQualificationChecks.RunAsync(["--chd-cd-qualification-probe", args[1], args[2]],
            ChdCdQualificationChecks.Retention.ArchiveForReadLoad, [args[3]], ct);
        Check.That(qualified.Archives.Count(archive => archive.Measured.Manifest is RomReconstructionManifest.CdTracks layout &&
            layout.Tracks.Any(track => track.Kind == RomCdTrackKind.Audio) && layout.Tracks.Any(track => track.Kind != RomCdTrackKind.Audio)) >= 1,
            "interactive qualification includes a real measured mixed-track disc");
        await using var database = PostgreSqlTestDatabase.Create();
        await using var services = new ServiceCollection().AddContentAddressableStorage(options =>
        {
            options.RootPath = Path.Combine(qualified.Configuration.DataDirectory, "interactive-cas");
            options.WritePolicy = ContentWritePolicy.Raw;
            options.MaxContentSize = ChdCdLimits.MaximumArtifactBytes;
        }).BuildServiceProvider();
        var cas = services.GetRequiredService<IContentAddressableStore>();
        await using (var db = database.CreateContext())
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await ChdCdVfsReadChecks.PublishReadLibraryAsync(db, cas, qualified.Archives, ct);
            await transaction.CommitAsync(ct);
        }
        ChdCdReadCatalog.Published catalog;
        await using (var db = database.CreateContext())
            catalog = await ChdCdReadCatalog.SeedAsync(db, qualified.Archives, ct);
        var cases = await BuildCasesAsync(catalog.Members, qualified.Archives, ct);
        var media = RomMediaPolicyComposition.Create();
        var evidence = new InteractiveReadEvidence(cases);
        await using var report = new FileStream(args[3], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var reportGate = new SemaphoreSlim(1, 1);
        async Task SaveAsync() => await SaveReportAsync(report, evidence, qualified, reportGate, ct);
        await SaveAsync();
        try
        {
            // Every cold observation starts with a fresh decoder/cache and a fresh TLS client.
            foreach (var sample in cases.Where(sample => sample.Boundary == "start"))
            {
                await using var isolated = await ChdCdInteractiveReadHost.StartAsync(database, cas, media,
                    qualified.Configuration.NativeDirectory, catalog.Users, ct);
                evidence.HttpLimits = isolated.HttpLimits;
                await MeasureAsync(isolated.Clients[2], sample, "fresh-decoder-and-tls", evidence, 0, ct);
                for (int count = 0; count < 20; count++)
                    await MeasureAsync(isolated.Clients[2], sample, "warm-isolated", evidence, 0, ct);
            }
            await SaveAsync();
            await using (var host = await ChdCdInteractiveReadHost.StartAsync(database, cas, media,
                qualified.Configuration.NativeDirectory, catalog.Users, ct))
            {
                evidence.HttpLimits = host.HttpLimits;
                // The same authority path must reject both an invalid secret and withdrawn library access.
                await AssertAuthorityAsync(host, database, catalog, ct);
                await PhaseAsync(host, cases, catalog.Members, "idle", 0, TimeSpan.FromSeconds(60), evidence, SaveAsync, ct);
                await PhaseAsync(host, cases, catalog.Members, "four-bulk-readers", 4, TimeSpan.FromSeconds(seconds), evidence, SaveAsync, ct);
                await PhaseAsync(host, cases, catalog.Members, "six-bulk-readers", 6, TimeSpan.FromSeconds(seconds), evidence, SaveAsync, ct);
                await PhaseAsync(host, cases, catalog.Members, "recovery", 0, TimeSpan.FromSeconds(60), evidence, SaveAsync, ct);
                await host.DisposeAsync();
                evidence.ClosedPool = host.Runtime.NativeSnapshot;
                evidence.DecoderObservations = host.Observations.Snapshot();
                Check.That(evidence.ClosedPool is
                {
                    LiveHandles: 0, ActiveHandles: 0, IdleHandles: 0, Flights: 0,
                    FlightWaiters: 0, Artifacts: 0, Speculations: 0, OpeningHandles: 0
                } pool && pool.OpenedHandles == pool.ClosedHandles,
                    "interactive native handles, artifacts and flights all joined");
            }
            evidence.Status = "measured";
        }
        catch (Exception error)
        {
            evidence.Status = "failed";
            evidence.Error = error.ToString();
            throw;
        }
        finally { await SaveReportAsync(report, evidence, qualified, reportGate, CancellationToken.None); }
    }

    internal static async Task<IReadOnlyList<Case>> BuildCasesAsync(IReadOnlyList<ChdCdReadCatalog.Member> members,
        IReadOnlyList<ChdCdQualificationChecks.QualifiedArchive> archives, CancellationToken ct)
    {
        var cases = new List<Case>();
        foreach (var disc in members.GroupBy(member => member.Disc))
        {
            if (!disc.Any(member => member.Kind == nameof(RomCdTrackKind.Audio)) ||
                !disc.Any(member => member.Kind != nameof(RomCdTrackKind.Audio)))
                continue;
            foreach (bool audio in new[] { false, true })
            {
                var member = disc.First(candidate => (candidate.Kind == nameof(RomCdTrackKind.Audio)) == audio && candidate.Identity.Size >= 1 << 20);
                var container = (RomContainerFacts.Chd)archives[member.Disc - 1].Measured.Container;
                var layout = (RomReconstructionManifest.CdTracks)archives[member.Disc - 1].Measured.Manifest;
                int track = ((RomCdBinMember)layout.Members[member.Ordinal]).Tracks.Single();
                long firstFrame = layout.Tracks.Single(candidate => candidate.Number == track).FirstFrame;
                long hunkFrames = container.HunkBytes / container.UnitBytes;
                long hunkBoundary = (hunkFrames - firstFrame % hunkFrames) * 2352 - 1;
                foreach (int bytes in new[] { 4 << 10, 64 << 10, 1 << 20 })
                    foreach (var boundary in new[] { ("start", 0L), ("hunk", hunkBoundary), ("sector-150", 150L * 2352 - 2048), ("eof", member.Identity.Size - bytes / 2) })
                    {
                        long offset = Math.Clamp(boundary.Item2, 0, member.Identity.Size - 1);
                        byte[] expected = new byte[(int)Math.Min(bytes, member.Identity.Size - offset)];
                        using var oracle = File.OpenHandle(member.OraclePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        int filled = 0;
                        while (filled < expected.Length)
                        {
                            int count = await RandomAccess.ReadAsync(oracle, expected.AsMemory(filled), offset + filled, ct);
                            Check.That(count > 0, "complete independently retained range oracle");
                            filled += count;
                        }
                        cases.Add(new(member, bytes, boundary.Item1, new(member.Url, member.Identity.Size, member.Identity.Sha256, offset, bytes, expected)));
                    }
            }
        }
        Check.That(cases.Count is >= 24 and <= 16 * 24, "bounded data and audio interactive range cases");
        return cases;
    }

    private static async Task MeasureAsync(HttpClient client, Case sample, string phase, InteractiveReadEvidence evidence,
        long scheduled, CancellationToken ct)
    {
        double dispatch = scheduled == 0 ? 0 : Stopwatch.GetElapsedTime(scheduled).TotalMilliseconds;
        evidence.BeginRead(sample, phase);
        try
        {
            var measurement = await InteractiveRangeRead.ReadAsync(client, sample.Range, TimeSpan.FromSeconds(120), ct);
            evidence.Record(sample, phase, measurement, dispatch);
        }
        catch (Exception error)
        {
            evidence.Fail(sample, phase, error is OperationCanceledException ? "cancelled" : "fatal-integrity-or-protocol-failure");
            throw;
        }
    }

    internal static async Task PhaseAsync(ChdCdInteractiveReadHost host, IReadOnlyList<Case> cases,
        IReadOnlyList<ChdCdReadCatalog.Member> members, string phase, int bulkReaders, TimeSpan duration,
        InteractiveReadEvidence evidence, Func<Task> save, CancellationToken ct)
    {
        using var bulk = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progress = evidence.BeginPhase(phase, bulkReaders, duration);
        var resources = ReadResourceChecks.Create(host.Runtime.Decoder);
        Check.That(!resources.IsError, "interactive decoder/cache resources observable");
        Check.That(!resources.Value.BeginLoad().IsError, "interactive read resource baseline is observable");
        var workers = Enumerable.Range(0, bulkReaders).Select(async lane =>
        {
            try
            { await BulkAsync(host.Clients[lane % 2], members, lane, progress, bulk.Token); }
            catch { await bulk.CancelAsync(); throw; }
        }).ToArray();
        Task allBulk = Task.WhenAll(workers);
        using var sampling = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task sampler = SampleAsync();
        try
        {
            progress.Schedule = await InteractiveReadSchedule.RunAsync(duration, TimeSpan.FromMilliseconds(500), 8,
                async (index, scheduled, token) =>
                {
                    if (allBulk.IsFaulted)
                        await allBulk;
                    await MeasureAsync(host.Clients[2], cases[index % cases.Count], phase, evidence, scheduled, token);
                }, index => evidence.Missed(cases[index % cases.Count], phase), bulk.Token);
        }
        finally
        {
            await bulk.CancelAsync();
            await sampling.CancelAsync();
            Exception? joinFailure = null;
            try
            { await allBulk; }
            catch (OperationCanceledException) when (bulk.IsCancellationRequested && !ct.IsCancellationRequested) { }
            catch (Exception error) { joinFailure = error; }
            try
            { await sampler; }
            catch (Exception error) { joinFailure ??= error; }
            progress.Complete();
            progress.Resources = resources.Value.Report();
            progress.DecoderObservations = host.Observations.Snapshot();
            await save();
            if (joinFailure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(joinFailure).Throw();
        }
        async Task SampleAsync()
        {
            try
            {
                int samples = 0;
                while (!sampling.IsCancellationRequested)
                {
                    // Each outer interactive period owns a separate read-only resource baseline.
                    // The resource observer uses its bounded read-phase vocabulary, independently of the workload label.
                    var sample = resources.Value.Sample("sustained-load");
                    Check.That(!sample.IsError, "interactive decoder/cache observation: " + (sample.IsError ? sample.FirstError.Code : "within bounds"));
                    progress.Observe(host.Runtime.NativeSnapshot, host.Admission);
                    if (samples++ % 4 == 0)
                        await save();
                    try
                    { await Task.Delay(ReadResourceChecks.SamplingInterval, sampling.Token); }
                    catch (OperationCanceledException) when (sampling.IsCancellationRequested) { break; }
                }
            }
            catch { await bulk.CancelAsync(); throw; }
        }
    }

    private static async Task BulkAsync(HttpClient client, IReadOnlyList<ChdCdReadCatalog.Member> members, int lane,
        InteractiveReadEvidence.Phase progress, CancellationToken ct)
    {
        byte[] buffer = new byte[256 << 10];
        for (int index = lane; !ct.IsCancellationRequested; index += 1)
        {
            var member = members[index % members.Count];
            using var response = await client.GetAsync(member.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                await InteractiveRangeRead.ValidateRejectionAsync(response, ct);
                Interlocked.Increment(ref progress.BulkRejections);
                await Task.Delay(response.Headers.RetryAfter!.Delta!.Value, ct);
                continue;
            }
            Check.That(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentLength == member.Identity.Size,
                "bulk HTTPS returns the exact whole member");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            long bytes = 0;
            int count;
            while ((count = await body.ReadAsync(buffer, ct)) != 0)
            {
                bytes += count;
                hash.AppendData(buffer, 0, count);
            }
            Check.That(bytes == member.Identity.Size && Convert.ToHexStringLower(hash.GetHashAndReset()) == member.Identity.Sha256.ToString(),
                "whole HTTPS member equals the independent original digest");
            progress.BulkCompleted(member.Disc, member.Ordinal, bytes);
        }
    }

    internal static async Task AssertAuthorityAsync(ChdCdInteractiveReadHost host, PostgreSqlTestDatabase database,
        ChdCdReadCatalog.Published catalog, CancellationToken ct)
    {
        var client = host.Clients[2];
        var valid = client.DefaultRequestHeaders.Authorization;
        client.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("invalid:invalid")));
        using (var denied = await client.SendAsync(new(HttpMethod.Head, catalog.Members[0].Url), ct))
            Check.That(denied.StatusCode == HttpStatusCode.Unauthorized, "real credentials reject an invalid secret");
        client.DefaultRequestHeaders.Authorization = valid;
        await using var db = database.CreateContext();
        int? library = await db.Users.Where(user => user.Id == catalog.Users[2].Id).Select(user => user.LibraryId).SingleAsync(ct);
        await db.Users.Where(user => user.Id == catalog.Users[2].Id).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.LibraryId, (int?)null), ct);
        using (var withdrawn = await client.SendAsync(new(HttpMethod.Head, catalog.Members[0].Url), ct))
            Check.That(withdrawn.StatusCode == HttpStatusCode.NotFound, "real live-library lookup rejects withdrawn assignment");
        await db.Users.Where(user => user.Id == catalog.Users[2].Id).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.LibraryId, library), ct);
        using var restored = await client.SendAsync(new(HttpMethod.Head, catalog.Members[0].Url), ct);
        Check.That(restored.StatusCode == HttpStatusCode.OK, "real credentials and restored library resolve measured content");
    }

    private static async Task SaveReportAsync(FileStream report, InteractiveReadEvidence evidence,
        ChdCdQualificationChecks.QualifiedRun qualified, SemaphoreSlim reportGate, CancellationToken ct)
    {
        await reportGate.WaitAsync(ct);
        try
        {
            report.Position = 0;
            report.SetLength(0);
            await JsonSerializer.SerializeAsync(report, new
            {
                Schema = "romd-ps1-interactive-read/v1",
                RecordedUtc = DateTimeOffset.UtcNow,
                Evidence = evidence.Report(),
                Source = qualified.Configuration.SourceDescription,
                Recipe = ChdCdEncodingRecipe.Id,
                Build = typeof(ChdCdInteractiveReadChecks).Assembly.GetName().Version?.ToString(),
                AssemblySha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(ChdCdInteractiveReadChecks).Assembly.Location))),
                Scope = "Linux loopback Kestrel HTTPS with ephemeral certificate pinning, production Basic VFS connection issuance/authentication, post-admission credential recheck, real PostgreSQL live-library/catalog repository, default layout/planner/resolver/reader/native decoder. Catalog and published library rows are explicitly seeded from independently qualified originals, not imported/materialized through subscription jobs. CHD-only BIN backing; exact CUE/sidecars retained in CAS and separately rehashed for logical publication. Three users, one Original-format credential per user; two users perform bulk reads and the third interactive reads, under unchanged decoder and HTTP admission defaults. Fixed 500ms interactive arrivals, at most eight outstanding; missed arrivals and deadline failures remain in the denominator. Timed ranges exclude preloaded independent oracle IO but include real authentication, library queries, admission, all HTTP503 attempts/backoff, TLS and body reads. Fresh-decoder observations also use a fresh TLS client; warm observations reuse both. OS page cache is uncontrolled. No deployed-container, WAN, real-mount or gameplay qualification. No Automatic policy approval."
            }, JsonOptions.Value, ct);
            await report.FlushAsync(ct);
        }
        finally { reportGate.Release(); }
    }
}
