using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;
using ErrorOr;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Source.Rom;
using Romd.Application.Common.Content;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Libraries;
using Romd.Consumer.Application.Vfs;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Domain.Vfs;
using Romd.Host.Endpoints;
using Romd.Hosting.Vfs;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Storage;
using Romd.Media.Chd;
using Romd.Storage;

namespace Romd.Media.Probe;

/// <summary>Read-only sustained qualification over a caller-owned, fully converged disposable library.</summary>
internal enum ConvergedReadProfile { NintendoWholeFiles, CdMembers }

internal static class ConvergedReadChecks
{
    private const int FullBufferBytes = 256 * 1024;
    private const int RangeBytes = 64 * 1024;
    private const string MetadataHeader = "X-Romd-Probe-Metadata";
    private const string ClientHeader = "X-Romd-Probe-Client";
    private const string Scheme = "ConvergedReadProbe";
    private static readonly TimeSpan OperationDeadline = TimeSpan.FromMinutes(10);
    private static readonly Guid[] Users =
    [Guid.Parse("483d1203-7b2e-4361-a5a1-8e189fd0dbb8"), Guid.Parse("88197ca4-27fa-43b5-934d-fc90d7057d7b")];

    internal static async Task RunAsync(IContentAddressableStore cas, IRomContentCatalog catalog,
        IReadOnlyList<(RomContentDescriptor Content, string IsoPath, string SystemKey)> library,
        RomMediaComposition media, IRomChdDeliveryAdmission admission, ConvergedReadDecoder decoderRuntime, ConvergedReadProfile profile, TimeSpan duration, string resultsPath, CancellationToken ct, ReadResourceChecks.StagingScope? stagingScope = null)
    {
        ArgumentNullException.ThrowIfNull(decoderRuntime);
        await using var ownedDecoder = decoderRuntime;
        ArgumentNullException.ThrowIfNull(cas);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultsPath);
        if (duration < TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(duration), "Sustained qualification requires at least 30 minutes.");
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(admission);
        if (!Enum.IsDefined(profile))
            throw new ArgumentOutOfRangeException(nameof(profile));
        if (profile == ConvergedReadProfile.NintendoWholeFiles)
            Check.That(library.Any(item => item.SystemKey == "gc") && library.Any(item => item.SystemKey == "wii"),
                "the same converged library contains real GameCube and Wii discs");
        else
        {
            var cd = media.Kinds.Kinds.Single(kind => kind.ManifestType == MediaManifestType.CdTracks);
            Check.That(library.Count > 1 && library.All(item => cd.PlatformKeys.Contains(item.SystemKey, StringComparer.Ordinal)),
                "the qualified CD library contains multiple logical members on the registered CD platform");
        }
        Check.That(library.Select(item => item.Content.RomId).Distinct().Count() == library.Count,
            "converged library ROM identities are distinct");
        string reportPath = Path.GetFullPath(resultsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await using (var reserved = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            4096, FileOptions.Asynchronous))
            await reserved.FlushAsync(ct);
        var state = new ReadState(duration, library, profile) { NativePool = () => decoderRuntime.NativeSnapshot };
        var readOptions = decoderRuntime.Options;
        Check.That(readOptions.MaxConcurrent == 4 && readOptions.MaxIdle == 2 && readOptions.CacheBytes == 64L << 20,
            "qualification uses the production 4-active, 2-idle, 64-MiB defaults");
        var accessor = new HttpContextAccessor();
        using var meter = Listen(state);
        var decoder = decoderRuntime.Decoder;
        var countedDecoder = new CountingDecoder(decoder, state, accessor);
        var countedCas = new ForwardingStore(cas, state, accessor);
        var reader = new RomContentReader(catalog, countedCas, countedDecoder, admission);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var sampling = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task sample = Task.CompletedTask;
        try
        {
            Check.That(library.All(item => decoder.CanDecode(item.Content.Encoding)), "decoder composition supports every qualified library encoding");
            Check.That(profile != ConvergedReadProfile.CdMembers || decoderRuntime.NativeSnapshot is not null,
                "CD qualification owns the actual native decoder pool");
            var observedResources = ReadResourceChecks.Create(decoder, stagingScope);
            Check.That(!observedResources.IsError, "exact decoder/cache assembly shape is observable");
            var resources = observedResources.Value;
            state.Resources = resources;
            sample = resources.SampleUntilCancelledAsync(() =>
            {
                state.ObserveContentAdmission();
                return state.Phase;
            }, sampling.Token);
            await WriteAsync(state, resultsPath, ct);
            var entries = await PrepareAsync(catalog, library, media, admission, profile, state, ct);
            state.Entries = entries;
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Romd:Vfs:Enabled"] = "true",
                ["Romd:Vfs:AllowDevelopmentBearer"] = "true"
            });
            builder.Services.AddAuthentication(Scheme)
                .AddScheme<AuthenticationSchemeOptions, ProbeAuthentication>(Scheme, _ => { });
            builder.Services.AddSingleton<IHttpContextAccessor>(accessor);
            builder.Services.AddScoped<ICurrentUser, CurrentUser>();
            builder.Services.AddRomdConsumerVfs(builder.Configuration);
            builder.Services.AddAuthorization(options => options.AddPolicy(VfsBasicAuthenticationHandler.PolicyName,
                policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser()));
            builder.Services.RemoveAll<IVfsLayout>();
            builder.Services.AddSingleton<IVfsLayout>(new FlatLayout());
            var files = entries.ToDictionary(entry => entry.Path, entry => entry.File, StringComparer.Ordinal);
            builder.Services.AddSingleton<IConsumerVfsRepository>(new FixedRepository(files));
            builder.Services.AddSingleton<IConsumerVfsContentResolver>(
                new CountingResolver(new ConsumerVfsContentResolver(countedCas, reader), state, accessor));
            await using var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapConsumerVfsEndpoints();
            await app.StartAsync(ct);
            state.VfsOptions = app.Services.GetRequiredService<ConsumerVfsOptions>();
            state.ContentAdmission = app.Services.GetRequiredService<ConsumerVfsAdmission>();
            using var first = app.GetTestClient();
            using var second = app.GetTestClient();
            HttpClient[] clients = [first, second];
            for (int index = 0; index < clients.Length; index++)
            {
                clients[index].Timeout = Timeout.InfiniteTimeSpan;
                clients[index].DefaultRequestHeaders.Add(ClientHeader, index.ToString(CultureInfo.InvariantCulture));
            }
            state.Phase = "metadata-and-capacity";
            foreach (var entry in entries)
                for (int client = 0; client < clients.Length; client++)
                    await MetadataAsync(clients[client], entry, state, client, true, ct);
            await SaturationAsync(reader, cas, clients, entries, state, ct);
            await ControlledQueueAsync(app.Services.GetRequiredService<ConsumerVfsAdmission>(), async token =>
            {
                foreach (var entry in entries)
                    for (int client = 0; client < clients.Length; client++)
                        await MetadataAsync(clients[client], entry, state, client, true, token, ["head", "propfind"]);
            }, state, ct);
            state.Phase = "sustained-load";
            state.BeginLoad();
            Check.That(!resources.BeginLoad().IsError, "sustained resource baselines are observed");
            await WriteAsync(state, resultsPath, ct);
            var workers = state.Workers;
            for (int client = 0; client < clients.Length; client++)
            {
                int captured = client;
                foreach (RomDeliveryFormat format in entries.Select(entry => entry.Target.Format).Distinct())
                {
                    var selected = entries.Where(entry => entry.Target.Format == format).ToArray();
                    workers.Add(WorkerAsync($"http-{captured}-{format}-full", selected, state, run, $"client-{captured}/full",
                        (entry, sequence, token) => FullHttpAsync(clients[captured], entry, state, captured, token), ct));
                    workers.Add(WorkerAsync($"http-{captured}-{format}-ranges", selected, state, run, $"client-{captured}/ranges",
                        (entry, sequence, token) => RangesAsync(reader, cas, clients[captured], entry, state, captured,
                            sequence, token), ct));
                }
            }
            workers.Add(WorkerAsync("direct-mixed-full", entries, state, run, "direct/full",
                (entry, sequence, token) => FullDirectAsync(reader, entry, state, token), ct));
            workers.Add(WorkerAsync("direct-mixed-ranges", entries, state, run, "direct/ranges",
                (entry, sequence, token) => RangesAsync(reader, cas, null, entry, state, -1, sequence, token), ct));
            var work = Task.WhenAll(workers);
            var progress = ProgressAsync(work, state, resultsPath, run, ct);
            workers.Add(progress);
            await Task.WhenAll(work, progress);
            long settling = Stopwatch.GetTimestamp();
            while (Interlocked.Read(ref state.EndpointActive) != 0 && Stopwatch.GetElapsedTime(settling) < TimeSpan.FromSeconds(5))
                await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
            state.ObserveContentAdmission();
            Check.That(Interlocked.Read(ref state.EndpointActive) == 0 && Interlocked.Read(ref state.ContentActive) == 0 &&
                Interlocked.Read(ref state.ContentQueued) == 0 && Interlocked.Read(ref state.MetadataQueued) == 0,
                "actual endpoint activity, content admission and both queued lanes finish at zero");
            Check.That(Interlocked.Read(ref state.PeakEndpointActive) <= state.VfsOptions.MaximumConcurrentRequests +
                state.VfsOptions.MaximumConcurrentMetadataRequests, "actual endpoint activity respects the two default lane bounds");
            await sampling.CancelAsync();
            await sample;
            await decoderRuntime.DisposeAsync();
            if (profile == ConvergedReadProfile.CdMembers)
            {
                Check.That(decoderRuntime.NativeSnapshot is
                {
                    LiveHandles: 0, ActiveHandles: 0, IdleHandles: 0, Flights: 0,
                    FlightWaiters: 0, Artifacts: 0, Speculations: 0, OpeningHandles: 0
                } native && native.OpenedHandles == native.ClosedHandles &&
                    native.PeakLiveHandles <= readOptions.MaxConcurrent && native.PeakActiveHandles <= readOptions.MaxConcurrent,
                    "native handles, source artifacts and speculative flights joined within default pool bounds");
                state.Check("native-pool-joined-and-closed-within-production-defaults");
            }
            var terminalResources = resources.Sample(state.Phase);
            state.TerminalResourcesConfirmed = !terminalResources.IsError;
            Check.That(state.TerminalResourcesConfirmed, "final resources remain observable and within defaults");
            Check.That(state.CurrentDecoderLeases == 0 && state.CurrentBufferBytes == 0 && state.AllOperationsStopped && state.CleanupCompleted,
                "every admitted operation, decoder lease and probe buffer has settled");
            Check.That(resources.Snapshot().ActiveDecoderSlots == 0, "all actual decoder admission slots are returned");
            if (stagingScope is not null)
                Check.That(resources.ReadPhaseStagingUnchanged, "read-only load created no staging reservations or payload");
            Check.That(state.LoadElapsed >= duration, "requested sustained load duration completed");
            ValidateCompletedCoverage(entries, clients.Length, state);
            Check.That(state.MetadataCasOpens == 0 && state.MetadataDecoderOpens == 0 && state.MetadataResolverOpens == 0,
                "all marked metadata-only responses, including during load, never opened CAS, decoder or content resolver");
            state.Status = "passed";
            state.Phase = "complete";
            state.Check("every-operation-compared-to-independent-iso-or-retained-rvz");
            await WriteAsync(state, resultsPath, ct);
            await app.StopAsync(ct);
        }
        catch (Exception error)
        {
            await ReportFailureAsync(error, run, sampling, sample, state, resultsPath, ct);
            throw;
        }
    }

    private static async Task ReportFailureAsync(Exception error, CancellationTokenSource run,
        CancellationTokenSource sampling, Task sample, ReadState state, string resultsPath, CancellationToken ct)
    {
        await CancelForCleanupAsync(run, state, "read-workers-cancel", CancellationToken.None);
        foreach (var worker in state.Workers)
        {
            try
            { await worker; }
            catch (OperationCanceledException) when (run.IsCancellationRequested) { }
            catch (Exception secondary) when (ReferenceEquals(secondary, error)) { }
            catch (Exception secondary) { RecordCleanupFailure(state, "read-worker-join", secondary); }
        }
        await CancelForCleanupAsync(sampling, state, "read-sampler-cancel", CancellationToken.None);
        try
        { await sample; }
        catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
        catch (Exception secondary) { RecordCleanupFailure(state, "read-sampler-join", secondary); }
        try
        {
            long settling = Stopwatch.GetTimestamp();
            while (Interlocked.Read(ref state.EndpointActive) != 0 && Stopwatch.GetElapsedTime(settling) < TimeSpan.FromSeconds(5))
                await Task.Delay(TimeSpan.FromMilliseconds(10), CancellationToken.None);
            state.ObserveContentAdmission();
            bool decoderActive = false;
            state.TerminalResourcesConfirmed = false;
            if (state.Resources is { } resources)
            {
                var terminal = resources.Sample(state.Phase);
                state.TerminalResourcesConfirmed = !terminal.IsError;
                if (terminal.IsError)
                    state.CleanupFailed("terminal-resource-observation");
                else
                    decoderActive = terminal.Value.ActiveDecoderSlots != 0;
            }
            if (!state.AllOperationsStopped || state.CurrentDecoderLeases != 0 || state.CurrentBufferBytes != 0 ||
                Interlocked.Read(ref state.EndpointActive) != 0 || Interlocked.Read(ref state.ContentActive) != 0 ||
                Interlocked.Read(ref state.ContentQueued) != 0 || Interlocked.Read(ref state.MetadataQueued) != 0 || decoderActive)
                state.CleanupFailed("terminal-active-operations");
        }
        catch (Exception secondary) { RecordCleanupFailure(state, "terminal-observation", secondary); }
        state.Status = ct.IsCancellationRequested ? "cancelled" : "failed";
        state.Error = error.ToString();
        try
        { await WriteAsync(state, resultsPath, CancellationToken.None); }
        catch (Exception secondary) { RecordCleanupFailure(state, "terminal-report-writer", secondary); }
        if (!state.CleanupCompleted)
        {
            // Report failures may prevent persistence. Keep a closed, bounded diagnostic on
            // the primary error as well; an unusual exception Data implementation is not trusted.
            try
            { error.Data["ReadProbeCleanupFailurePhases"] = state.CleanupFailurePhases; }
            catch (Exception) { }
        }
        ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static async Task CancelForCleanupAsync(CancellationTokenSource source, ReadState state,
        string phase, CancellationToken ct)
    {
        try
        { await source.CancelAsync().WaitAsync(ct); }
        catch (Exception secondary) { RecordCleanupFailure(state, phase, secondary); }
    }

    private static void RecordCleanupFailure(ReadState state, string phase, Exception error)
    {
        string diagnostic = phase + "/" + error.GetType().Name;
        state.CleanupFailed(diagnostic);
        try
        { Console.Error.WriteLine("Read probe cleanup failure: " + diagnostic); }
        catch (Exception) { }
    }

    private static void ValidateCompletedCoverage(Entry[] entries, int clients, ReadState state)
    {
        foreach (var entry in entries)
        {
            Check.That(state.Completed($"direct/full/{entry.Path}") > 0 && state.Completed($"direct/ranges/{entry.Path}") > 0,
                "every disc and format completed direct full and range operations");
            for (int client = 0; client < clients; client++)
                Check.That(state.Completed($"client-{client}/full/{entry.Path}") > 0 &&
                    state.Completed($"client-{client}/ranges/{entry.Path}") > 0,
                    "each independent client completed both formats of every disc");
        }
    }

    private static async Task ControlledQueueAsync(ConsumerVfsAdmission admission,
        Func<CancellationToken, Task> metadata, ReadState state, CancellationToken ct)
    {
        state.Phase = "controlled-vfs-queue";
        state.ContentAdmission = admission;
        var held = new List<IDisposable>();
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var grantDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var waiters = new List<Task<IDisposable?>>();
        Exception? primaryFailure = null;
        try
        {
            for (int user = 0; user < Users.Length; user++)
                for (int index = 0; index < 4; index++)
                    held.Add(await admission.EnterAsync(Users[user], null, ct)
                        ?? throw new InvalidOperationException("Default VFS content admission was not available."));
            state.ObserveContentAdmission();
            {
                var task = admission.EnterAsync(Users[0], null, cancelled.Token);
                waiters.Add(task);
                Check.That(admission.QueuedRequestCount == 1 && !task.IsCompleted, "real content waiter is queued");
                await metadata(ct);
                await cancelled.CancelAsync();
                OperationCanceledException? cancellation = null;
                try
                { using var unexpected = await task; }
                catch (OperationCanceledException error) { cancellation = error; }
                waiters.Remove(task);
                Check.That(cancellation?.CancellationToken == cancelled.Token && admission.QueuedRequestCount == 0,
                    "queued caller cancellation preserves its token and removes the waiter");
            }
            state.Check("vfs-real-queue-cancellation-and-metadata-zero-opens");

            long started = Stopwatch.GetTimestamp();
            using (var timeout = await admission.EnterAsync(Users[1], null, ct))
                Check.That(timeout is null && Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(9) &&
                    Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30) && admission.QueuedRequestCount == 0,
                    "real queue timeout honors the unchanged ten-second production limit");
            state.Record("queue/default-ten-second-timeout", 0, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            state.Check("vfs-real-default-queue-timeout");

            for (int index = 0; index < 16; index++)
                waiters.Add(admission.EnterAsync(Users[0], null, pending.Token));
            Check.That(admission.QueuedRequestCount == 16 && waiters.All(task => !task.IsCompleted),
                "one connection's real sixteen-waiter default queue is full");
            using (var rejected = await admission.EnterAsync(Users[0], null, ct))
                Check.That(rejected is null && admission.QueuedRequestCount == 16, "queue-full rejection retains no extra waiter");
            await metadata(ct);
            await pending.CancelAsync();
            foreach (var task in waiters)
            {
                OperationCanceledException? cancellation = null;
                try
                { using var unexpected = await task; }
                catch (OperationCanceledException error) { cancellation = error; }
                Check.That(cancellation?.CancellationToken == pending.Token, "every cancelled content waiter settles with its requested token");
            }
            waiters.Clear();
            Check.That(admission.QueuedRequestCount == 0, "cancelled full queue leaves zero waiters");
            state.Check("vfs-real-queue-full-rejection-and-cancelled-waiter-join");

            grantDeadline.CancelAfter(TimeSpan.FromSeconds(30));
            var grant = admission.EnterAsync(Users[0], null, grantDeadline.Token);
            waiters.Add(grant);
            Check.That(admission.QueuedRequestCount == 1 && !grant.IsCompleted, "real grant remains pending while eight content leases are held");
            held[0].Dispose();
            held.RemoveAt(0);
            using var admitted = await grant;
            waiters.Remove(grant);
            Check.That(admitted is not null && admission.QueuedRequestCount == 0, "queued content receives capacity after owned release");
            state.Check("vfs-real-queued-grant-after-release");
        }
        catch (Exception error) { primaryFailure = error; throw; }
        finally
        {
            Exception? cleanupFailure = null;
            // Every pending task is cancelled and joined before held slots can grant an unobserved lease.
            foreach (var cancellation in new[] { pending, cancelled, grantDeadline })
            {
                try
                { await cancellation.CancelAsync(); }
                catch (Exception error) { cleanupFailure ??= error; }
            }
            foreach (var task in waiters)
            {
                try
                { (await task)?.Dispose(); }
                catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
                catch (Exception error) { cleanupFailure ??= error; }
            }
            foreach (var lease in held)
            {
                try
                { lease.Dispose(); }
                catch (Exception error) { cleanupFailure ??= error; }
            }
            try
            { state.ObserveContentAdmission(); }
            catch (Exception error) { cleanupFailure ??= error; }
            if (cleanupFailure is not null)
            {
                state.CleanupFailed("controlled-vfs-queue");
                if (primaryFailure is null)
                    ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }
        }
        Check.That(admission.QueuedRequestCount == 0, "controlled VFS phase leaves no queued request");
        // Reacquire the complete real default capacity to prove there is no hidden lease leak.
        var recovered = new List<IDisposable>();
        try
        {
            for (int user = 0; user < Users.Length; user++)
                for (int index = 0; index < 4; index++)
                    recovered.Add(await admission.EnterAsync(Users[user], null, ct)
                        ?? throw new InvalidOperationException("A controlled VFS content slot leaked."));
        }
        finally { foreach (var lease in recovered) lease.Dispose(); state.ObserveContentAdmission(); }
        state.Check("vfs-real-queue-phase-all-eight-slots-reusable");
    }

    private static async Task<Entry[]> PrepareAsync(IRomContentCatalog catalog,
        IReadOnlyList<(RomContentDescriptor Content, string IsoPath, string SystemKey)> library,
        RomMediaComposition media, IRomChdDeliveryAdmission admission, ConvergedReadProfile profile, ReadState state, CancellationToken ct)
    {
        var policies = media.Registry;
        var entries = new List<Entry>();
        foreach (var item in library)
        {
            if (profile == ConvergedReadProfile.NintendoWholeFiles)
                Check.That(item.SystemKey is "gc" or "wii", "only inspected GameCube and Wii fixtures enter Nintendo qualification");
            var current = await catalog.FindByRomIdAsync(item.Content.RomId, ct)
                ?? throw new InvalidOperationException("Converged ROM missing from the production catalog.");
            Check.That(current.ContentSha256 == item.Content.ContentSha256 && current.FileId == item.Content.FileId &&
                current.ArtifactSha256 == item.Content.ArtifactSha256 && current.ContentSize == item.Content.ContentSize,
                "caller descriptors agree with the current production catalog");
            Check.That(current.Artifacts is { Count: 1 } && current.Artifacts[0].IsPreferred && current.Artifacts[0].HasMeasuredEvidence &&
                current.ContentSize > 0 && current.ArtifactSize > 0, "one complete measured preferred graph is the sole retained artifact");
            if (profile == ConvergedReadProfile.NintendoWholeFiles)
                Check.That(current.Encoding == RomRepresentationEncoding.Rvz &&
                    policies.Classify(current.Preferred.Container, policies.ResolveObservedKind(current)) == RomRepresentationProfileClass.Canonical,
                    "Nintendo qualification retains only its canonical RVZ");
            else
            {
                var kind = MediaKindResolver.Resolve(media.Kinds, current, [item.SystemKey]).Unique;
                Check.That(kind is { ManifestType: MediaManifestType.CdTracks } cdKind && current.Encoding == RomRepresentationEncoding.Chd &&
                    current.Reconstruction is RomReconstruction.CdMember && media.Reconciliation[cdKind.Kind].MatchesParameters(current.Preferred.Container),
                    "CD qualification retains only canonical measured CHD-backed logical members");
            }
            using var deadline = Deadline(ct);
            await using (var iso = new FileStream(item.IsoPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await HashFullAsync(iso, current.ContentSha256, current.ContentSize, current.ContentMd5,
                    current.ContentSha1, state, "oracle/iso", deadline.Token);
            }
            var deliveryPolicy = media.DeliveryPolicy;
            var planned = RomDeliveryPlanner.Plan(current, Path.GetFileName(item.IsoPath), deliveryPolicy,
                MediaKindResolver.Resolve(deliveryPolicy.Kinds, current, [item.SystemKey]), admission).ToArray();
            Check.That(planned.Select(option => option.Format).Order().SequenceEqual(profile == ConvergedReadProfile.NintendoWholeFiles
                ? new[] { RomDeliveryFormat.Original, RomDeliveryFormat.Rvz }.Order() : [RomDeliveryFormat.Original]),
                "the closed qualification profile exposes exactly its expected delivery formats");
            foreach (var option in planned)
            {
                Check.That(option.State == RomDeliveryState.Available && option.Target is not null,
                    "both formats are available through the production delivery planner");
                var target = option.Target!;
                Check.That(RomDeliveryTargets.IsValid(target), "valid immutable delivery target");
                string extension = target.Format == RomDeliveryFormat.Original
                    ? profile == ConvergedReadProfile.NintendoWholeFiles ? ".iso" : ".bin" : deliveryPolicy.Formats.FileExtension(target.Format);
                Check.That(Path.GetExtension(target.Filename).Equals(extension, StringComparison.OrdinalIgnoreCase),
                    "production delivered filename extension matches the format");
                string path = $"{item.SystemKey}-{current.RomId}{extension}";
                var file = new ConsumerVfsFile(path, target.Sha256, target.SizeBytes,
                    target.Md5 ?? default, target.Sha1 ?? default, Target: target);
                entries.Add(new(target, Path.GetFullPath(item.IsoPath), item.SystemKey, path, file));
            }
        }
        state.Check(profile == ConvergedReadProfile.NintendoWholeFiles ? "production-catalog-single-canonical-artifact-and-two-delivery-targets"
            : "production-catalog-canonical-chd-member-original-targets");
        state.Check("read-only-original-oracles-match-logical-length-and-advertised-hashes");
        return entries.ToArray();
    }

    private static ConsumerLibraryReadResult<ConsumerVfsResolvedFile> Lookup(ConsumerLibraryScope scope,
        string path, IReadOnlyDictionary<string, ConsumerVfsFile> files, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Users.Contains(scope.UserId) && files.TryGetValue(path, out var file)
            ? new ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.Found(1,
                new(new(1, 1, "Converged probe", "Probe"), file, files.Keys.ToArray()))
            : new ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound(1);
    }

    private static async Task MetadataAsync(HttpClient client, Entry entry, ReadState state, int clientIndex,
        bool isolated, CancellationToken ct, IReadOnlyList<string>? kinds = null)
    {
        long beforeCas = Interlocked.Read(ref state.CasOpens), beforeDecoder = Interlocked.Read(ref state.DecoderOpens);
        long beforeResolver = Interlocked.Read(ref state.ResolverOpens);
        foreach (string kind in kinds ?? ["head", "propfind", "not-modified", "precondition", "eof"])
        {
            string operation = $"client-{clientIndex}/metadata-{kind}/{entry.Path}";
            state.StartOperation(operation);
            try
            {
                using var deadline = Deadline(ct);
                using var request = new HttpRequestMessage(kind switch
                {
                    "head" => HttpMethod.Head,
                    "propfind" => new HttpMethod("PROPFIND"),
                    _ => HttpMethod.Get
                }, Url(entry));
                request.Headers.Add(MetadataHeader, "1");
                if (kind == "head")
                    request.Headers.Add("Range", "bytes=0-15");
                if (kind == "not-modified")
                    request.Headers.Add("If-None-Match", Tag(entry));
                if (kind == "precondition")
                    request.Headers.Add("If-Match", "\"outdated-probe\"");
                if (kind == "eof")
                    request.Headers.Add("Range", $"bytes={entry.Target.SizeBytes}-");
                if (kind == "propfind")
                {
                    request.Headers.Add("Depth", "0");
                    request.Content = new StringContent("<D:propfind xmlns:D='DAV:' xmlns:oc='http://owncloud.org/ns'><D:prop><D:getcontentlength/><D:getetag/><oc:checksums/></D:prop></D:propfind>",
                        Encoding.UTF8, "application/xml");
                }
                long started = Stopwatch.GetTimestamp();
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                int expectedStatus = kind switch { "head" => 200, "propfind" => 207, "not-modified" => 304, "precondition" => 412, _ => 416 };
                Check.That((int)response.StatusCode == expectedStatus, $"metadata {kind} HTTP status");
                await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
                if (kind == "propfind")
                {
                    var xml = XDocument.Parse(await SmallTextAsync(body, state, deadline.Token));
                    Check.That(xml.Descendants(XName.Get("getcontentlength", "DAV:")).Single().Value ==
                        entry.Target.SizeBytes.ToString(CultureInfo.InvariantCulture), "PROPFIND delivered length");
                    Check.That(xml.Descendants(XName.Get("getetag", "DAV:")).Single().Value == Tag(entry), "PROPFIND delivered SHA-256");
                    string[] expected = new[] { entry.Target.Md5 is { } md5 ? "MD5:" + md5 : null,
                        entry.Target.Sha1 is { } sha1 ? "SHA1:" + sha1 : null }.OfType<string>().ToArray();
                    string[] actual = xml.Descendants(XName.Get("checksum", "http://owncloud.org/ns"))
                        .SelectMany(element => element.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
                    Check.That(actual.SequenceEqual(expected), "ownCloud checksums describe delivered bytes, unknown hashes absent");
                }
                else
                {
                    using var buffer = new BufferLease(state, 1);
                    Check.That(await body.ReadAsync(buffer.Bytes.AsMemory(0, 1), deadline.Token) == 0, "metadata response has zero body bytes");
                    if (kind == "head")
                        Check.That(response.Content.Headers.ContentLength == entry.Target.SizeBytes && response.Headers.ETag?.Tag == Tag(entry) &&
                            response.Content.Headers.ContentRange is null && response.Headers.AcceptRanges.Contains("bytes"),
                            "HEAD advertises full delivered length and ETag even with a Range header");
                    if (kind == "not-modified")
                        Check.That(response.Headers.ETag?.Tag == Tag(entry), "304 delivered ETag");
                    if (kind == "eof")
                        Check.That(response.Content.Headers.ContentRange?.Length == entry.Target.SizeBytes,
                        "416 records delivered full length");
                }
                state.Record(operation, 0, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                state.Check($"{kind}-delivered-metadata-and-body-boundary");
            }
            catch { state.FailOperation(operation); throw; }
            finally { state.EndOperation(operation); }
        }
        if (isolated)
            Check.That(beforeCas == Interlocked.Read(ref state.CasOpens) && beforeDecoder == Interlocked.Read(ref state.DecoderOpens) &&
                beforeResolver == Interlocked.Read(ref state.ResolverOpens), "isolated metadata never opens CAS, decoder or content resolver");
    }

    private static async Task SaturationAsync(RomContentReader reader, IContentAddressableStore cas,
        HttpClient[] clients, Entry[] entries, ReadState state, CancellationToken ct)
    {
        var original = entries.First(entry => entry.Target.Format == RomDeliveryFormat.Original);
        var held = new List<Stream>();
        using var queuedStop = Deadline(ct);
        Task<Stream?>? queued = null;
        Exception? primaryFailure = null;
        try
        {
            for (int index = 0; index < 4; index++)
                held.Add(await reader.OpenSeekableAsync(original.Target, ct)
                    ?? throw new InvalidOperationException("Held original stream unavailable."));
            state.SampleMemory();
            using (var deadline = Deadline(ct))
            using (var request = new HttpRequestMessage(HttpMethod.Get, Url(original)))
            {
                request.Headers.Add("Range", $"bytes=0-{RangeBytes - 1}");
                long started = Stopwatch.GetTimestamp();
                using var response = await clients[0].SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                Check.That(response.StatusCode == HttpStatusCode.ServiceUnavailable, "four held originals produce bounded HTTP 503");
                Check.That(response.Headers.RetryAfter?.Delta is { } retry && retry > TimeSpan.Zero && retry <= TimeSpan.FromSeconds(30),
                    "saturated HTTP response includes bounded Retry-After");
                Check.That(response.Headers.ETag is null && response.Content.Headers.ContentRange is null,
                    "capacity rejection does not retain successful file validators or ranges");
                await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
                using var buffer = new BufferLease(state, 1);
                Check.That(await body.ReadAsync(buffer.Bytes.AsMemory(0, 1), deadline.Token) == 0, "capacity response is bodyless");
                state.Record("saturation/http-503", 0, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                Interlocked.Increment(ref state.CapacityRejections);
            }
            foreach (var entry in entries)
                for (int client = 0; client < clients.Length; client++)
                    await MetadataAsync(clients[client], entry, state, client, true, ct);
            foreach (var entry in entries.Where(entry => entry.Target.Format == RomDeliveryFormat.Rvz))
                await RangesAsync(reader, cas, clients[1], entry, state, 1, 0, ct);
            state.Check("four-original-leases-saturated-http-503-retry-after");
            state.Check(entries.Any(entry => entry.Target.Format == RomDeliveryFormat.Rvz)
                ? "metadata-and-other-user-rvz-progress-while-original-decoder-full" : "metadata-progress-while-cd-decoder-full");
            long waiting = Stopwatch.GetTimestamp();
            bool capacity = false;
            try
            {
                await using var unexpected = await reader.OpenAsync(original.Target, ct, TimeSpan.FromMilliseconds(250));
            }
            catch (ResourceCapacityException) { capacity = true; }
            ct.ThrowIfCancellationRequested();
            double elapsed = Stopwatch.GetElapsedTime(waiting).TotalMilliseconds;
            Check.That(capacity && elapsed >= 200 && elapsed < 5000, "export-style waiting respects its explicit admission deadline");
            Interlocked.Increment(ref state.CapacityRejections);
            state.Record("saturation/export-admission-deadline", 0, elapsed);
            using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cancelled.CancelAfter(TimeSpan.FromMilliseconds(250));
                waiting = Stopwatch.GetTimestamp();
                bool observed = false;
                try
                {
                    await using var unexpected = await reader.OpenAsync(original.Target, cancelled.Token, TimeSpan.FromSeconds(30));
                }
                catch (OperationCanceledException) when (cancelled.IsCancellationRequested && !ct.IsCancellationRequested) { observed = true; }
                ct.ThrowIfCancellationRequested();
                elapsed = Stopwatch.GetElapsedTime(waiting).TotalMilliseconds;
                Check.That(observed && elapsed >= 200 && elapsed < 5000, "export-style waiting cancels without leaking a slot");
                state.Record("saturation/export-admission-cancellation", 0, elapsed);
            }
            using (var deadline = Deadline(ct))
            {
                waiting = Stopwatch.GetTimestamp();
                long decoderBefore = Interlocked.Read(ref state.DecoderOpens);
                queued = reader.OpenAsync(original.Target, queuedStop.Token, TimeSpan.FromSeconds(30));
                while (!queued.IsCompleted && Interlocked.Read(ref state.DecoderOpens) == decoderBefore &&
                    Stopwatch.GetElapsedTime(waiting) < TimeSpan.FromSeconds(5))
                    await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
                await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
                Check.That(!queued.IsCompleted && Interlocked.Read(ref state.DecoderOpens) > decoderBefore,
                    "export-style waiter reached decoder admission and remains pending while original capacity is full");
                await held[^1].DisposeAsync();
                held.RemoveAt(held.Count - 1);
                var granted = await queued;
                queued = null; // Ownership transfers to the lexical stream lease only after the task has settled.
                await using var admitted = granted ?? throw new InvalidOperationException("Queued original unavailable.");
                elapsed = Stopwatch.GetElapsedTime(waiting).TotalMilliseconds;
                using var expected = new BufferLease(state, RangeBytes);
                using var actual = new BufferLease(state, RangeBytes);
                await ReadExpectedAsync(cas, original, 0, expected.Bytes.AsMemory(0, RangeBytes), state, deadline.Token);
                await admitted.ReadExactlyAsync(actual.Bytes.AsMemory(0, RangeBytes), deadline.Token);
                Compare(expected.Bytes.AsSpan(0, RangeBytes), actual.Bytes.AsSpan(0, RangeBytes));
                state.Record("saturation/export-admission-granted-after-release", RangeBytes, elapsed);
            }
            state.Check("export-reader-wait-deadline-cancellation-and-grant-after-release");
        }
        catch (Exception error) { primaryFailure = error; throw; }
        finally
        {
            Exception? cleanupFailure = null;
            try
            { await queuedStop.CancelAsync(); }
            catch (Exception error) { cleanupFailure ??= error; }
            if (queued is not null)
            {
                try
                { if (await queued is { } stream) await stream.DisposeAsync(); }
                catch (OperationCanceledException) when (queuedStop.IsCancellationRequested) { }
                catch (Exception error) { cleanupFailure ??= error; }
            }
            foreach (var stream in held)
            {
                try
                { await stream.DisposeAsync(); }
                catch (Exception error) { cleanupFailure ??= error; }
            }
            if (cleanupFailure is not null)
            {
                state.CleanupFailed("decoder-saturation");
                if (primaryFailure is null)
                    ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }
        }
        await RangesAsync(reader, cas, clients[0], original, state, 0, 0, ct);
        state.Check("original-http-range-recovers-after-held-lease-release");
    }

    private static async Task WorkerAsync(string lane, Entry[] entries, ReadState state, CancellationTokenSource run,
        string operationPrefix, Func<Entry, long, CancellationToken, Task> operation, CancellationToken ct)
    {
        try
        {
            long sequence = 0;
            while (state.LoadElapsed < state.RequestedDuration)
            {
                ct.ThrowIfCancellationRequested();
                run.Token.ThrowIfCancellationRequested();
                var entry = entries[(int)(sequence % entries.Length)];
                using var deadline = Deadline(run.Token);
                string key = operationPrefix + "/" + entry.Path;
                string verifiedKey = operationPrefix.EndsWith("/ranges", StringComparison.Ordinal)
                    ? operationPrefix[..^1] + "/" + entry.Path : key;
                long beforeBytes = state.Bytes(verifiedKey), started = Stopwatch.GetTimestamp();
                state.StartLane(lane);
                bool completed = false, deferred = false;
                try
                {
                    await operation(entry, sequence, deadline.Token);
                    completed = true;
                }
                catch (HttpCapacityException error) when (error.Exhaustion is not null &&
                    !deadline.IsCancellationRequested && !run.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    deferred = true;
                    state.AdmissionDeferred(key, error, state.Bytes(verifiedKey) - beforeBytes,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                finally
                {
                    state.EndLane(lane, completed, state.Bytes(verifiedKey) - beforeBytes,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds, deferred);
                }
                sequence++;
                state.Lanes[lane] = sequence;
            }
        }
        catch (Exception error)
        {
            await CancelForCleanupAsync(run, state, "read-workers-cancel", CancellationToken.None);
            ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }
    }

    private static async Task FullDirectAsync(RomContentReader reader, Entry entry, ReadState state, CancellationToken ct)
    {
        string key = $"direct/full/{entry.Path}";
        long started = Stopwatch.GetTimestamp();
        state.StartOperation(key);
        try
        {
            await RetryAsync(async token =>
            {
                await using var stream = await reader.OpenAsync(entry.Target, token)
                    ?? throw new InvalidOperationException("Direct delivery target unavailable.");
                await HashFullAsync(stream, entry.Target.Sha256, entry.Target.SizeBytes, entry.Target.Md5,
                    entry.Target.Sha1, state, "delivered/direct", token);
            }, state, ct, key);
            state.Record(key, entry.Target.SizeBytes, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch { state.FailOperation(key); throw; }
        finally { state.EndOperation(key); }
    }

    private static async Task FullHttpAsync(HttpClient client, Entry entry, ReadState state, int clientIndex, CancellationToken ct)
    {
        string key = $"client-{clientIndex}/full/{entry.Path}";
        long started = Stopwatch.GetTimestamp();
        state.StartOperation(key);
        try
        {
            await RetryAsync(async token =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Url(entry));
                using var response = await SendContentAsync(client, request, state, token);
                Check.That(response.StatusCode == HttpStatusCode.OK, "full HTTP GET succeeds");
                Check.That(response.Content.Headers.ContentLength == entry.Target.SizeBytes && response.Headers.ETag?.Tag == Tag(entry),
                    "full HTTP delivered length and ETag");
                await using var body = await response.Content.ReadAsStreamAsync(token);
                await HashFullAsync(body, entry.Target.Sha256, entry.Target.SizeBytes, entry.Target.Md5,
                    entry.Target.Sha1, state, $"delivered/client-{clientIndex}", token);
            }, state, ct, key);
            state.Record(key, entry.Target.SizeBytes, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (HttpCapacityException error) when (error.Exhaustion is not null && !ct.IsCancellationRequested) { state.DeferOperation(key); throw; }
        catch { state.FailOperation(key); throw; }
        finally { state.EndOperation(key); }
    }

    private static async Task RangesAsync(RomContentReader reader, IContentAddressableStore cas, HttpClient? client,
        Entry entry, ReadState state, int clientIndex, long sequence, CancellationToken ct)
    {
        string lane = client is null ? "direct" : $"client-{clientIndex}";
        long batchBytes = 0;
        async Task ReadBatchAsync(CancellationToken token)
        {
            long bytes = 0;
            using var expected = new BufferLease(state, RangeBytes);
            using var actual = new BufferLease(state, RangeBytes);
            await using var direct = client is null ? await reader.OpenSeekableAsync(entry.Target, token) : null;
            if (client is null)
                Check.That(direct is not null && direct.CanSeek && direct.Length == entry.Target.SizeBytes,
                "direct seekable target length");
            long[] boundaries = [0, 32767, 65535, 131071, 2097151, 0x3ffff, 0xf7fffff,
                Math.Max(0, entry.Target.SizeBytes - RangeBytes), entry.Target.SizeBytes - 1, entry.Target.SizeBytes];
            var random = new Random(unchecked((int)(sequence * 104729 + entry.Target.RomId * 6151 + clientIndex * 41)));
            long[] offsets = boundaries.Where(offset => offset >= 0 && offset <= entry.Target.SizeBytes)
                .Concat(Enumerable.Range(0, 8).Select(_ => random.NextInt64(entry.Target.SizeBytes))).Distinct().ToArray();
            foreach (long offset in offsets)
            {
                int wanted = (int)Math.Min(RangeBytes, entry.Target.SizeBytes - offset);
                await ReadExpectedAsync(cas, entry, offset, expected.Bytes.AsMemory(0, wanted), state, token);
                string rangeKey = $"{lane}/range/{entry.Path}";
                long rangeStarted = Stopwatch.GetTimestamp();
                state.StartOperation(rangeKey);
                try
                {
                    if (client is null)
                    {
                        direct!.Seek(offset, SeekOrigin.Begin);
                        if (wanted > 0)
                            await direct.ReadExactlyAsync(actual.Bytes.AsMemory(0, wanted), token);
                        if (offset + wanted == entry.Target.SizeBytes)
                        {
                            using var eof = new BufferLease(state, 1);
                            Check.That(await direct.ReadAsync(eof.Bytes.AsMemory(0, 1), token) == 0, "direct range reaches exact EOF");
                        }
                    }
                    else
                    {
                        await RetryAsync(async requestToken =>
                        {
                            using var request = new HttpRequestMessage(HttpMethod.Get, Url(entry));
                            request.Headers.Add("Range", $"bytes={offset}-{offset + RangeBytes - 1}");
                            if (wanted == 0)
                                request.Headers.Add(MetadataHeader, "1");
                            using var response = await SendContentAsync(client, request, state, requestToken);
                            Check.That(response.StatusCode == (wanted == 0 ? HttpStatusCode.RequestedRangeNotSatisfiable : HttpStatusCode.PartialContent),
                                "deterministic HTTP range status");
                            Check.That(response.Content.Headers.ContentRange?.Length == entry.Target.SizeBytes, "HTTP range full delivered length");
                            if (wanted > 0)
                                Check.That(response.Content.Headers.ContentLength == wanted && response.Content.Headers.ContentRange?.From == offset &&
                                    response.Content.Headers.ContentRange?.To == offset + wanted - 1 && response.Headers.ETag?.Tag == Tag(entry),
                                    "HTTP range bounds, ETag and clipped length");
                            await using var body = await response.Content.ReadAsStreamAsync(requestToken);
                            if (wanted > 0)
                                await body.ReadExactlyAsync(actual.Bytes.AsMemory(0, wanted), requestToken);
                            using var eof = new BufferLease(state, 1);
                            Check.That(await body.ReadAsync(eof.Bytes.AsMemory(0, 1), requestToken) == 0, "HTTP range has exact body length and EOF");
                        }, state, token, $"{lane}/range/{entry.Path}");
                    }
                    Compare(expected.Bytes.AsSpan(0, wanted), actual.Bytes.AsSpan(0, wanted));
                    state.Record($"{lane}/range/{entry.Path}", wanted, Stopwatch.GetElapsedTime(rangeStarted).TotalMilliseconds);
                    bytes += wanted;
                    state.AddFlow($"delivered/{lane}", wanted);
                }
                catch (HttpCapacityException error) when (error.Exhaustion is not null && !token.IsCancellationRequested) { state.DeferOperation(rangeKey); throw; }
                catch { state.FailOperation(rangeKey); throw; }
                finally { state.EndOperation(rangeKey); }
            }
            batchBytes = bytes;
        }
        string batchKey = $"{lane}/ranges/{entry.Path}";
        long batchStarted = Stopwatch.GetTimestamp();
        state.StartOperation(batchKey);
        try
        {
            if (client is null)
                await RetryAsync(ReadBatchAsync, state, ct, batchKey);
            else
                await ReadBatchAsync(ct);
            state.Record(batchKey, batchBytes, Stopwatch.GetElapsedTime(batchStarted).TotalMilliseconds);
        }
        catch (HttpCapacityException error) when (error.Exhaustion is not null && !ct.IsCancellationRequested) { state.DeferOperation(batchKey); throw; }
        catch { state.FailOperation(batchKey); throw; }
        finally { state.EndOperation(batchKey); }
        if (client is not null && sequence % 16 == 0)
            await MetadataAsync(client, entry, state, clientIndex, false, ct);
    }

    private static async Task ReadExpectedAsync(IContentAddressableStore cas, Entry entry, long offset,
        Memory<byte> expected, ReadState state, CancellationToken ct)
    {
        if (entry.Target.Format == RomDeliveryFormat.Original)
        {
            using var handle = File.OpenHandle(entry.IsoPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
            int received = 0;
            while (received < expected.Length)
            {
                int read = await RandomAccess.ReadAsync(handle, expected[received..], offset + received, ct);
                Check.That(read > 0, "ISO oracle contains every expected range byte");
                received += read;
            }
            if (offset == entry.Target.SizeBytes)
            {
                using var eof = new BufferLease(state, 1);
                Check.That(await RandomAccess.ReadAsync(handle, eof.Bytes.AsMemory(0, 1), offset, ct) == 0, "ISO oracle EOF");
            }
            state.AddFlow("oracle/iso-range", expected.Length);
        }
        else
        {
            await using var raw = await cas.OpenRandomAccessAsync(StorageKey.FromHash(entry.Target.ArtifactSha256), ct)
                ?? throw new InvalidOperationException("Retained RVZ oracle unavailable.");
            Interlocked.Increment(ref state.OracleCasOpens);
            Check.That(raw.Length == entry.Target.SizeBytes, "raw retained RVZ oracle length");
            int received = 0;
            while (received < expected.Length)
            {
                int read = await raw.ReadAtAsync(offset + received, expected[received..], ct);
                Check.That(read > 0, "raw retained RVZ oracle contains every expected range byte");
                received += read;
            }
            if (offset == entry.Target.SizeBytes)
            {
                using var eof = new BufferLease(state, 1);
                Check.That(await raw.ReadAtAsync(offset, eof.Bytes.AsMemory(0, 1), ct) == 0, "retained RVZ oracle EOF");
            }
            state.AddFlow("oracle/rvz-range", expected.Length);
        }
    }

    private static async Task HashFullAsync(Stream source, Sha256 sha256, long size, Md5? md5, Sha1? sha1,
        ReadState state, string flow, CancellationToken ct)
    {
        using var buffer = new BufferLease(state, FullBufferBytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5Hash = md5.HasValue ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null;
        using var sha1Hash = sha1.HasValue ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : null;
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer.Bytes.AsMemory(0, FullBufferBytes), ct);
            if (read == 0)
                break;
            total += read;
            Check.That(total <= size, "full stream never exceeds delivered metadata length");
            hash.AppendData(buffer.Bytes, 0, read);
            md5Hash?.AppendData(buffer.Bytes, 0, read);
            sha1Hash?.AppendData(buffer.Bytes, 0, read);
            state.AddFlow(flow, read);
        }
        Check.That(total == size && Convert.ToHexStringLower(hash.GetHashAndReset()) == sha256.ToString(),
            "complete delivered stream has exact SHA-256 and length through EOF");
        if (md5Hash is not null)
            Check.That(Convert.ToHexStringLower(md5Hash.GetHashAndReset()) == md5!.Value.ToString(),
            "advertised delivered MD5 matches full stream");
        if (sha1Hash is not null)
            Check.That(Convert.ToHexStringLower(sha1Hash.GetHashAndReset()) == sha1!.Value.ToString(),
            "advertised delivered SHA-1 matches full stream");
    }

    private static void Compare(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        Span<byte> expectedHash = stackalloc byte[32], actualHash = stackalloc byte[32];
        SHA256.HashData(expected, expectedHash);
        SHA256.HashData(actual, actualHash);
        Check.That(expected.SequenceEqual(actual) && expectedHash.SequenceEqual(actualHash),
            "every range equals oracle bytes and SHA-256");
    }

    private static async Task<HttpResponseMessage> SendContentAsync(HttpClient client, HttpRequestMessage request,
        ReadState state, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        state.HeadersLatency.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
            return response;
        TimeSpan? retry = response.Headers.RetryAfter?.Delta;
        using (response)
        {
            Check.That(retry is { } wait && wait > TimeSpan.Zero && wait <= TimeSpan.FromSeconds(30),
                "every HTTP capacity rejection includes a bounded Retry-After");
            Check.That(response.Headers.ETag is null && response.Content.Headers.ContentRange is null &&
                response.Content.Headers.ContentLength is null or 0, "pre-body capacity rejection has no successful delivery metadata");
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new BufferLease(state, 1);
            Check.That(await body.ReadAsync(buffer.Bytes.AsMemory(0, 1), ct) == 0, "pre-body capacity response is bodyless");
        }
        long? offset = request.Headers.Range?.Ranges.Count == 1 ? request.Headers.Range.Ranges.Single().From : null;
        throw new HttpCapacityException(retry!.Value, offset);
    }

    private static async Task RetryAsync(Func<CancellationToken, Task> operation, ReadState state, CancellationToken ct,
        string context)
    {
        long started = Stopwatch.GetTimestamp();
        int retries = 0;
        bool onlyHttpRejections = true;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            long attemptStarted = Stopwatch.GetTimestamp();
            long attemptEnded = 0;
            state.RetryAttempt(context);
            try
            { await operation(ct); return; }
            catch (Exception error) when (error is ResourceCapacityException or HttpCapacityException)
            {
                attemptEnded = Stopwatch.GetTimestamp();
                Interlocked.Increment(ref state.CapacityRejections);
                state.RetryRejected(context, error is HttpCapacityException);
                onlyHttpRejections &= error is HttpCapacityException;
                if (++retries > 120 || Stopwatch.GetElapsedTime(started) > TimeSpan.FromMinutes(2))
                {
                    ct.ThrowIfCancellationRequested();
                    if (onlyHttpRejections && error is HttpCapacityException exhausted)
                        exhausted.MarkExhausted(context, retries, retries - 1, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    throw;
                }
                Interlocked.Increment(ref state.CapacityRetries);
                var delay = error is HttpCapacityException http ? http.RetryAfter
                    : TimeSpan.FromMilliseconds(Math.Min(1000, 25 * retries));
                state.RetryBackoffMilliseconds.Add(delay.TotalMilliseconds);
                long waitStarted = Stopwatch.GetTimestamp();
                try
                { await Task.Delay(delay, ct); }
                finally { state.RetryWait(context, delay.TotalMilliseconds, Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds); }
            }
            finally
            {
                state.RetryAttemptElapsed(context,
                Stopwatch.GetElapsedTime(attemptStarted, attemptEnded == 0 ? Stopwatch.GetTimestamp() : attemptEnded).TotalMilliseconds);
            }
        }
    }

    private static async Task<string> SmallTextAsync(Stream body, ReadState state, CancellationToken ct)
    {
        const int maximum = 16 * 1024;
        using var buffer = new BufferLease(state, maximum + 1);
        int received = 0;
        while (received <= maximum)
        {
            int read = await body.ReadAsync(buffer.Bytes.AsMemory(received, maximum + 1 - received), ct);
            if (read == 0)
                return Encoding.UTF8.GetString(buffer.Bytes, 0, received);
            received += read;
        }
        throw new InvalidDataException("Metadata response exceeded the bounded XML buffer.");
    }

    private static async Task<IRandomAccessContent?> OpenCountedAsync(IContentAddressableStore cas, StorageKey key,
        ReadState state, IHttpContextAccessor accessor, CancellationToken ct)
    {
        Interlocked.Increment(ref state.CasOpens);
        if (Marked(accessor))
            Interlocked.Increment(ref state.MetadataCasOpens);
        var content = await cas.OpenRandomAccessAsync(key, ct);
        return content is null ? null : new CountingContent(content, state);
    }

    private static MeterListener Listen(ReadState state)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observed) =>
        {
            if (instrument.Meter.Name == "Romd.Vfs")
                observed.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, context) =>
        {
            if (instrument.Name == "romd.vfs.queue_wait")
                state.VfsQueueWait.Add(value * 1000);
        });
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, context) =>
        {
            if (instrument.Name == "romd.vfs.response_bytes")
                Interlocked.Add(ref state.HttpResponseBytes, value);
            string? lane = null, reason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "lane")
                    lane = tag.Value as string;
                if (tag.Key == "reason")
                    reason = tag.Value as string;
            }
            state.AdmissionMeasurement(instrument.Name, lane, reason, value);
        });
        listener.Start();
        return listener;
    }

    private static async Task ProgressAsync(Task work, ReadState state, string resultsPath,
        CancellationTokenSource run, CancellationToken ct)
    {
        try
        {
            while (!work.IsCompleted)
            {
                await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(15), run.Token));
                ct.ThrowIfCancellationRequested();
                run.Token.ThrowIfCancellationRequested();
                await WriteAsync(state, resultsPath, ct);
            }
        }
        catch (Exception error)
        {
            await CancelForCleanupAsync(run, state, "read-workers-cancel", CancellationToken.None);
            ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }
    }

    private static async Task WriteAsync(ReadState state, string path, CancellationToken ct)
    {
        state.SampleMemory();
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".next";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(output, state.Report(), JsonOptions.Value, ct);
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static CancellationTokenSource Deadline(CancellationToken ct)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(OperationDeadline);
        return deadline;
    }

    private static bool Marked(IHttpContextAccessor accessor) => accessor.HttpContext?.Request.Headers[MetadataHeader].ToString() == "1";
    private static string Url(Entry entry) => "/vfs/converged/" + Uri.EscapeDataString(entry.Path);
    private static string Tag(Entry entry) => "\"" + entry.Target.Sha256 + "\"";

    private sealed record Entry(RomDeliveryTarget Target, string IsoPath, string SystemKey, string Path, ConsumerVfsFile File);
    private sealed record HttpExhaustion(string Context, int Attempts, int Retries, double ElapsedMilliseconds);
    private sealed class HttpCapacityException(TimeSpan retryAfter, long? rangeOffset) : Exception("HTTP capacity is temporarily occupied.")
    {
        public TimeSpan RetryAfter { get; } = retryAfter;
        public long? RangeOffset { get; } = rangeOffset;
        public HttpExhaustion? Exhaustion { get; private set; }
        public void MarkExhausted(string context, int attempts, int retries, double elapsedMilliseconds) =>
            Exhaustion = new(context, attempts, retries, elapsedMilliseconds);
    }

    private sealed class ProbeAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            Context.RequestAborted.ThrowIfCancellationRequested();
            if (!int.TryParse(Request.Headers[ClientHeader].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index < 0 || index >= Users.Length)
                return Task.FromResult(AuthenticateResult.NoResult());
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Users[index].ToString())],
                    ConvergedReadChecks.Scheme)), ConvergedReadChecks.Scheme)));
        }
    }

    private sealed class FlatLayout : IVfsLayout
    {
        public string Key => "converged";
        public ConsumerVfsFileLocator? LocateFile(string path) => new(1, path);
        public string? FilePath(ConsumerVfsResolvedFile file) => file.File.RelativePath;
        public Task<ErrorOr<VfsLayoutRead>> ReadAsync(ConsumerLibraryScope scope, string path, bool children, int maximum,
            CancellationToken ct = default, VfsFormat format = VfsFormat.Stored)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<ErrorOr<VfsLayoutRead>>(ConsumerVfsErrors.NotFound());
        }
    }

    // The caller owns the real CAS. This bounded observer keeps no invocation history.
    private sealed class ForwardingStore(IContentAddressableStore inner, ReadState state, IHttpContextAccessor accessor)
        : IContentAddressableStore
    {
        public Task<IStagedContent> StageAsync(Stream content, StorageKey? expectedKey = null,
            IProgress<StoreProgress>? progress = null, CancellationToken ct = default) => inner.StageAsync(content, expectedKey, progress, ct);
        public Task<StoreResult> StoreAsync(Stream content, IProgress<StoreProgress>? progress = null,
            CancellationToken ct = default) => inner.StoreAsync(content, progress, ct);
        public Task<Stream?> RetrieveAsync(StorageKey key, CancellationToken ct = default) => inner.RetrieveAsync(key, ct);
        public Task<bool> VerifyAsync(StorageKey key, CancellationToken ct = default) => inner.VerifyAsync(key, ct);
        public Task<int> CleanupTemporaryFilesAsync(CancellationToken ct = default) => inner.CleanupTemporaryFilesAsync(ct);
        public Task<IRandomAccessContent?> OpenRandomAccessAsync(StorageKey key, CancellationToken ct = default) =>
            OpenCountedAsync(inner, key, state, accessor, ct);
        public ISynchronousRawContent? OpenSynchronousRaw(StorageKey key)
        {
            Interlocked.Increment(ref state.CasOpens);
            if (Marked(accessor))
                Interlocked.Increment(ref state.MetadataCasOpens);
            return inner.OpenSynchronousRaw(key);
        }
        public Task<bool> ExistsAsync(StorageKey key, CancellationToken ct = default) => inner.ExistsAsync(key, ct);
        public IAsyncEnumerable<StorageKey> EnumerateAsync(CancellationToken ct = default) => inner.EnumerateAsync(ct);
        public Task<bool> DeleteAsync(StorageKey key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
        public Task<ContentInfo?> GetInfoAsync(StorageKey key, CancellationToken ct = default) => inner.GetInfoAsync(key, ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedRepository : IConsumerVfsRepository
    {
        private readonly IReadOnlyDictionary<string, ConsumerVfsFile> _files;
        public FixedRepository(IReadOnlyDictionary<string, ConsumerVfsFile> files) =>
            _files = new Dictionary<string, ConsumerVfsFile>(files, StringComparer.Ordinal);
        public Task<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>> GetFileAsync(ConsumerLibraryScope scope,
            int releaseId, string relativePath, CancellationToken ct = default, VfsFormat format = VfsFormat.Stored)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(releaseId == 1 && format is VfsFormat.Stored or VfsFormat.Original
                ? Lookup(scope, relativePath, _files, ct)
                : new ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound(1));
        }
        public Task<ConsumerLibraryReadResult<ConsumerVfsPlatformPage>> ListPlatformsAsync(ConsumerLibraryScope scope,
            int afterPlatformId = 0, int limit = 100, CancellationToken ct = default) => Unsupported<ConsumerVfsPlatformPage>(ct);
        public Task<ConsumerLibraryReadResult<ConsumerVfsPlatform>> GetPlatformAsync(ConsumerLibraryScope scope,
            int platformId, CancellationToken ct = default) => Unsupported<ConsumerVfsPlatform>(ct);
        public Task<ConsumerLibraryReadResult<ConsumerVfsReleasePage>> ListReleasesAsync(ConsumerLibraryScope scope,
            int platformId, int afterReleaseId = 0, int limit = 100, CancellationToken ct = default,
            VfsFormat format = VfsFormat.Stored) => Unsupported<ConsumerVfsReleasePage>(ct);
        public Task<ConsumerLibraryReadResult<ConsumerVfsRelease>> GetReleaseAsync(ConsumerLibraryScope scope,
            int releaseId, CancellationToken ct = default, VfsFormat format = VfsFormat.Stored) => Unsupported<ConsumerVfsRelease>(ct);
        private static Task<ConsumerLibraryReadResult<T>> Unsupported<T>(CancellationToken ct) where T : notnull
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<ConsumerLibraryReadResult<T>>(new ConsumerLibraryReadResult<T>.Rejected(
                Error.Failure("ReadProbe.UnsupportedRepositoryOperation", "The flat qualification lookup does not implement this operation.")));
        }
    }

    private sealed class CountingResolver(ConsumerVfsContentResolver inner, ReadState state, IHttpContextAccessor accessor)
        : IConsumerVfsContentResolver
    {
        public Task<Stream?> OpenAsync(ConsumerVfsFile file, CancellationToken ct = default)
        {
            Interlocked.Increment(ref state.ResolverOpens);
            if (Marked(accessor))
                Interlocked.Increment(ref state.MetadataResolverOpens);
            return inner.OpenAsync(file, ct);
        }
    }

    private sealed class CountingDecoder(IRomRepresentationDecoder inner, ReadState state, IHttpContextAccessor accessor)
        : IRomRepresentationDecoder
    {
        public bool CanDecode(RomRepresentationEncoding encoding) => inner.CanDecode(encoding);

        public async Task<IRandomAccessContent> DecodeAsync(RomRepresentationEncoding encoding, IRandomAccessContent source,
            CancellationToken ct = default, Sha256? artifact = null, TimeSpan? admissionTimeout = null)
        {
            Interlocked.Increment(ref state.DecoderOpens);
            if (Marked(accessor))
                Interlocked.Increment(ref state.MetadataDecoderOpens);
            long started = Stopwatch.GetTimestamp();
            try
            {
                var content = await inner.DecodeAsync(encoding, source, ct, artifact, admissionTimeout);
                if (encoding == RomRepresentationEncoding.Identity)
                    return content;
                state.DecoderLeaseDelta(1);
                return new DecoderLease(content, state);
            }
            finally { state.DecoderOpenLatency.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
        }

        public async Task<IRandomAccessContent> DecodeAsync(RomRepresentationEncoding encoding, RomArtifactSource source,
            RomMediaOpenContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref state.DecoderOpens);
            if (Marked(accessor))
                Interlocked.Increment(ref state.MetadataDecoderOpens);
            long started = Stopwatch.GetTimestamp();
            try
            {
                var content = await inner.DecodeAsync(encoding, source, context, ct);
                if (encoding == RomRepresentationEncoding.Identity)
                    return content;
                state.DecoderLeaseDelta(1);
                return new DecoderLease(content, state);
            }
            finally { state.DecoderOpenLatency.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
        }
    }

    private sealed class DecoderLease(IRandomAccessContent inner, ReadState state) : IRandomAccessContent
    {
        private int _disposed;
        public long Length => inner.Length;
        public ContentReadIntegrity Integrity => inner.Integrity;
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default) =>
            inner.ReadAtAsync(offset, destination, ct);
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try
            { await inner.DisposeAsync(); }
            finally { state.DecoderLeaseDelta(-1); }
        }
    }

    private sealed class CountingContent(IRandomAccessContent inner, ReadState state) : IRandomAccessContent
    {
        public long Length => inner.Length;
        public ContentReadIntegrity Integrity => inner.Integrity;
        public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            int read = await inner.ReadAtAsync(offset, destination, ct);
            state.AddFlow("cas/encoded-artifact-read", read);
            return read;
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class BufferLease : IDisposable
    {
        private readonly ReadState _state;
        private bool _disposed;
        public byte[] Bytes { get; }
        public BufferLease(ReadState state, int size)
        {
            _state = state;
            Bytes = ArrayPool<byte>.Shared.Rent(size);
            state.BufferDelta(Bytes.Length);
        }
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _state.BufferDelta(-Bytes.Length);
            ArrayPool<byte>.Shared.Return(Bytes);
        }
    }

    private sealed class ReadState(TimeSpan duration,
        IReadOnlyList<(RomContentDescriptor Content, string IsoPath, string SystemKey)> library, ConvergedReadProfile profile)
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private long _loadStarted, _peakWorkingSet, _liveBuffers, _peakBuffers, _decoderLeases, _peakDecoderLeases;
        private readonly ConcurrentDictionary<string, Metric> _operations = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, RetryMetric> _retries = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, RetryMetric> _phaseRetries = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, LaneMetric> _lanes = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DeferralMetric> _deferrals = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> _flow = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> _checks = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _cleanupFailures = new(StringComparer.Ordinal);
        private Dictionary<string, long> _loadFlowBaseline = new(StringComparer.Ordinal);
        private Dictionary<string, long> _loadCompletedBaseline = new(StringComparer.Ordinal);
        public TimeSpan RequestedDuration { get; } = duration;
        public Entry[] Entries { get; set; } = [];
        public ConsumerVfsOptions? VfsOptions { get; set; }
        public ConsumerVfsAdmission? ContentAdmission { get; set; }
        public ReadResourceChecks? Resources { get; set; }
        public Func<ChdReadPoolSnapshot?>? NativePool { get; init; }
        public List<Task> Workers { get; } = [];
        public bool TerminalResourcesConfirmed { get; set; }
        public string Status { get; set; } = "in-progress";
        public string Phase { get; set; } = "iso-oracle-and-production-catalog";
        public string? Error { get; set; }
        public ConcurrentDictionary<string, long> Lanes { get; } = new(StringComparer.Ordinal);
        public Latency DecoderOpenLatency { get; } = new();
        public Latency HeadersLatency { get; } = new();
        public Latency VfsQueueWait { get; } = new();
        public Latency RetryBackoffMilliseconds { get; } = new();
        public long ContentActive, ContentQueued, PeakContentActive, PeakContentQueued,
            MetadataQueued, PeakMetadataQueued, EndpointActive, PeakEndpointActive, ContentAdmissionSamples,
            QueueFullRejections, QueueTimeoutRejections;
        public long CasOpens, OracleCasOpens, DecoderOpens, ResolverOpens, MetadataCasOpens,
            MetadataDecoderOpens, MetadataResolverOpens, CapacityRejections, CapacityRetries, HttpResponseBytes;
        public TimeSpan LoadElapsed => _loadStarted == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_loadStarted);
        public long CurrentBufferBytes => Interlocked.Read(ref _liveBuffers);
        public long CurrentDecoderLeases => Interlocked.Read(ref _decoderLeases);
        public bool AllOperationsStopped => _operations.Values.All(metric => metric.InFlight == 0) &&
            _lanes.Values.All(lane => lane.InFlight == 0);
        public void BeginLoad()
        {
            _loadFlowBaseline = _flow.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            _loadCompletedBaseline = _operations.ToDictionary(pair => pair.Key, pair => pair.Value.Count, StringComparer.Ordinal);
            _loadStarted = Stopwatch.GetTimestamp();
            foreach (var metric in _operations.Values)
                metric.BeginLoad();
        }
        public void Check(string name) => _checks.AddOrUpdate(name, 1, (_, count) => count + 1);
        public void CleanupFailed(string phase) => _cleanupFailures.TryAdd(phase, 0);
        public bool CleanupCompleted => _cleanupFailures.IsEmpty;
        public string[] CleanupFailurePhases => _cleanupFailures.Keys.ToArray();
        public void AddFlow(string lane, long bytes) => _flow.AddOrUpdate(lane, bytes, (_, count) => count + bytes);
        private Metric Operation(string name) => _operations.GetOrAdd(name, _ => new(_loadStarted != 0));
        public void Record(string name, long bytes, double milliseconds)
        {
            global::Check.That(milliseconds <= OperationDeadline.TotalMilliseconds, "cooperative operation completed within its configured deadline");
            Operation(name).Add(bytes, milliseconds);
        }
        public long Bytes(string name) => _operations.TryGetValue(name, out var metric) ? metric.Bytes : 0;
        public void StartOperation(string name) => Operation(name).Start();
        public void FailOperation(string name) => Operation(name).Fail();
        public void DeferOperation(string name) => Operation(name).Defer();
        public void EndOperation(string name) => Operation(name).End();
        public void StartLane(string name) => _lanes.GetOrAdd(name, _ => new()).Start();
        public void EndLane(string name, bool completed, long bytes, double milliseconds, bool deferred = false) =>
            _lanes[name].Finish(completed, deferred, bytes, milliseconds);
        public void AdmissionDeferred(string context, HttpCapacityException error, long verifiedBytes, double milliseconds) =>
            _deferrals.GetOrAdd(context, _ => new()).Add(error.Exhaustion!, error.RangeOffset, verifiedBytes, milliseconds);
        private RetryMetric Retry(string context) => _retries.GetOrAdd(context, _ => new());
        private RetryMetric PhaseRetry(string context) => _phaseRetries.GetOrAdd(Phase + "/" + context, _ => new());
        public void RetryAttempt(string context) { Retry(context).Attempt(); PhaseRetry(context).Attempt(); }
        public void RetryRejected(string context, bool http) { Retry(context).Reject(http); PhaseRetry(context).Reject(http); }
        public void RetryWait(string context, double scheduled, double observed)
        {
            Retry(context).Wait(scheduled, observed);
            PhaseRetry(context).Wait(scheduled, observed);
        }
        public void RetryAttemptElapsed(string context, double milliseconds)
        {
            Retry(context).AttemptElapsed.Add(milliseconds);
            PhaseRetry(context).AttemptElapsed.Add(milliseconds);
        }
        public long Completed(string name) => _operations.TryGetValue(name, out var metric)
            ? metric.Count - _loadCompletedBaseline.GetValueOrDefault(name) : 0;
        public void SampleMemory()
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            Peak(ref _peakWorkingSet, Math.Max(process.WorkingSet64, process.PeakWorkingSet64));
        }
        public void BufferDelta(int bytes) => Peak(ref _peakBuffers, Interlocked.Add(ref _liveBuffers, bytes));
        public void DecoderLeaseDelta(int count) => Peak(ref _peakDecoderLeases, Interlocked.Add(ref _decoderLeases, count));
        public void ObserveContentAdmission()
        {
            if (ContentAdmission is not { } admission)
                return;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(ConsumerVfsAdmission);
            var gate = type.GetField("_gate", flags);
            var active = type.GetField("_active", flags);
            var options = type.GetField("_options", flags);
            global::Check.That(admission.GetType() == type && type.IsSealed && gate?.FieldType == typeof(object) &&
                active?.FieldType == typeof(int) && options?.FieldType == typeof(ConsumerVfsOptions) &&
                new[] { gate, active, options }.All(field => field?.DeclaringType == type),
                "exact content admission assembly shape remains observable");
            var limits = (ConsumerVfsOptions)options!.GetValue(admission)!;
            global::Check.That(limits.MaximumConcurrentRequests == 8 && limits.MaximumConcurrentRequestsPerUser == 4 &&
                limits.MaximumConcurrentRequestsPerConnection == 4 && limits.MaximumQueuedRequests == 128 &&
                limits.MaximumQueuedRequestsPerUser == 32 && limits.MaximumQueuedRequestsPerConnection == 16 &&
                limits.MaximumQueueWaitMilliseconds == 10000, "content admission uses the actual unchanged defaults");
            int count;
            lock (gate!.GetValue(admission)!)
                count = (int)active!.GetValue(admission)!;
            global::Check.That(count >= 0 && count <= limits.MaximumConcurrentRequests, "actual content leases remain within their bound");
            Interlocked.Exchange(ref ContentActive, count);
            Peak(ref PeakContentActive, count);
            Interlocked.Increment(ref ContentAdmissionSamples);
        }
        public void AdmissionMeasurement(string instrument, string? lane, string? reason, long value)
        {
            // The production endpoint active instrument has no lane tag. Do not invent a content/metadata split.
            if (instrument == "romd.vfs.active_requests")
                Peak(ref PeakEndpointActive, Interlocked.Add(ref EndpointActive, value));
            if (lane is not ("content" or "metadata"))
                return;
            if (instrument == "romd.vfs.queued_requests")
            {
                if (lane == "content")
                    Peak(ref PeakContentQueued, Interlocked.Add(ref ContentQueued, value));
                else
                    Peak(ref PeakMetadataQueued, Interlocked.Add(ref MetadataQueued, value));
            }
            if (instrument == "romd.vfs.admission_rejected")
            {
                if (reason == "queue_full")
                    Interlocked.Add(ref QueueFullRejections, value);
                if (reason == "timeout")
                    Interlocked.Add(ref QueueTimeoutRejections, value);
            }
        }
        public object Report()
        {
            TimeSpan loadElapsed = LoadElapsed;
            double seconds = Math.Max(loadElapsed.TotalSeconds, 0.001);
            var sustainedFlow = _flow.ToDictionary(pair => pair.Key,
                pair => _loadStarted == 0 ? 0 : pair.Value - _loadFlowBaseline.GetValueOrDefault(pair.Key), StringComparer.Ordinal);
            var delivered = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var pair in _operations)
            {
                string[] parts = pair.Key.Split('/', 3);
                if (parts.Length != 3 || parts[1] is not ("full" or "range"))
                    continue;
                var entry = Entries.SingleOrDefault(entry => entry.Path == parts[2]);
                if (entry is null)
                    continue;
                string key = parts[0] + "/" + RomDeliveryFormats.ToWire(entry.Target.Format);
                delivered[key] = delivered.GetValueOrDefault(key) + pair.Value.SustainedBytes;
            }
            return new
            {
                Status,
                Phase,
                Error,
                StartedUtc = DateTimeOffset.UtcNow - Stopwatch.GetElapsedTime(_started),
                ReportUtc = DateTimeOffset.UtcNow,
                RequestedLoadSeconds = RequestedDuration.TotalSeconds,
                LoadElapsedSeconds = loadElapsed.TotalSeconds,
                TotalElapsedSeconds = Stopwatch.GetElapsedTime(_started).TotalSeconds,
                Environment = new
                {
                    RuntimeInformation.RuntimeIdentifier,
                    RuntimeInformation.OSDescription,
                    Framework = RuntimeInformation.FrameworkDescription,
                    System.Environment.ProcessorCount,
                    System.Environment.CurrentDirectory
                },
                Limits = new
                {
                    Decoder = new ReadDecoderOptions(),
                    Vfs = VfsOptions,
                    FullReadBufferBytes = FullBufferBytes,
                    RangeBufferBytes = RangeBytes,
                    LoadWorkers = 2 * Users.Length * Entries.Select(entry => entry.Target.Format).Distinct().Count() + 2,
                    OperationDeadlineSeconds = OperationDeadline.TotalSeconds,
                    CapacityRetryMaximum = 120,
                    CapacityRetryWindowSeconds = 120,
                    DirectCapacityBackoffMaximumMilliseconds = 1000,
                    HttpRetryAfterMaximumSeconds = 30,
                    ExportProductionAdmissionTimeoutSeconds = 30,
                    ExportProbeDeadlineMilliseconds = 250,
                    ExportProbeCancellationMilliseconds = 250,
                    ProgressWriteIntervalSeconds = 15
                },
                LibraryProfile = profile.ToString(),
                NativeReadPool = NativePool?.Invoke(),
                Library = library.Select(item => new
                {
                    item.Content.RomId,
                    item.Content.FileId,
                    item.SystemKey,
                    IsoOraclePath = Path.GetFullPath(item.IsoPath),
                    LogicalSha256 = item.Content.ContentSha256.ToString(),
                    item.Content.ContentSize,
                    ArtifactSha256 = item.Content.ArtifactSha256.ToString(),
                    item.Content.ArtifactSize,
                    item.Content.EncodingProfile
                }).ToArray(),
                DeliveredTargets = Entries.Select(entry => new
                {
                    entry.Path,
                    entry.SystemKey,
                    Format = RomDeliveryFormats.ToWire(entry.Target.Format),
                    entry.Target.Filename,
                    Sha256 = entry.Target.Sha256.ToString(),
                    entry.Target.SizeBytes,
                    Md5 = entry.Target.Md5?.ToString(),
                    Sha1 = entry.Target.Sha1?.ToString()
                }).ToArray(),
                Clients = Users.Select((user, index) => new
                {
                    Client = index,
                    UserId = user,
                    ConnectionId = (Guid?)null,
                    Authentication = "test double using a development bearer lane",
                    Progress = _operations
                        .Where(pair => pair.Key.StartsWith($"client-{index}/", StringComparison.Ordinal))
                        .ToDictionary(pair => pair.Key, pair => pair.Value.Report(seconds, true))
                }).ToArray(),
                WorkerIterations = Lanes.ToDictionary(pair => pair.Key, pair => pair.Value),
                LaneProgress = _lanes.ToDictionary(pair => pair.Key, pair => pair.Value.Report(seconds)),
                AdmissionDeferrals = _deferrals.ToDictionary(pair => pair.Key, pair => pair.Value.Report()),
                Operations = _operations.ToDictionary(pair => pair.Key,
                    pair => pair.Value.Report(Math.Max(Stopwatch.GetElapsedTime(_started).TotalSeconds, .001), false)),
                SustainedOperations = _operations.ToDictionary(pair => pair.Key, pair => pair.Value.Report(seconds, true)),
                SustainedDeliveredBytesByClientAndFormat = delivered,
                SustainedDeliveredMiBPerSecondByClientAndFormat = delivered.ToDictionary(pair => pair.Key,
                    pair => pair.Value / (1024d * 1024 * seconds)),
                Retries = _retries.ToDictionary(pair => pair.Key, pair => pair.Value.Report()),
                RetriesByPhase = _phaseRetries.ToDictionary(pair => pair.Key, pair => pair.Value.Report()),
                OperationContexts = _operations.Keys.ToDictionary(key => key, key => Describe(key)),
                DataflowBytes = _flow.ToDictionary(pair => pair.Key, pair => pair.Value),
                SustainedDataflowBytes = sustainedFlow,
                SustainedDeliveredMiBPerSecond = delivered.Values.Sum() / (1024d * 1024 * seconds),
                HttpResponseBytes = Interlocked.Read(ref HttpResponseBytes),
                PeakProcessWorkingSetBytes = Interlocked.Read(ref _peakWorkingSet),
                ManagedHeapBytes = GC.GetGCMemoryInfo().HeapSizeBytes,
                ManagedAllocatedBytes = GC.GetTotalAllocatedBytes(),
                PeakLeasedBufferBytes = Interlocked.Read(ref _peakBuffers),
                CurrentLeasedBufferBytes = Interlocked.Read(ref _liveBuffers),
                CurrentDecodedReaderLeases = Interlocked.Read(ref _decoderLeases),
                PeakDecodedReaderLeases = Interlocked.Read(ref _peakDecoderLeases),
                ObservedResources = Resources?.Report(),
                TerminalResourcesConfirmed,
                ObservedAdmission = new
                {
                    ContentActive = Interlocked.Read(ref ContentActive),
                    ContentQueued = Interlocked.Read(ref ContentQueued),
                    PeakContentActive = Interlocked.Read(ref PeakContentActive),
                    PeakContentQueued = Interlocked.Read(ref PeakContentQueued),
                    MetadataQueued = Interlocked.Read(ref MetadataQueued),
                    PeakMetadataQueued = Interlocked.Read(ref PeakMetadataQueued),
                    EndpointActive = Interlocked.Read(ref EndpointActive),
                    PeakEndpointActive = Interlocked.Read(ref PeakEndpointActive),
                    ContentAdmissionSamples = Interlocked.Read(ref ContentAdmissionSamples),
                    ActiveObservationScope = "Endpoint active meter covers both lanes without tags; content active leases are exact admission snapshots, including controlled holds. No metadata active split is inferred.",
                    QueueFullRejections = Interlocked.Read(ref QueueFullRejections),
                    QueueTimeoutRejections = Interlocked.Read(ref QueueTimeoutRejections)
                },
                LatencyHistogramBufferBytes = (_operations.Count * 2L + (_retries.Count + _phaseRetries.Count) * 3L + _lanes.Count * 2L + _deferrals.Count + 4L) * 2048 * sizeof(long),
                CasOpens = Interlocked.Read(ref CasOpens),
                OracleCasOpens = Interlocked.Read(ref OracleCasOpens),
                DecoderOpens = Interlocked.Read(ref DecoderOpens),
                ResolverOpens = Interlocked.Read(ref ResolverOpens),
                MetadataCasOpens = Interlocked.Read(ref MetadataCasOpens),
                MetadataDecoderOpens = Interlocked.Read(ref MetadataDecoderOpens),
                MetadataResolverOpens = Interlocked.Read(ref MetadataResolverOpens),
                CapacityRejections = Interlocked.Read(ref CapacityRejections),
                CapacityRetries = Interlocked.Read(ref CapacityRetries),
                DecoderOpenMillisecondsIncludingAdmissionAndContainerOpen = DecoderOpenLatency.Report(),
                HttpHeadersMilliseconds = HeadersLatency.Report(),
                ActualVfsQueueWaitMilliseconds = VfsQueueWait.Report(),
                ScheduledCapacityBackoffMilliseconds = RetryBackoffMilliseconds.Report(),
                Checks = _checks.ToDictionary(pair => pair.Key, pair => pair.Value),
                CleanupFailurePhases,
                LatencyMethod = "Bounded 2048-bin logarithmic histogram; p50/p95/p99 are bin upper bounds (1% relative resolution, 0.01 ms floor); maximum is observed exactly. Full operation latency includes failed attempts/backoff; per-attempt latency excludes backoff. Scheduled retry delay and observed wait are separate; neither is a decoder-admission measurement. Deferred end-to-end latency is separate from successful completion percentiles; per-context exhaustion records retain exact rejected range offset, attempts/retries and verified partial progress.",
                Scope = "Production catalog with caller-owned disposable database/CAS; production planner, shared default reader/decoder, VFS service/content resolver/admission/HTTP endpoint in TestServer. Authentication and flat VFS lookup are explicit doubles. Original-file oracles are read-only local files; stored-format range oracles use independent raw CAS readers. No credential/connection-format-setting, default-layout, TCP/TLS, real mount, WAN, Linux container, export HTTP endpoint or gameplay qualification. Buffers include all probe ArrayPool leases and fixed latency histograms; framework/CAS/decoder allocations are represented by process working set and managed allocation totals. Operation latencies include client hashing; range batches include oracle IO, while individual range latencies exclude oracle IO and direct-reader construction. Lifetime operation counters and throughput cover all phases; sustained counters, completion latency, allocation/CPU deltas and verified delivered throughput start at the load baseline. Individual ranges count delivered bytes once; enclosing range batches have separate operation timing and are excluded from delivered throughput. Exhausted validated bodyless pre-body HTTP503 retries yield explicit lane admission deferrals and advance the unpaced workload; partial batches never count as complete. Decoder-capacity, IO, corruption, auth/header, cancellation and operation-deadline failures remain fatal. Every disc/format/client still requires sustained completed full and range-batch coverage; deferrals do not satisfy that minimum or establish starvation freedom. Full-operation and range-batch clocks include retries and backoff. Process/resource peaks are bounded fixed-cadence samples; cache usage and decoder slots come from the loaded production objects. Controlled content queue leases use unchanged defaults and real timeout/cancellation/grant; HEAD/PROPFIND HTTP metadata-lane traffic continues with zero opens while content is queued; conditional GET validators retain the production content-admission route. Connection IDs are absent in this development-bearer harness."
            };
        }
        private object Describe(string key)
        {
            string[] parts = key.Split('/', 3);
            var entry = parts.Length == 3 ? Entries.SingleOrDefault(entry => entry.Path == parts[2]) : null;
            return new
            {
                ClientLane = parts[0],
                Operation = parts.Length > 1 ? parts[1] : "unknown",
                Format = entry is null ? null : RomDeliveryFormats.ToWire(entry.Target.Format),
                Disc = entry?.SystemKey,
                RomId = entry?.Target.RomId,
                ConnectionId = (Guid?)null
            };
        }
    }

    private sealed class Metric(bool inLoad = false)
    {
        private long _count, _bytes, _started, _failed, _deferred, _inFlight, _baselineCount, _baselineBytes, _baselineStarted, _baselineFailed, _baselineDeferred;
        private long _firstProgress, _lastProgress, _firstLoadProgress, _lastLoadProgress;
        private bool _inLoad = inLoad;
        private readonly Latency _latency = new();
        private readonly Latency _loadLatency = new();
        public long Count => Interlocked.Read(ref _count);
        public long Bytes => Interlocked.Read(ref _bytes);
        public long InFlight => Interlocked.Read(ref _inFlight);
        public long SustainedBytes => _inLoad ? Bytes - Interlocked.Read(ref _baselineBytes) : 0;
        public void BeginLoad()
        {
            _baselineCount = Count;
            _baselineBytes = Bytes;
            _baselineStarted = Interlocked.Read(ref _started);
            _baselineFailed = Interlocked.Read(ref _failed);
            _baselineDeferred = Interlocked.Read(ref _deferred);
            _inLoad = true;
        }
        public void Start() { Interlocked.Increment(ref _started); Interlocked.Increment(ref _inFlight); }
        public void Fail() => Interlocked.Increment(ref _failed);
        public void Defer() => Interlocked.Increment(ref _deferred);
        public void End() => Interlocked.Decrement(ref _inFlight);
        public void Add(long bytes, double milliseconds)
        {
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _bytes, bytes);
            _latency.Add(milliseconds);
            if (_inLoad)
                _loadLatency.Add(milliseconds);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Interlocked.CompareExchange(ref _firstProgress, now, 0);
            Interlocked.Exchange(ref _lastProgress, now);
            if (_inLoad)
            {
                Interlocked.CompareExchange(ref _firstLoadProgress, now, 0);
                Interlocked.Exchange(ref _lastLoadProgress, now);
            }
        }
        public object Report(double seconds, bool sustained)
        {
            long bytes = sustained ? SustainedBytes : Bytes;
            return new
            {
                Started = sustained ? (_inLoad ? Interlocked.Read(ref _started) - _baselineStarted : 0) : Interlocked.Read(ref _started),
                Completed = sustained ? (_inLoad ? Count - _baselineCount : 0) : Count,
                Failed = sustained ? (_inLoad ? Interlocked.Read(ref _failed) - _baselineFailed : 0) : Interlocked.Read(ref _failed),
                Deferred = sustained ? (_inLoad ? Interlocked.Read(ref _deferred) - _baselineDeferred : 0) : Interlocked.Read(ref _deferred),
                InFlight,
                Bytes = bytes,
                MiBPerSecond = bytes / (1024d * 1024 * seconds),
                FirstProgressUnixMilliseconds = sustained ? Interlocked.Read(ref _firstLoadProgress) : Interlocked.Read(ref _firstProgress),
                LastProgressUnixMilliseconds = sustained ? Interlocked.Read(ref _lastLoadProgress) : Interlocked.Read(ref _lastProgress),
                LatencyMilliseconds = (sustained ? _loadLatency : _latency).Report()
            };
        }
    }

    private sealed class RetryMetric
    {
        private long _attempts, _httpRejections, _decoderRejections, _retries;
        public Latency AttemptElapsed { get; } = new();
        private readonly Latency _scheduled = new(), _observed = new();
        public void Attempt() => Interlocked.Increment(ref _attempts);
        public void Reject(bool http)
        {
            if (http)
                Interlocked.Increment(ref _httpRejections);
            else
                Interlocked.Increment(ref _decoderRejections);
        }
        public void Wait(double scheduled, double observed)
        {
            Interlocked.Increment(ref _retries);
            _scheduled.Add(scheduled);
            _observed.Add(observed);
        }
        public object Report() => new
        {
            Attempts = Interlocked.Read(ref _attempts),
            HttpRejections = Interlocked.Read(ref _httpRejections),
            DecoderRejections = Interlocked.Read(ref _decoderRejections),
            Retries = Interlocked.Read(ref _retries),
            AttemptMilliseconds = AttemptElapsed.Report(),
            ScheduledWaitMilliseconds = _scheduled.Report(),
            ObservedWaitMilliseconds = _observed.Report()
        };
    }

    private sealed class DeferralMetric
    {
        private readonly object _gate = new();
        private long _count, _verifiedBytes, _attempts, _retries;
        private long? _offset;
        private string? _retryContext;
        private double _retryElapsed;
        private readonly Latency _elapsed = new();
        public void Add(HttpExhaustion exhausted, long? offset, long verifiedBytes, double milliseconds)
        {
            lock (_gate)
            {
                _count++;
                _verifiedBytes += verifiedBytes;
                _attempts += exhausted.Attempts;
                _retries += exhausted.Retries;
                _offset = offset;
                _retryContext = exhausted.Context;
                _retryElapsed = exhausted.ElapsedMilliseconds;
                _elapsed.Add(milliseconds);
            }
        }
        public object Report()
        {
            lock (_gate)
                return new
                {
                    Count = _count,
                    VerifiedPartialBytes = _verifiedBytes,
                    Attempts = _attempts,
                    Retries = _retries,
                    RejectedRangeOffset = _offset,
                    LastRetryContext = _retryContext,
                    LastRetryElapsedMilliseconds = _retryElapsed,
                    ElapsedMilliseconds = _elapsed.Report(),
                    Scope = "Only exhausted validated HTTP admission rejection; latest offset/context plus fixed counters, no per-offset history. Partial bytes are independently verified ranges; full deferrals have zero verified bytes."
                };
        }
    }

    private sealed class LaneMetric
    {
        private long _started, _completed, _failed, _deferred, _inFlight, _bytes, _firstStart, _lastComplete, _peakNoProgressTicks;
        private readonly Latency _latency = new(), _deferredLatency = new();
        public long InFlight => Interlocked.Read(ref _inFlight);
        public void Start()
        {
            long now = Stopwatch.GetTimestamp();
            Interlocked.CompareExchange(ref _firstStart, now, 0);
            Interlocked.Increment(ref _started);
            Interlocked.Increment(ref _inFlight);
        }
        public void Finish(bool completed, bool deferred, long bytes, double milliseconds)
        {
            long now = Stopwatch.GetTimestamp(), previous = Interlocked.Read(ref _lastComplete);
            Peak(ref _peakNoProgressTicks, now - (previous == 0 ? Interlocked.Read(ref _firstStart) : previous));
            if (completed)
            {
                Interlocked.Increment(ref _completed);
                Interlocked.Exchange(ref _lastComplete, now);
                _latency.Add(milliseconds);
            }
            else if (deferred)
            { Interlocked.Increment(ref _deferred); _deferredLatency.Add(milliseconds); }
            else
                Interlocked.Increment(ref _failed);
            Interlocked.Add(ref _bytes, bytes);
            Interlocked.Decrement(ref _inFlight);
        }
        public object Report(double seconds)
        {
            long now = Stopwatch.GetTimestamp(), first = Interlocked.Read(ref _firstStart), last = Interlocked.Read(ref _lastComplete);
            if (InFlight != 0 && first != 0)
                Peak(ref _peakNoProgressTicks, now - (last == 0 ? first : last));
            return new
            {
                Started = Interlocked.Read(ref _started),
                Completed = Interlocked.Read(ref _completed),
                Failed = Interlocked.Read(ref _failed),
                Deferred = Interlocked.Read(ref _deferred),
                InFlight,
                Bytes = Interlocked.Read(ref _bytes),
                MiBPerSecond = Interlocked.Read(ref _bytes) / (1024d * 1024 * seconds),
                FirstStartSecondsAgo = first == 0 ? (double?)null : Stopwatch.GetElapsedTime(first, now).TotalSeconds,
                LastCompletionSecondsAgo = last == 0 ? (double?)null : Stopwatch.GetElapsedTime(last, now).TotalSeconds,
                MaximumNoProgressMilliseconds = Interlocked.Read(ref _peakNoProgressTicks) * 1000d / Stopwatch.Frequency,
                EndToEndMilliseconds = _latency.Report(),
                DeferredEndToEndMilliseconds = _deferredLatency.Report()
            };
        }
    }

    private sealed class Latency
    {
        private readonly object _gate = new();
        private readonly long[] _bins = new long[2048];
        private long _count;
        private double _maximum;
        public void Add(double milliseconds)
        {
            int bin = Math.Clamp((int)Math.Ceiling(Math.Log(Math.Max(0.01, milliseconds) / 0.01) / Math.Log(1.01)), 0, _bins.Length - 1);
            lock (_gate)
            { _bins[bin]++; _count++; _maximum = Math.Max(_maximum, milliseconds); }
        }
        public object Report()
        {
            lock (_gate)
                return new
                {
                    Count = _count,
                    P50UpperBoundMilliseconds = Percentile(0.5),
                    P95UpperBoundMilliseconds = Percentile(0.95),
                    P99UpperBoundMilliseconds = Percentile(0.99),
                    MaximumMilliseconds = _maximum
                };
        }
        private double? Percentile(double quantile)
        {
            if (_count == 0)
                return null;
            long wanted = (long)Math.Ceiling(_count * quantile), total = 0;
            for (int index = 0; index < _bins.Length; index++)
            {
                total += _bins[index];
                if (total >= wanted)
                    return Math.Max(Math.Min(_maximum, 0.01 * Math.Pow(1.01, index)), 0.01);
            }
            return _maximum;
        }
    }

    private static void Peak(ref long peak, long candidate)
    {
        long observed = Interlocked.Read(ref peak);
        while (candidate > observed)
        {
            long previous = Interlocked.CompareExchange(ref peak, candidate, observed);
            if (previous == observed)
                return;
            observed = previous;
        }
    }
}
