using Romd.Contracts.Management.Export;
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Hangfire;
using Hangfire.States;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Application.Common.Ids;
using Romd.Consumer.Application.Vfs;
using Romd.Contracts.Consumer.Account;
using Romd.Contracts.Consumer.Releases;
using Romd.Domain.Hashing;
using Romd.Domain.Identity;
using Romd.Domain.Libraries;
using Romd.Domain.Paths;
using Romd.Domain.Source.Rom;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Hosting.IntegrationTests.Vfs;
using Romd.Hosting.Vfs;
using Romd.Infrastructure;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Jobs.Handlers;
using Romd.Infrastructure.Source;
using Romd.Infrastructure.Storage;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;
using Xunit;
using static Romd.Hosting.IntegrationTests.Vfs.MediaQualificationFixture;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class ConsumerIdentityEndpointsTests
{
    [MediaQualificationFact]
    public async Task MediaDelivery_RealNintendoDiscs_QualifiesDownloadsExportsManifestsAndPersistedVfs()
    {
        string configPath = Environment.GetEnvironmentVariable("ROMD_MEDIA_QUALIFICATION_FIXTURES") ?? "";
        string reportPath = Environment.GetEnvironmentVariable("ROMD_MEDIA_DELIVERY_REPORT") ?? "";
        using var deadline = new CancellationTokenSource(TimeSpan.FromHours(3));
        var ct = deadline.Token;
        var inputs = await LoadAsync(configPath, reportPath, ct);
        await using var report = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous);
        string scratch = Path.Combine(Path.GetDirectoryName(reportPath)!,
            "romd-media-delivery-" + Guid.NewGuid().ToString("N"));
        var results = new List<MediaDeliveryObservation>();
        var measured = new List<(MediaQualificationInput Input, VfsProbeContent Original, VfsProbeContent Rvz)>();
        var counters = new MediaQualificationCounters();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        string phase = "starting";
        string status = "in-progress";
        string? failureType = null;
        Exception? primaryFailure = null;
        MediaQualificationContention? contention = null;

        async Task SaveReportAsync(CancellationToken token)
        {
            bool captureScratchExists = Directory.Exists(scratch);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Status = status,
                StartedAtUtc = started,
                RecordedAtUtc = DateTimeOffset.UtcNow,
                Phase = phase,
                FailureType = failureType,
                Cleanup = new
                {
                    PendingFailureType = ReadCleanupDiagnostic(primaryFailure, "MediaQualification.PendingCleanupFailureType") as string,
                    PendingFailureCount = ReadCleanupDiagnostic(primaryFailure, "MediaQualification.PendingCleanupFailureCount") is int count
                        ? count : (int?)null,
                    UnjoinedRequest = ReadCleanupDiagnostic(primaryFailure, "MediaQualification.UnjoinedRequest") is true,
                    RevocationFailureType = ReadCleanupDiagnostic(primaryFailure, "MediaQualification.RevocationFailureType") as string
                },
                Scope = "Actual admin and consumer TestServer HTTP, production PostgreSQL catalog/layout/CAS/reader/decoder; Testing authentication seam; actual issued persisted VFS Basic credentials with HTTPS request scheme. Short content-admission contention uses four controlled leases on the production admission instance bound to actual persisted owner/connection identities, followed by actual Basic HTTP ranges, cancellation, default ten-second queue timeout and release/grant; held leases are not stalled HTTP transfers; reported request times include authentication, queue observation and response validation, not isolated queue-only latency. Disposable manual measured RVZ publication, not a convergence job; released test-owned original CAS copies reclaimed through the production file ledger and independently checked absent, with retained RVZ checked intact; ordinary text companions are synthetic additions to the disposable DAT projection. Durable export acceptance/dispatch/claimed executor with controlled transport, not a Hangfire server. Failure scratch preservation covers only the task-owned response ZIP capture directory; validated ZIP captures are deleted immediately. The disposable PostgreSQL database and consumer data directory, including CAS and durable export artifacts, are cleaned up on fixture disposal and are not captured as failure evidence. No network/TLS handshake, sustained load, mount, container or total-memory qualification.",
                ReadDefaults = new { MaxConcurrent = 4, MaxIdle = 2, CacheBytes = 64L << 20 },
                Fixtures = measured.Select(item => new
                {
                    Fixture = item.Input.Name,
                    item.Input.SystemKey,
                    item.Input.DatId,
                    Original = MediaQualificationFacts.From(item.Original),
                    Retained = MediaQualificationFacts.From(item.Rvz)
                }),
                CaptureScratch = new { Path = scratch, Exists = captureScratchExists },
                ScratchPreservedOnFailure = status == "failed" && captureScratchExists,
                Observations = results,
                Access = counters.Snapshot(),
                ContentAdmission = contention
            }, new JsonSerializerOptions { WriteIndented = true });
            Require(bytes.Length <= 128 * 1024 && results.Count <= 128, "bounded qualification report");
            report.Position = 0;
            await report.WriteAsync(bytes, token);
            report.SetLength(bytes.Length);
            await report.FlushAsync(token);
        }
        void Observed(string surface, string fixture, string format, long bytes = 0, int members = 0,
            MediaQualificationAccess? metadataDelta = null)
        {
            Require(results.Count < 128, "bounded surface observations");
            results.Add(new(surface, fixture, format, bytes, members, metadataDelta));
        }

        try
        {
            await SaveReportAsync(ct);
            Directory.CreateDirectory(scratch);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var deliveryOptions = CreateDeliveryOptions();
            await using (var fixture = await ConsumerHostFixture.CreateAsync(services =>
            {
                services.AddSingleton(new ConsumerVfsOptions { Enabled = true, AllowDevelopmentBearer = false });
                DecorateMediaSingleton<IContentAddressableStore>(services,
                    (inner, owns) => new MediaQualificationStore(inner, counters, owns));
                DecorateMediaSingleton<IRomRepresentationDecoder>(services,
                    (inner, owns) => new MediaDeliveryDecoder(inner, counters, owns));
                services.RemoveAll<IConsumerVfsContentResolver>();
                services.AddScoped<IConsumerVfsContentResolver>(provider => new MediaDeliveryResolver(
                    new ConsumerVfsContentResolver(provider.GetRequiredService<IContentAddressableStore>(),
                        provider.GetRequiredService<IRomContentReader>()), counters));
            }, configureHost: builder =>
            {
                builder.UseSetting("Romd:ConsumerHost:PublicUrl", "https://localhost");
                builder.UseSetting("Romd:ConsumerDelivery:SigningKeyId", deliveryOptions.SigningKeyId);
                builder.UseSetting("Romd:ConsumerDelivery:SigningSecret", deliveryOptions.SigningSecret);
                builder.UseSetting("Romd:ConsumerDelivery:SignedUrlTtlMinutes",
                    deliveryOptions.SignedUrlTtlMinutes.ToString(CultureInfo.InvariantCulture));
                builder.UseSetting("Romd:MediaRead:MaxConcurrent", "4");
                builder.UseSetting("Romd:MediaRead:MaxIdle", "2");
                builder.UseSetting("Romd:MediaRead:CacheBytes", (64L << 20).ToString(CultureInfo.InvariantCulture));
            }))
            {
                var transport = new MediaDeliveryTransport();
                await using var adminFactory = fixture.CreateBorrowedMediaAdminFactory(transport);
                int libraryId = await fixture.CreateLibraryAsync("Media qualification");
                Guid ownerId = await fixture.SeedMediaUserAsync(libraryId, ct);
                using var owner = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
                { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false })
                    .WithTestUser(ownerId, "media-user@localhost", [RomdRoleType.User], libraryId);
                owner.Timeout = Timeout.InfiniteTimeSpan;
                using var anonymousConsumer = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
                { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
                anonymousConsumer.Timeout = Timeout.InfiniteTimeSpan;
                var seeds = new List<MediaDeliverySeed>(inputs.Count);

                foreach (var input in inputs)
                {
                    phase = "measure-and-publish-" + input.SystemKey;
                    await SaveReportAsync(ct);
                    var original = await VfsProbeContent.CreateAsync(input.Iso, ct);
                    VfsProbeContent? rvz = null;
                    try
                    {
                        rvz = await VfsProbeContent.CreateAsync(input.NormalizedRvz, ct);
                        await ValidateMediaDiscKindAsync(input, original, ct);
                        var dat = await ReadDatAsync(input, MediaQualificationFacts.From(original), ct);
                        var seed = await fixture.SeedMediaDeliveryAsync(libraryId, ownerId, input, dat, original, ct);
                        await fixture.PublishMeasuredProbeRvzAsync(rvz, original, ct);
                        await fixture.AssertMediaPublicationAsync(seed, rvz, ct);
                        await fixture.ReclaimMediaOriginalAsync(adminFactory, seed, rvz, ct);
                        seeds.Add(seed with { Rvz = MediaQualificationFacts.From(rvz) });
                        measured.Add((input, original, rvz));
                        Observed("measured-manual-canonical-publication-and-original-release", input.Name, "rvz",
                            rvz.Length, 1);
                    }
                    catch
                    {
                        await original.DisposeAsync();
                        if (rvz is not null)
                            await rvz.DisposeAsync();
                        throw;
                    }
                }

                phase = "persisted-vfs-metadata-before-content";
                await SaveReportAsync(ct);
                var connections = new List<VfsConnectionCredential>(2);
                foreach (string format in new[] { "stored", "original" })
                {
                    using var issued = await owner.PostAsJsonAsync("/api/me/connections",
                        new SaveVfsConnectionRequest("Media qualification " + format, format == "stored" ? null : format), ct);
                    Require(issued.StatusCode == HttpStatusCode.Created, "issue actual VFS connection");
                    var credential = await issued.Content.ReadFromJsonAsync<VfsConnectionCredential>(cancellationToken: ct);
                    Require(credential is not null && credential.Connection.Format == format, "persisted VFS format");
                    await fixture.AssertMediaConnectionAsync(ownerId, credential!.Connection.Id, format, ct);
                    connections.Add(credential);
                    foreach (var seed in seeds)
                    {
                        var facts = format == "stored" ? seed.Rvz! : seed.Original;
                        string relative = format == "stored" ? Path.ChangeExtension(seed.RelativePath, ".rvz") : seed.RelativePath;
                        await QualifyMediaMetadataAsync(anonymousConsumer, credential, seed.VfsPrefix + relative,
                            facts, counters, seed.Name, format, Observed, ct);
                    }
                }

                phase = "persisted-vfs-short-content-admission-contention";
                await SaveReportAsync(ct);
                Guid otherUserId = await fixture.SeedMediaUserAsync(libraryId, ct);
                using var otherOwner = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
                { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false })
                    .WithTestUser(otherUserId, "media-other@localhost", [RomdRoleType.User], libraryId);
                otherOwner.Timeout = Timeout.InfiniteTimeSpan;
                VfsConnectionCredential otherConnection;
                using (var issuedOther = await otherOwner.PostAsJsonAsync("/api/me/connections",
                    new SaveVfsConnectionRequest("Media qualification other account"), ct))
                {
                    Require(issuedOther.StatusCode == HttpStatusCode.Created, "issue other actual account connection");
                    otherConnection = (await issuedOther.Content.ReadFromJsonAsync<VfsConnectionCredential>(
                        cancellationToken: ct))!;
                    Require(otherConnection is not null && otherConnection.Connection.Format == "stored",
                        "other account stored format");
                    await fixture.AssertMediaConnectionAsync(otherUserId, otherConnection!.Connection.Id, "stored", ct);
                }
                await ExecuteWithRevocationAsync(token =>
                    QualifyMediaContentionAsync(anonymousConsumer, connections, otherConnection, ownerId,
                        seeds[0], inputs[0], fixture.Factory.Services.GetRequiredService<ConsumerVfsAdmission>(),
                        fixture.Factory.Services.GetRequiredService<ConsumerVfsOptions>(), counters,
                        value => contention = value, Observed, token),
                    otherOwner, otherConnection.Connection.Id, TimeSpan.FromSeconds(30), ct);
                await SaveReportAsync(ct);

                phase = "actual-admin-and-consumer-delivery";
                await SaveReportAsync(ct);
                {
                    using var admin = adminFactory.CreateClient(new WebApplicationFactoryClientOptions
                    { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false })
                        .WithTestUser(ownerId, "media-admin@localhost", [RomdRoleType.Admin], libraryId);
                    admin.Timeout = Timeout.InfiniteTimeSpan;
                    using var libraryUser = adminFactory.CreateClient(new WebApplicationFactoryClientOptions
                    { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false })
                        .WithTestUser(ownerId, "media-user@localhost", [RomdRoleType.User], libraryId);
                    libraryUser.Timeout = Timeout.InfiniteTimeSpan;
                    using var anonymousAdmin = adminFactory.CreateClient(new WebApplicationFactoryClientOptions
                    { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
                    anonymousAdmin.Timeout = Timeout.InfiniteTimeSpan;

                    foreach (var seed in seeds)
                    {
                        phase = "delivery-" + seed.SystemKey;
                        await SaveReportAsync(ct);
                        await QualifyMediaRomMetadataAsync(admin, seed, ct);
                        Observed("admin-ROM-format-metadata", seed.Name, "both");
                        await QualifyMediaFileAsync(admin, "/api/roms/" + IdCoder.Encode(seed.RomId) + "/download",
                            seed.Original, seed.OriginalFilename, ct);
                        Observed("admin-default-original", seed.Name, "original", seed.Original.Size);
                        foreach (string format in new[] { "original", "rvz" })
                        {
                            var facts = format == "original" ? seed.Original : seed.Rvz!;
                            string filename = format == "original" ? seed.OriginalFilename :
                                Path.ChangeExtension(seed.OriginalFilename, ".rvz");
                            await QualifyMediaFileAsync(admin,
                                "/api/roms/" + IdCoder.Encode(seed.RomId) + "/download?format=" + format,
                                facts, filename, ct);
                            Observed("admin-direct-ROM", seed.Name, format, facts.Size);
                            var fileTicket = await CreateMediaTicketAsync(admin, "Rom", seed.RomId, format, ct);
                            await QualifyMediaFileAsync(anonymousAdmin, fileTicket.DownloadUrl, facts, filename, ct);
                            Observed("admin-anonymous-single-file-ticket", seed.Name, format, facts.Size);

                            var titleMembers = MediaArchiveMembers(seed, format, "title");
                            var titleTicket = await CreateMediaTicketAsync(admin, "Title", seed.TitleId, format, ct);
                            await QualifyMediaZipAsync(anonymousAdmin, titleTicket.DownloadUrl, HttpMethod.Get,
                                titleMembers, scratch, ct);
                            Observed("admin-anonymous-title-mixed-ZIP-ticket", seed.Name, format, titleMembers.Values.Sum(f => f.Size), 2);
                            var releaseTicket = await CreateMediaTicketAsync(libraryUser, "Release", seed.ReleaseId, format, ct);
                            var releaseMembers = MediaArchiveMembers(seed, format, "release");
                            await QualifyMediaZipAsync(anonymousAdmin, releaseTicket.DownloadUrl, HttpMethod.Get,
                                releaseMembers, scratch, ct);
                            Observed("admin-library-authorized-exact-release-mixed-ZIP-ticket", seed.Name, format,
                                releaseMembers.Values.Sum(f => f.Size), 2);
                        }
                        long manifestBytes = await QualifyMediaManifestAsync(owner, anonymousConsumer, seed, ct);
                        Observed("consumer-legacy-and-both-format-metadata-and-anonymous-grants", seed.Name,
                            "original+rvz+ordinary-fallback", manifestBytes, 2);
                    }

                    foreach (string format in new[] { "original", "rvz" })
                    {
                        phase = "durable-export-" + format;
                        await SaveReportAsync(ct);
                        Guid jobId = await fixture.RunMediaExportAsync(adminFactory, admin, transport,
                            ownerId, libraryId, format, seeds.Count * 2, ct);
                        var expected = new Dictionary<string, MediaQualificationFacts>(StringComparer.Ordinal);
                        foreach (var seed in seeds)
                            foreach (var member in MediaArchiveMembers(seed, format, "library"))
                                expected.Add(member.Key, member.Value);
                        var ticket = await IssueMediaTicketAsync(admin, new HttpRequestMessage(HttpMethod.Post,
                            "/api/download-tickets")
                        { Content = JsonContent.Create(new { target = "ExportJob", id = jobId.ToString("D") }) }, HttpStatusCode.Created, ct);
                        await QualifyMediaZipAsync(anonymousAdmin, ticket.DownloadUrl, HttpMethod.Get, expected, scratch, ct);
                        Observed("actual-durable-export-anonymous-completed-mixed-ZIP-ticket", "shared library",
                            format, expected.Values.Sum(f => f.Size), expected.Count);
                    }
                }
                foreach (var connection in connections)
                    await ExecuteWithRevocationAsync(_ => Task.CompletedTask, owner, connection.Connection.Id,
                        TimeSpan.FromSeconds(30), ct);
            }
            phase = "immutable-oracles-after-delivery";
            foreach (var item in measured)
            {
                Require(await MeasureFileAsync(item.Input.Iso, ct) == MediaQualificationFacts.From(item.Original),
                    "ISO oracle remains unchanged");
                Require(await MeasureFileAsync(item.Input.NormalizedRvz, ct) == MediaQualificationFacts.From(item.Rvz),
                    "RVZ oracle remains unchanged");
            }
            Directory.Delete(scratch);
            status = "passed";
            phase = "completed";
        }
        catch (Exception error)
        {
            status = "failed";
            failureType = error.GetType().FullName;
            primaryFailure = error;
            throw;
        }
        finally
        {
            try
            {
                using var evidenceDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await SaveReportAsync(evidenceDeadline.Token);
            }
            catch (Exception evidenceFailure) when (primaryFailure is not null)
            {
                TryAttachCleanupDiagnostic(primaryFailure, "MediaQualification.EvidenceFailureType", evidenceFailure.GetType().FullName);
            }
            finally
            {
                foreach (var item in measured)
                {
                    try
                    { await item.Original.DisposeAsync(); }
                    catch (Exception cleanupFailure) when (primaryFailure is not null)
                    { TryAttachCleanupDiagnostic(primaryFailure, "MediaQualification.OriginalCleanupFailureType", cleanupFailure.GetType().FullName); }
                    try
                    { await item.Rvz.DisposeAsync(); }
                    catch (Exception cleanupFailure) when (primaryFailure is not null)
                    { TryAttachCleanupDiagnostic(primaryFailure, "MediaQualification.RvzCleanupFailureType", cleanupFailure.GetType().FullName); }
                }
            }
        }
    }

    private sealed record MediaDeliveryObservation(string Surface, string Fixture, string Format,
        long VerifiedBytes, int Members, MediaQualificationAccess? MetadataAccessDelta);
    private sealed record MediaDeliverySeed(string Name, string SystemKey, int RomId, int OriginalFileId,
        int TitleId, int ReleaseId, string PlatformName, string TitleName, string OriginalFilename,
        string RelativePath, string VfsPrefix, MediaQualificationFacts Original,
        MediaQualificationFacts Companion, MediaQualificationFacts? Rvz = null);

    private static async Task ValidateMediaDiscKindAsync(MediaQualificationInput input,
        VfsProbeContent original, CancellationToken ct)
    {
        byte[] magic = new byte[8];
        original.Stream.Position = 0x18;
        await original.Stream.ReadExactlyAsync(magic, ct);
        bool gameCube = BinaryPrimitives.ReadUInt32BigEndian(magic.AsSpan(4)) == 0xc2339f3d;
        bool wii = BinaryPrimitives.ReadUInt32BigEndian(magic) == 0x5d1c9ea3;
        Require(input.SystemKey == "gc" ? gameCube && !wii : wii && !gameCube, "ISO disc magic matches configured kind");
        original.Stream.Position = 0;
    }

    private static void DecorateMediaSingleton<T>(IServiceCollection services,
        Func<T, bool, T> decorate) where T : class
    {
        var descriptor = services.Last(service => service.ServiceType == typeof(T));
        Require(descriptor.Lifetime == ServiceLifetime.Singleton, "production singleton decorator lifetime");
        services.RemoveAll<T>();
        services.AddSingleton<T>(provider =>
        {
            var inner = descriptor.ImplementationInstance as T ??
                descriptor.ImplementationFactory?.Invoke(provider) as T ??
                (T)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
            return decorate(inner, descriptor.ImplementationInstance is null);
        });
    }

    private sealed class MediaDeliveryDecoder(IRomRepresentationDecoder inner,
        MediaQualificationCounters counters, bool ownsInner) : IRomRepresentationDecoder, IAsyncDisposable
    {
        public bool CanDecode(RomRepresentationEncoding format) => inner.CanDecode(format);
        public async Task<IRandomAccessContent> DecodeAsync(RomRepresentationEncoding format,
            IRandomAccessContent source, CancellationToken ct = default, Sha256? artifact = null,
            TimeSpan? admissionTimeout = null)
        {
            counters.Decode();
            return new MediaQualificationCountedContent(
                await inner.DecodeAsync(format, source, ct, artifact, admissionTimeout), counters, decoded: true);
        }
        public async Task<IRandomAccessContent> DecodeAsync(RomRepresentationEncoding format,
            RomArtifactSource source, RomMediaOpenContext context, CancellationToken ct = default)
        {
            counters.Decode();
            return new MediaQualificationCountedContent(
                await inner.DecodeAsync(format, source, context, ct), counters, decoded: true);
        }
        public ValueTask DisposeAsync() => ownsInner && inner is IAsyncDisposable disposable
            ? disposable.DisposeAsync() : ValueTask.CompletedTask;
    }

    private sealed class MediaDeliveryResolver(ConsumerVfsContentResolver inner,
        MediaQualificationCounters counters) : IConsumerVfsContentResolver
    {
        public Task<Stream?> OpenAsync(ConsumerVfsFile file, CancellationToken ct = default)
        { counters.Resolve(); return inner.OpenAsync(file, ct); }
    }

    private sealed class MediaDeliveryTransport : IBackgroundJobClient
    {
        public Hangfire.Common.Job? Invocation { get; private set; }
        public IState? State { get; private set; }
        public string? DeliveryId { get; private set; }
        public int Created { get; private set; }
        public string Create(Hangfire.Common.Job job, IState state)
        {
            Invocation = job;
            State = state;
            DeliveryId = "media-qualification-" + (++Created).ToString(CultureInfo.InvariantCulture);
            return DeliveryId;
        }
        public bool ChangeState(string jobId, IState state, string expectedState) =>
            throw new InvalidOperationException("Qualification transport received an unexpected state change.");
    }

    private static IReadOnlyDictionary<string, MediaQualificationFacts> MediaArchiveMembers(
        MediaDeliverySeed seed, string format, string scope)
    {
        string filename = format == "rvz" ? Path.ChangeExtension(seed.OriginalFilename, ".rvz") : seed.OriginalFilename;
        string parent = scope switch
        {
            "title" => "",
            "release" => seed.RelativePath[..(seed.RelativePath.LastIndexOf('/') + 1)],
            "library" => ExportArchivePaths.SafeFilename(seed.PlatformName) + "/" +
                ExportArchivePaths.SafeFilename(seed.TitleName) + "/",
            _ => throw new InvalidOperationException("Unknown qualification archive scope.")
        };
        return new Dictionary<string, MediaQualificationFacts>(StringComparer.Ordinal)
        {
            [parent + filename] = format == "rvz" ? seed.Rvz! : seed.Original,
            [(scope == "release" ? "" : parent) + "qualification-notes.txt"] = seed.Companion
        };
    }

    /// <summary>A title, release or file ticket is a member the ticket collection creates (201) at its redemption URL.</summary>
    private static Task<AdminDownloadTicket> CreateMediaTicketAsync(HttpClient client, string target, int id,
        string format, CancellationToken ct) =>
        IssueMediaTicketAsync(client, new HttpRequestMessage(HttpMethod.Post, "/api/download-tickets")
        {
            Content = JsonContent.Create(new { target, id = IdCoder.Encode(id), format })
        }, HttpStatusCode.Created, ct);

    /// <summary>Issues a ticket and requires exactly <paramref name="expected" />; a created one is at its Location.</summary>
    private static async Task<AdminDownloadTicket> IssueMediaTicketAsync(HttpClient client, HttpRequestMessage request,
        HttpStatusCode expected, CancellationToken ct)
    {
        using (request)
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            Require(response.StatusCode == expected, "actual admin ticket issuance");
            var ticket = await ReadMediaJsonAsync<AdminDownloadTicket>(response, ct);
            Require(ticket.DownloadUrl.StartsWith("/api/download-tickets/", StringComparison.Ordinal) &&
                ticket.ExpiresAt > DateTimeOffset.UtcNow, "fresh relative admin ticket");
            Require(expected != HttpStatusCode.Created ||
                response.Headers.Location?.OriginalString == ticket.DownloadUrl, "created ticket location");
            return ticket;
        }
    }

    private static async Task<T> ReadMediaJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        byte[] bytes = await ReadSmallAsync(input, MaximumConfigurationBytes, ct);
        var value = JsonSerializer.Deserialize<T>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Require(value is not null, "typed bounded response");
        return value!;
    }

    private static async Task QualifyMediaFileAsync(HttpClient client, string route,
        MediaQualificationFacts expected, string filename, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Require(response.StatusCode == HttpStatusCode.OK, "actual file HTTP response");
        Require(response.Content.Headers.ContentType?.MediaType == "application/octet-stream" &&
            response.Content.Headers.ContentLength == expected.Size, "delivered file headers");
        string? actualFilename = response.Content.Headers.ContentDisposition?.FileNameStar ??
            response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Require(actualFilename == filename, "delivered file extension and filename");
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        Require(await MeasureAsync(body, expected.Size, ct) == expected, "full HTTP stream hashes and EOF");
    }

    private static async Task QualifyMediaZipAsync(HttpClient client, string route, HttpMethod method,
        IReadOnlyDictionary<string, MediaQualificationFacts> expected, string scratch, CancellationToken ct)
    {
        string zip = Path.Combine(scratch, Guid.NewGuid().ToString("N") + ".zip");
        using var request = new HttpRequestMessage(method, route);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Require(response.StatusCode == HttpStatusCode.OK &&
            response.Content.Headers.ContentType?.MediaType == "application/zip", "actual ZIP HTTP response");
        long bound = checked(expected.Values.Sum(facts => facts.Size) + (1 << 20));
        Require(bound <= MaximumContentBytes, "bounded archive transfer");
        await using (var output = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            byte[] buffer = new byte[64 * 1024];
            long bytes = 0;
            while (true)
            {
                int count = await body.ReadAsync(buffer, ct);
                if (count == 0)
                    break;
                bytes = checked(bytes + count);
                Require(bytes <= bound, "ZIP exceeds expected bounded transfer");
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            await output.FlushAsync(ct);
            Require(bytes > 0 && (!response.Content.Headers.ContentLength.HasValue ||
                response.Content.Headers.ContentLength == bytes), "complete nonempty ZIP transfer");
        }
        await ValidateZipAsync(zip, expected, ct);
        File.Delete(zip);
    }

    private static void CheckMediaFormat(Romd.Contracts.Common.Models.RomDeliveryFormatMetadata format,
        string wireFormat, string filename, MediaQualificationFacts facts)
    {
        Require(format.Format == wireFormat && format.State == "available" && format.Filename == filename &&
            format.SizeBytes?.Value == facts.Size && format.Sha256 == facts.Sha256 &&
            format.Md5 == facts.Md5 && format.Sha1 == facts.Sha1, "delivered-format checksum and length metadata");
        Require(!string.IsNullOrWhiteSpace(format.ProfileVersion) &&
            format.RuntimeApplicability.Count <= 1, "bounded format profile and applicability");
    }

    private static async Task QualifyMediaRomMetadataAsync(HttpClient admin, MediaDeliverySeed seed, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/roms/" + IdCoder.Encode(seed.RomId));
        using var response = await admin.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Require(response.StatusCode == HttpStatusCode.OK, "admin ROM metadata endpoint");
        var rom = await ReadMediaJsonAsync<Romd.Contracts.Management.Models.Rom>(response, ct);
        Require(rom.Id == IdCoder.Encode(seed.RomId) && rom.Size.Value == seed.Original.Size &&
            rom.Sha1 == seed.Original.Sha1 && rom.Md5 == seed.Original.Md5 && rom.Crc32 == seed.Original.Crc32 &&
            rom.Formats?.Count == 2, "admin logical identity remains original");
        foreach (var format in rom.Formats!)
        {
            Require(format.Format is "original" or "rvz", "known admin delivery format");
            CheckMediaFormat(format, format.Format, format.Format == "original" ? seed.OriginalFilename :
                Path.ChangeExtension(seed.OriginalFilename, ".rvz"), format.Format == "original" ? seed.Original : seed.Rvz!);
        }
        Require(rom.Formats!.Select(format => format.Format).Distinct(StringComparer.Ordinal).Count() == 2,
            "one admin option per format");
    }

    private static async Task<long> QualifyMediaManifestAsync(HttpClient owner, HttpClient anonymous,
        MediaDeliverySeed seed, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/releases/" + IdCoder.Encode(seed.ReleaseId) + "/manifest");
        using var response = await owner.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Require(response.StatusCode == HttpStatusCode.OK, "actual consumer manifest");
        var manifest = await ReadMediaJsonAsync<ConsumerReleaseManifest>(response, ct);
        Require(manifest.ReleaseId == IdCoder.Encode(seed.ReleaseId) && manifest.TitleId == IdCoder.Encode(seed.TitleId) &&
            manifest.SystemKey == seed.SystemKey && manifest.IsComplete && manifest.Items.Count == 2 &&
            Guid.TryParse(manifest.ServerInstanceId, out _), "consumer release and identity metadata");
        Require(manifest.Runtime.MinimumInstallBytes.Value == seed.Original.Size + seed.Companion.Size &&
            manifest.Runtime.Packaging == "direct_files", "consumer original runtime size");
        Require(manifest.Items.Select(item => item.RelativePath).Distinct(StringComparer.Ordinal).Count() == 2,
            "distinct consumer manifest members");
        long delivered = 0;
        foreach (var item in manifest.Items)
        {
            bool disc = item.RelativePath == seed.RelativePath;
            Require(disc || item.RelativePath == "qualification-notes.txt", "exact manifest members");
            var original = disc ? seed.Original : seed.Companion;
            string filename = disc ? seed.OriginalFilename : "qualification-notes.txt";
            Require(item.Role == "rom" && item.IsAvailable && item.SizeBytes.Value == original.Size &&
                item.Sha256 == original.Sha256 && item.ContentGrant is not null, "legacy manifest original fields");
            Require(item.Formats.Select(format => format.Format).SequenceEqual(disc ? ["original", "rvz"] : ["original"]),
                "original first, and RVZ only where the member holds an RVZ artifact");
            Require(item.ContentGrant!.DownloadUrl.StartsWith("/delivery/content/", StringComparison.Ordinal),
                "relative legacy consumer grant route");
            await QualifyMediaFileAsync(anonymous, item.ContentGrant.DownloadUrl, original, filename, ct);
            delivered += original.Size;
            foreach (var format in item.Formats)
            {
                Require(format.Format is "original" or "rvz", "known consumer format");
                var facts = format.Format == "rvz" ? seed.Rvz! : original;
                string deliveredName = format.Format == "rvz" ? Path.ChangeExtension(filename, ".rvz") : filename;
                CheckMediaFormat(format, format.Format, deliveredName, facts);
                Require(format.RelativePath == (format.Format == "rvz" ? Path.ChangeExtension(item.RelativePath, ".rvz") :
                    item.RelativePath) && format.ContentGrant is not null, "format-relative path and bound grant");
                Require(format.ContentGrant!.DownloadUrl.StartsWith("/delivery/content/", StringComparison.Ordinal) &&
                    format.ContentGrant.ExpiresAt > DateTimeOffset.UtcNow, "fresh relative format grant");
                await QualifyMediaFileAsync(anonymous, format.ContentGrant.DownloadUrl, facts, deliveredName, ct);
                delivered += facts.Size;
            }
        }
        return delivered;
    }

    private static async Task QualifyMediaMetadataAsync(HttpClient client, VfsConnectionCredential credential,
        string relativePath, MediaQualificationFacts facts, MediaQualificationCounters counters,
        string fixture, string format,
        Action<string, string, string, long, int, MediaQualificationAccess?> observed, CancellationToken ct,
        IReadOnlyList<string>? operations = null)
    {
        string route = "/vfs/library/" + string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));
        var authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
            Encoding.UTF8.GetBytes(credential.Connection.Username + ":" + credential.Password)));
        string etag = "\"" + facts.Sha256 + "\"";
        foreach (string operation in operations ?? ["HEAD", "PROPFIND", "304", "412", "416"])
        {
            var before = counters.Snapshot();
            using var request = new HttpRequestMessage(operation switch
            {
                "HEAD" => HttpMethod.Head,
                "PROPFIND" => new HttpMethod("PROPFIND"),
                _ => HttpMethod.Get
            }, route);
            request.Headers.Authorization = authorization;
            if (operation == "304")
                request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
            if (operation == "412")
                request.Headers.IfMatch.Add(new EntityTagHeaderValue("\"qualification-mismatch\""));
            if (operation == "416")
                request.Headers.Range = new RangeHeaderValue(facts.Size, null);
            if (operation == "PROPFIND")
            {
                request.Headers.Add("Depth", "0");
                request.Content = new StringContent(
                    "<d:propfind xmlns:d=\"DAV:\" xmlns:o=\"http://owncloud.org/ns\"><d:prop><d:getcontentlength/><d:getetag/><o:checksums/></d:prop></d:propfind>",
                    Encoding.UTF8, "application/xml");
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            Require(response.StatusCode == (operation switch
            {
                "HEAD" => HttpStatusCode.OK,
                "PROPFIND" => HttpStatusCode.MultiStatus,
                "304" => HttpStatusCode.NotModified,
                "412" => HttpStatusCode.PreconditionFailed,
                _ => HttpStatusCode.RequestedRangeNotSatisfiable
            }), "VFS metadata HTTP status");
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            if (operation == "PROPFIND")
            {
                byte[] xml = await ReadSmallAsync(body, MaximumConfigurationBytes, ct);
                var document = XDocument.Parse(Encoding.UTF8.GetString(xml));
                XNamespace dav = "DAV:";
                XNamespace ownCloud = "http://owncloud.org/ns";
                Require(document.Descendants(dav + "response").Count() == 1 &&
                    document.Descendants(dav + "getcontentlength").SingleOrDefault()?.Value ==
                        facts.Size.ToString(CultureInfo.InvariantCulture) &&
                    document.Descendants(dav + "getetag").SingleOrDefault()?.Value == etag &&
                    document.Descendants(ownCloud + "checksum").SingleOrDefault()?.Value ==
                        "MD5:" + facts.Md5 + " SHA1:" + facts.Sha1, "VFS delivered length/ETag/ownCloud checksums");
            }
            else
            {
                Require(response.Headers.ETag?.Tag == etag, "VFS delivered-byte ETag");
                if (operation == "HEAD")
                    Require(response.Content.Headers.ContentLength == facts.Size, "HEAD delivered length");
                else
                    Require(response.Content.Headers.ContentLength is null or 0, "conditional metadata empty length");
                if (operation == "416")
                    Require(response.Content.Headers.ContentRange?.ToString() ==
                        "bytes */" + facts.Size.ToString(CultureInfo.InvariantCulture), "416 delivered-byte range length");
                Require(await body.ReadAsync(new byte[1], ct) == 0, "metadata response has zero body bytes");
            }
            var after = counters.Snapshot();
            Require(after == before, "metadata opens no CAS/decoder/resolver and reads no content");
            observed("vfs-" + operation.ToLowerInvariant(), fixture, format, 0, 0, new(0, 0, 0, 0, 0, 0, 0, 0));
        }
    }

    private static async Task QualifyMediaContentionAsync(HttpClient anonymous,
        IReadOnlyList<VfsConnectionCredential> ownerConnections, VfsConnectionCredential otherConnection,
        Guid ownerId, MediaDeliverySeed seed, MediaQualificationInput input, ConsumerVfsAdmission admission,
        ConsumerVfsOptions options, MediaQualificationCounters counters,
        Action<MediaQualificationContention> update,
        Action<string, string, string, long, int, MediaQualificationAccess?> observed, CancellationToken ct)
    {
        Require(options.MaximumConcurrentRequests == 8 && options.MaximumConcurrentRequestsPerUser == 4 &&
            options.MaximumConcurrentRequestsPerConnection == 4 && options.MaximumQueueWaitMilliseconds == 10_000 &&
            options.MaximumQueuedRequests == 128 && options.MaximumQueuedRequestsPerUser == 32 &&
            options.MaximumQueuedRequestsPerConnection == 16 && options.MaximumConcurrentMetadataRequests == 2,
            "actual production VFS admission defaults");
        Require(ownerConnections.Count == 2 && ownerConnections.Select(item => item.Connection.Id).Distinct().Count() == 2 &&
            otherConnection.Connection.Id != ownerConnections[0].Connection.Id &&
            otherConnection.Connection.Id != ownerConnections[1].Connection.Id, "three distinct issued connection identities");
        var evidence = new MediaQualificationContention("starting", options.MaximumConcurrentRequests,
            options.MaximumConcurrentRequestsPerUser, options.MaximumConcurrentRequestsPerConnection,
            options.MaximumQueueWaitMilliseconds, 0, 0, null, null, null,
            [new("owner-stored", "owner", "stored", 0, 0), new("owner-original", "owner", "original", 0, 0),
             new("other-stored", "other-account", "stored", 0, 0)]);
        update(evidence);
        var held = new List<IDisposable>(4);
        Task<HttpResponseMessage>? pending = null;
        CancellationTokenSource? pendingCancellation = null;
        Exception? primary = null;
        using var cancelledStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var timeoutStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var grantedStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        const int rangeBytes = 4096;
        const long offset = 32768;
        byte[] originalRange = await ReadMediaOracleRangeAsync(input.Iso, offset, rangeBytes, ct);
        byte[] rvzRange = await ReadMediaOracleRangeAsync(input.NormalizedRvz, offset, rangeBytes, ct);
        string originalRoute = MediaVfsRoute(seed.VfsPrefix + seed.RelativePath);
        string storedRoute = MediaVfsRoute(seed.VfsPrefix + Path.ChangeExtension(seed.RelativePath, ".rvz"));
        var originalCredential = ownerConnections.Single(item => item.Connection.Format == "original");
        using var cancelledRequest = MediaVfsRangeRequest(originalRoute, originalCredential, offset, rangeBytes);
        using var timeoutRequest = MediaVfsRangeRequest(originalRoute, originalCredential, offset, rangeBytes);
        using var grantedRequest = MediaVfsRangeRequest(originalRoute, originalCredential, offset, rangeBytes);

        async Task WaitForQueueAsync(int expected, CancellationToken token)
        {
            using var pollDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            pollDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (admission.QueuedRequestCount != expected)
            {
                evidence = evidence with
                {
                    MaximumObservedQueued = Math.Max(evidence.MaximumObservedQueued,
                    admission.QueuedRequestCount)
                };
                await Task.Delay(10, pollDeadline.Token);
            }
            evidence = evidence with { MaximumObservedQueued = Math.Max(evidence.MaximumObservedQueued, expected) };
            update(evidence);
        }

        try
        {
            foreach (var connection in ownerConnections)
                for (int count = 0; count < 2; count++)
                {
                    var lease = await admission.EnterAsync(ownerId, connection.Connection.Id, ct);
                    Require(lease is not null, "four controlled owner content leases are admitted");
                    held.Add(lease!);
                }
            Require(admission.QueuedRequestCount == 0, "held requests do not themselves queue");
            evidence = evidence with
            {
                Stage = "owner-held",
                MaximumHeldOwnerLeases = held.Count,
                Connections = evidence.Connections.Select(item => item.AccountAlias == "owner"
                    ? item with { ControlledHeldLeases = 2 } : item).ToArray()
            };
            update(evidence);

            using (var otherRequest = MediaVfsRangeRequest(storedRoute, otherConnection, offset, rangeBytes))
            using (var otherResponse = await anonymous.SendAsync(otherRequest,
                HttpCompletionOption.ResponseHeadersRead, ct))
                await QualifyMediaRangeResponseAsync(otherResponse, seed.Rvz!, offset, rvzRange, ct);
            evidence = evidence with
            {
                Stage = "other-account-progress",
                Connections = evidence.Connections.Select(item =>
                item.Alias == "other-stored" ? item with { VerifiedRangeBytes = rangeBytes } : item).ToArray()
            };
            update(evidence);
            observed("vfs-contention-other-account-range", seed.Name, "stored", rangeBytes, 0, null);

            // No real content request is concurrent with these global access-counter snapshots.
            foreach (var connection in ownerConnections)
            {
                bool stored = connection.Connection.Format == "stored";
                await QualifyMediaMetadataAsync(anonymous, connection,
                    seed.VfsPrefix + (stored ? Path.ChangeExtension(seed.RelativePath, ".rvz") : seed.RelativePath),
                    stored ? seed.Rvz! : seed.Original, counters, seed.Name, connection.Connection.Format,
                    (surface, fixture, format, bytes, members, delta) =>
                        observed("held-content-" + surface, fixture, format, bytes, members, delta), ct,
                    ["HEAD", "PROPFIND"]);
            }
            evidence = evidence with { Stage = "metadata-progress-with-owner-held" };
            update(evidence);

            var beforeCancellation = counters.Snapshot();
            long cancellationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            pendingCancellation = cancelledStop;
            pending = anonymous.SendAsync(cancelledRequest, HttpCompletionOption.ResponseHeadersRead, cancelledStop.Token);
            await WaitForQueueAsync(1, ct);
            Require(!pending.IsCompleted, "owner HTTP range waits across both connection identities at account cap");
            await cancelledStop.CancelAsync();
            try
            {
                using var unexpected = await pending;
                Require(false, "queued cancellation must not return a response");
            }
            catch (OperationCanceledException) when (cancelledStop.IsCancellationRequested && pending.IsCanceled) { }
            pending = null;
            await WaitForQueueAsync(0, ct);
            Require(counters.Snapshot() == beforeCancellation, "cancelled queued request opens no content");
            evidence = evidence with
            {
                Stage = "queue-cancelled",
                CancelledRequestMilliseconds =
                System.Diagnostics.Stopwatch.GetElapsedTime(cancellationStarted).TotalMilliseconds
            };
            update(evidence);

            var beforeTimeout = counters.Snapshot();
            long timeoutStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            pendingCancellation = timeoutStop;
            pending = anonymous.SendAsync(timeoutRequest, HttpCompletionOption.ResponseHeadersRead, timeoutStop.Token);
            await WaitForQueueAsync(1, ct);
            Require(!pending.IsCompleted, "timeout request is genuinely queued");
            using (var response = await pending)
            {
                double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(timeoutStarted).TotalMilliseconds;
                Require(response.StatusCode == HttpStatusCode.ServiceUnavailable &&
                    response.Headers.RetryAfter?.Delta is { } retry && retry > TimeSpan.Zero && retry <= TimeSpan.FromSeconds(15),
                    "actual queue timeout returns 503 and bounded Retry-After");
                Require(elapsed >= options.MaximumQueueWaitMilliseconds - 1000 &&
                    elapsed <= options.MaximumQueueWaitMilliseconds + 10_000, "observed actual ten-second queue deadline");
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                Require(await body.ReadAsync(new byte[1], ct) == 0 &&
                    response.Content.Headers.ContentLength is null or 0, "queue timeout has no body");
                evidence = evidence with { Stage = "queue-timed-out", TimeoutRequestMilliseconds = elapsed };
                update(evidence);
            }
            pending = null;
            await WaitForQueueAsync(0, ct);
            Require(counters.Snapshot() == beforeTimeout, "timed-out queued request opens no content");

            long grantStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            pendingCancellation = grantedStop;
            pending = anonymous.SendAsync(grantedRequest, HttpCompletionOption.ResponseHeadersRead, grantedStop.Token);
            await WaitForQueueAsync(1, ct);
            Require(!pending.IsCompleted, "grant request is queued before capacity is released");
            held[0].Dispose();
            held.RemoveAt(0);
            using (var response = await pending)
                await QualifyMediaRangeResponseAsync(response, seed.Original, offset, originalRange, ct);
            pending = null;
            await WaitForQueueAsync(0, ct);
            evidence = evidence with
            {
                Stage = "passed",
                GrantedRequestMilliseconds =
                System.Diagnostics.Stopwatch.GetElapsedTime(grantStarted).TotalMilliseconds,
                Connections = evidence.Connections.Select(item => item.Alias == "owner-original"
                    ? item with { VerifiedRangeBytes = rangeBytes } : item).ToArray()
            };
            update(evidence);
            observed("vfs-contention-owner-queued-then-granted-original-range", seed.Name, "original", rangeBytes, 0, null);
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            evidence = evidence with { Stage = "cleanup" };
            update(evidence);
            Exception? cleanupFailure = null;
            try
            {
                if (pending is not null)
                    await CancelAndDisposeResponseAsync(pending, pendingCancellation!, CancellationToken.None);
            }
            catch (Exception failure) { cleanupFailure = failure; }
            var cleanup = ReleaseContentLeasesAfterJoin(held, pending, primary, cleanupFailure);
            evidence = evidence with
            {
                Stage = cleanup.State == "completed" && primary is null ? "passed" : "failed",
                Cleanup = cleanup
            };
            update(evidence);
        }
    }

    private static string MediaVfsRoute(string relative) =>
        "/vfs/library/" + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));

    private static HttpRequestMessage MediaVfsRangeRequest(string route, VfsConnectionCredential credential,
        long offset, int bytes)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
            Encoding.UTF8.GetBytes(credential.Connection.Username + ":" + credential.Password)));
        request.Headers.Range = new RangeHeaderValue(offset, offset + bytes - 1);
        return request;
    }

    private static async Task<byte[]> ReadMediaOracleRangeAsync(string path, long offset, int bytes, CancellationToken ct)
    {
        Require(bytes is > 0 and <= 64 * 1024 && offset >= 0, "bounded qualification oracle range");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
        Require(offset + bytes <= input.Length, "oracle range lies within immutable file");
        input.Position = offset;
        byte[] expected = new byte[bytes];
        await input.ReadExactlyAsync(expected, ct);
        return expected;
    }

    private static async Task QualifyMediaRangeResponseAsync(HttpResponseMessage response,
        MediaQualificationFacts facts, long offset, byte[] expected, CancellationToken ct)
    {
        Require(response.StatusCode == HttpStatusCode.PartialContent &&
            response.Content.Headers.ContentLength == expected.Length &&
            response.Content.Headers.ContentRange?.ToString() ==
                $"bytes {offset}-{offset + expected.Length - 1}/{facts.Size}" &&
            response.Headers.ETag?.Tag == "\"" + facts.Sha256 + "\"", "actual range status and delivered-byte metadata");
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        byte[] actual = await ReadSmallAsync(body, expected.Length, ct);
        Require(actual.AsSpan().SequenceEqual(expected), "exact streamed range bytes and EOF");
    }

    private sealed partial class ConsumerHostFixture
    {
        public async Task<Guid> SeedMediaUserAsync(int libraryId, CancellationToken ct)
        {
            await using var db = CreateSetupDbContext();
            Guid id = Guid.NewGuid();
            string name = "media-qualification-" + id.ToString("N");
            db.Users.Add(new RomdUser
            {
                Id = id,
                UserName = name,
                NormalizedUserName = name.ToUpperInvariant(),
                Email = "media-user@localhost",
                NormalizedEmail = "MEDIA-USER@LOCALHOST",
                LibraryId = libraryId,
                CreatedAt = DateTimeOffset.UtcNow,
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                RequiresActivation = false,
                IsSuspended = false
            });
            await db.SaveChangesAsync(ct);
            return id;
        }

        public WebApplicationFactory<Romd.Admin.Host.Program> CreateBorrowedMediaAdminFactory(
            MediaDeliveryTransport transport)
        {
            var cas = Factory.Services.GetRequiredService<IContentAddressableStore>();
            var decoder = Factory.Services.GetRequiredService<IRomRepresentationDecoder>();
            return new WebApplicationFactory<Romd.Admin.Host.Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseContentRoot(AppContext.BaseDirectory);
                builder.UseWebRoot(Path.Combine(_tempDataDirectory, "wwwroot"));
                builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
                builder.UseSetting("Romd:DataDirectory", _tempDataDirectory);
                builder.UseSetting($"ConnectionStrings:{PostgreSqlConfiguration.RuntimeConnectionName}", _database.ConnectionString);
                builder.UseSetting("Romd:JwtSecret", TestJwtSecret);
                builder.UseSetting("Romd:JwtIssuer", TestJwtIssuer);
                builder.UseSetting("Romd:JwtAudience", "RomdMediaQualificationAdmin");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.AddHostedService<ServerInstanceIdentityInitializer>();
                    services.RemoveAll<IContentAddressableStore>();
                    services.AddSingleton(cas);
                    services.RemoveAll<IRomRepresentationDecoder>();
                    services.AddSingleton(decoder);
                    services.AddSingleton<IBackgroundJobClient>(transport);
                    services.AddRomdExportExecution();
                    services.AddRomdJobLifecyclePolicies();
                    services.AddScoped<JobRunner<Romd.Domain.Jobs.ExportJob>>();
                    services.AddTestAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                });
            });
        }

        public async Task<MediaDeliverySeed> SeedMediaDeliveryAsync(int libraryId, Guid actorId,
            MediaQualificationInput input, MediaQualificationDat dat, VfsProbeContent original, CancellationToken ct)
        {
            var cas = Factory.Services.GetRequiredService<IContentAddressableStore>();
            var originalFacts = MediaQualificationFacts.From(original);
            original.Stream.Position = 0;
            var originalStored = await cas.StoreAsync(original.Stream, ct: ct);
            Require(originalStored.Key.Hash.ToString() == originalFacts.Sha256 &&
                originalStored.Size == originalFacts.Size, "production CAS contains measured ISO");

            byte[] text = Encoding.UTF8.GetBytes("Disposable mixed-archive qualification companion for " + input.Name + "\n");
            MediaQualificationFacts companionFacts;
            StoreResult companionStored;
            await using (var companion = new MemoryStream(text, writable: false))
            {
                companionFacts = await MeasureAsync(companion, text.Length, ct);
                companion.Position = 0;
                companionStored = await cas.StoreAsync(companion, ct: ct);
            }
            var datFacts = await MeasureFileAsync(input.Dat, ct);
            StoreResult datStored;
            await using (var datStream = new FileStream(input.Dat, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                datStored = await cas.StoreAsync(datStream, ct: ct);
            Require(datStored.Key.Hash.ToString() == datFacts.Sha256, "stored measured DAT");

            await using var db = CreateSetupDbContext();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            async Task<FileEntityPersistence> StoreRecordAsync(StoreResult stored, MediaQualificationFacts facts,
                CancellationToken token)
            {
                var known = await db.Files.SingleOrDefaultAsync(file => file.Sha256 == stored.Key.Hash, token);
                if (known is not null)
                {
                    Require(known.Size == facts.Size && known.ArtifactMd5?.ToString() == facts.Md5 &&
                        known.ArtifactSha1?.ToString() == facts.Sha1, "existing measured file facts");
                    return known;
                }
                var file = new FileEntityPersistence
                {
                    Sha256 = stored.Key.Hash,
                    Size = stored.Size,
                    SizeOnDisk = stored.StoredSize,
                    IsCompressed = stored.IsCompressed,
                    ArtifactMd5 = Md5.Parse(facts.Md5),
                    ArtifactSha1 = Sha1.Parse(facts.Sha1),
                    CreatedAt = now,
                    CreatedByUserId = actorId
                };
                db.Files.Add(file);
                await db.SaveChangesAsync(token);
                return file;
            }
            var originalFile = await StoreRecordAsync(originalStored, originalFacts, ct);
            var companionFile = await StoreRecordAsync(companionStored, companionFacts, ct);
            var datFile = await StoreRecordAsync(datStored, datFacts, ct);
            var platform = new PlatformEntity
            {
                CanonicalKey = input.SystemKey,
                ShortName = input.SystemKey,
                Name = input.SystemKey == "gc" ? "GameCube" : "Wii",
                BaseName = input.SystemKey == "gc" ? "GameCube" : "Wii",
                BaseCompactLabel = input.SystemKey,
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
                BuiltInVersion = 1,
                CreatedAt = now,
                CreatedByUserId = actorId
            };
            db.Platforms.Add(platform);
            await db.SaveChangesAsync(ct);
            var title = new TitleEntity
            {
                PlatformId = platform.Id,
                Name = input.Name,
                NormalizedName = input.Name.ToLowerInvariant(),
                EnrichmentStatus = "Completed",
                CreatedAt = now,
                CreatedByUserId = actorId
            };
            var catalogSource = new CatalogSourceEntity
            { Kind = "Dat", Status = "Active", CreatedAt = now, CreatedByUserId = actorId };
            db.Titles.Add(title);
            db.CatalogSources.Add(catalogSource);
            await db.SaveChangesAsync(ct);
            var version = new DatFileEntity
            {
                Source = new DatSourceEntity
                { CatalogSourceId = catalogSource.Id, CreatedAt = now, CreatedByUserId = actorId },
                Name = input.Name,
                Description = "Disposable real-disc qualification with a synthetic ordinary companion",
                Type = "Redump",
                PlatformId = platform.Id,
                OriginalFilename = Path.GetFileName(input.Dat),
                FileId = datFile.Id,
                GameCount = 1,
                RomCount = 2,
                CreatedAt = now,
                CreatedByUserId = actorId
            };
            var sourceEntry = new SourceEntryEntity
            {
                CatalogSourceId = catalogSource.Id,
                EntryKey = input.DatId,
                Name = dat.GameName,
                PlatformId = platform.Id,
                HasLocalPayload = true,
                CreatedAt = now,
                CreatedByUserId = actorId
            };
            db.DatFiles.Add(version);
            db.SourceEntries.Add(sourceEntry);
            await db.SaveChangesAsync(ct);
            var game = new DatGameEntity
            {
                DatFileId = version.Id,
                SourceEntryId = sourceEntry.Id,
                Name = dat.GameName,
                CreatedAt = now,
                CreatedByUserId = actorId
            };
            db.DatGames.Add(game);
            db.TitleSourceLinks.Add(new TitleSourceLinkEntity
            { SourceEntryId = sourceEntry.Id, TitleId = title.Id, CreatedAt = now, CreatedByUserId = actorId });
            string relative = dat.RomName.Replace('\\', '/');
            string filename = relative.Split('/').Last();
            Require(ExportArchivePaths.SafeFilename(filename) == filename, "portable measured disc filename");
            var rom = new OriginalRomFixture
            {
                OriginalFilename = filename,
                FileId = originalFile.Id,
                ContentSha256 = Sha256.Parse(originalFacts.Sha256),
                ContentSize = originalFacts.Size,
                Sha1 = Sha1.Parse(originalFacts.Sha1),
                Md5 = Md5.Parse(originalFacts.Md5),
                Crc32 = Crc32.Parse(originalFacts.Crc32),
                CreatedAt = now,
                CreatedByUserId = actorId
            };
            var ordinary = new OriginalRomFixture
            {
                OriginalFilename = "qualification-notes.txt",
                FileId = companionFile.Id,
                ContentSha256 = Sha256.Parse(companionFacts.Sha256),
                ContentSize = companionFacts.Size,
                Sha1 = Sha1.Parse(companionFacts.Sha1),
                Md5 = Md5.Parse(companionFacts.Md5),
                Crc32 = Crc32.Parse(companionFacts.Crc32),
                CreatedAt = now,
                CreatedByUserId = actorId
            };
            db.StageOriginalRoms(rom, ordinary);
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync(ct);
            db.DatRoms.AddRange(
                new DatRomEntity
                {
                    DatGameId = game.Id,
                    Name = relative,
                    Size = originalFacts.Size,
                    Sha1 = rom.Sha1,
                    Md5 = rom.Md5,
                    Crc = rom.Crc32,
                    RomFileId = rom.Id,
                    CreatedAt = now,
                    CreatedByUserId = actorId
                },
                new DatRomEntity
                {
                    DatGameId = game.Id,
                    Name = ordinary.OriginalFilename,
                    Size = companionFacts.Size,
                    Sha1 = ordinary.Sha1,
                    Md5 = ordinary.Md5,
                    Crc = ordinary.Crc32,
                    RomFileId = ordinary.Id,
                    CreatedAt = now,
                    CreatedByUserId = actorId
                });
            await db.SaveChangesAsync(ct);
            var projection = new CatalogProjectionService(db,
                new CatalogSourceSnapshotReader([new DatCatalogSourceSnapshotProvider(db)]),
                new TitlePayloadAvailabilityProjection(db,
                    new CatalogPayloadAssertionReader(db, [new DatCatalogPayloadAssertionProvider(db)])),
            new Romd.Persistence.Catalog.DiscSetService(db),
            new Romd.Persistence.EfUnitOfWork(db),
                TimeProvider.System, NullLogger<CatalogProjectionService>.Instance);
            Require(await projection.RebuildPlatformAsync(platform.Id, ct), "actual catalog projection succeeds");
            int releaseId = await db.CatalogReleaseSources.Where(source => source.SourceEntryId == sourceEntry.Id)
                .Select(source => source.CatalogReleaseId).SingleAsync(ct);
            var release = await db.CatalogReleases.SingleAsync(row => row.Id == releaseId, ct);
            db.MaterializedLibraryReleases.Add(new MaterializedLibraryReleaseEntity
            {
                LibraryId = libraryId,
                TitleId = title.Id,
                PlatformId = platform.Id,
                CatalogReleaseId = releaseId,
                DatGameId = game.Id,
                DatFileId = version.Id,
                IsOwned = true,
                IsExposed = true,
                IsComplete = true,
                IsEligible = true,
                IsPlayable = true,
                ExposureReason = "ExposedDefault"
            });
            db.MaterializedLibraryTitles.Add(new MaterializedLibraryTitleEntity
            {
                LibraryId = libraryId,
                TitleId = title.Id,
                PlatformId = platform.Id,
                IsVisible = true,
                IsOwned = true,
                IsPlayable = true,
                EligibleReleaseCount = 1,
                PlayableReleaseCount = 1,
                ExposedReleaseCount = 1,
                Availability = LibraryTitleAvailability.Playable.ToString()
            });
            await db.SaveChangesAsync(ct);
            string prefix = IdCoder.Encode(platform.Id) + " " + PortablePathLabel.Create(platform.Name, "Platform") +
                "/" + IdCoder.Encode(releaseId) + " " + PortablePathLabel.Create(release.Name, "Release") + "/";
            Require(await db.CatalogReleaseFiles.CountAsync(row => row.CatalogReleaseId == releaseId, ct) == 2,
                "projected mixed release has exactly two members");
            return new(input.Name, input.SystemKey, rom.Id, originalFile.Id, title.Id, releaseId, platform.Name,
                title.Name, filename, relative, prefix, originalFacts, companionFacts);
        }

        public async Task AssertMediaPublicationAsync(MediaDeliverySeed seed, VfsProbeContent rvz, CancellationToken ct)
        {
            await using var db = CreateSetupDbContext();
            var descriptor = await Romd.PostgreSql.TestSupport.MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(seed.RomId, ct);
            Require(descriptor is not null && descriptor.Encoding == RomRepresentationEncoding.Rvz &&
                descriptor.ContentSha256.ToString() == seed.Original.Sha256 && descriptor.ContentSize == seed.Original.Size &&
                descriptor.ArtifactSha256.ToString() == Convert.ToHexStringLower(rvz.Sha256) &&
                descriptor.ArtifactSize == rvz.Length && descriptor.FileId != seed.OriginalFileId,
                "preferred retained RVZ preserves original identity");
            Require(RomMediaPolicyComposition.CreateRegistry().Classify(descriptor!.Preferred.Container, null) ==
                RomRepresentationProfileClass.Canonical && descriptor.Artifacts.Count == 1 &&
                descriptor.Artifacts.All(artifact => artifact.FileId != seed.OriginalFileId),
                "one owned canonical RVZ and original output released");
            var proof = descriptor.Preferred.Facts.Verifications.Single();
            var measured = proof.Outputs.Single().Identity;
            Require(proof.CheckedProfile == RvzVerifier.Profile && measured.Sha256.ToString() == seed.Original.Sha256 &&
                measured.Size == seed.Original.Size && measured.Sha1.ToString() == seed.Original.Sha1 &&
                measured.Md5.ToString() == seed.Original.Md5 && measured.Crc32.ToString() == seed.Original.Crc32,
                "actual supervised measured verification evidence");
        }

        public async Task ReclaimMediaOriginalAsync(WebApplicationFactory<Romd.Admin.Host.Program> adminFactory,
            MediaDeliverySeed seed, VfsProbeContent rvz, CancellationToken ct)
        {
            await using var scope = adminFactory.Services.CreateAsyncScope();
            Require(await scope.ServiceProvider.GetRequiredService<IFileStorageService>()
                .DeleteIfUnreferencedAsync(seed.OriginalFileId, ct), "reclaim released test-owned original through file ledger");
            await using var db = CreateSetupDbContext();
            var cas = Factory.Services.GetRequiredService<IContentAddressableStore>();
            Require(!await db.Files.AnyAsync(file => file.Id == seed.OriginalFileId, ct) &&
                !await cas.ExistsAsync(StorageKey.FromHash(Sha256.Parse(seed.Original.Sha256)), ct),
                "released original row and physical CAS copy are absent");
            var retained = (await MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(seed.RomId, ct))!.FileId;
            var retainedHash = Sha256.FromSpan(rvz.Sha256);
            Require(await db.Files.AnyAsync(file => file.Id == retained && file.Sha256 == retainedHash, ct) &&
                await cas.ExistsAsync(StorageKey.FromHash(retainedHash), ct),
                "retained measured RVZ row and physical artifact survive original cleanup");
        }

        public async Task AssertMediaConnectionAsync(Guid userId, Guid connectionId, string format, CancellationToken ct)
        {
            await using var db = CreateSetupDbContext();
            Require(await db.VfsConnections.AnyAsync(connection => connection.Id == connectionId &&
                connection.UserId == userId && connection.Format == format && connection.RevokedAt == null, ct),
                "actual account-owned persisted VFS connection");
        }

        public async Task<Guid> RunMediaExportAsync(WebApplicationFactory<Romd.Admin.Host.Program> adminFactory,
            HttpClient admin, MediaDeliveryTransport transport, Guid actorId, int libraryId, string format,
            int expectedFiles, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/export/library")
            {
                Content = JsonContent.Create(new Romd.Contracts.Management.Export.ExportLibraryRequest
                { LibraryId = IdCoder.Encode(libraryId), Format = format })
            };
            using var response = await admin.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            Require(response.StatusCode == HttpStatusCode.Accepted, "actual durable library export acceptance");
            var accepted = await ReadMediaJsonAsync<MediaDeliveryAccepted>(response, ct);
            Require(accepted.JobId != Guid.Empty, "accepted durable export identity");
            await using (var scope = adminFactory.Services.CreateAsyncScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IJobRepository<Romd.Domain.Jobs.ExportJob>>();
                var job = await repo.GetByIdAsync(accepted.JobId, ct);
                var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
                long generation = await context.Libraries.Where(library => library.Id == libraryId)
                    .Select(library => library.MaterializationGeneration).SingleAsync(ct);
                Require(job is not null && job.Phase == "Pending" && job.CreatedByUserId == actorId &&
                    job.LibraryId == libraryId && job.AuthorizedMaterializationGeneration == generation &&
                    job.ScopeKind == Romd.Domain.Jobs.ExportScopeKind.Library &&
                    job.Format == (format == "rvz" ? RomDeliveryFormat.Rvz : RomDeliveryFormat.Original),
                    "persisted format actor library and authorization generation");
                Require(await context.JobDispatches.AnyAsync(dispatch => dispatch.JobId == accepted.JobId &&
                    dispatch.JobType == "export" && dispatch.DeliveredAtUtc == null, ct), "committed export dispatch intent");
                int prior = transport.Created;
                await scope.ServiceProvider.GetRequiredService<JobDispatchService>().DispatchAsync(accepted.JobId, ct);
                Require(transport.Created == prior + 1 && transport.Invocation?.Type == typeof(ExportJobHangfireHandler) &&
                    transport.Invocation.Method.Name == nameof(ExportJobHangfireHandler.ExecuteAsync) &&
                    transport.Invocation.Args.Count == 2 && transport.Invocation.Args[0] is Guid invocationId &&
                    invocationId == accepted.JobId && transport.State is EnqueuedState enqueued &&
                    enqueued.Queue == "default", "real dispatch invocation with controlled transport");
                Require(await context.JobDispatches.AnyAsync(dispatch => dispatch.JobId == accepted.JobId &&
                    dispatch.DeliveredAtUtc != null && dispatch.HangfireJobId == transport.DeliveryId, ct),
                    "durable dispatch acknowledgement");
            }
            await using (var execution = adminFactory.Services.CreateAsyncScope())
                await execution.ServiceProvider.GetRequiredService<JobRunner<Romd.Domain.Jobs.ExportJob>>()
                    .RunAsync(accepted.JobId, transport.DeliveryId!, ct);
            await using (var verification = adminFactory.Services.CreateAsyncScope())
            {
                var job = await verification.ServiceProvider.GetRequiredService<IJobRepository<Romd.Domain.Jobs.ExportJob>>()
                    .GetByIdAsync(accepted.JobId, ct);
                Require(job is not null && job.Phase == "Completed" && job.IsTerminal && job.Errors.Count == 0 &&
                    job.TotalFiles == expectedFiles && job.ProcessedFiles == expectedFiles && job.SkippedFiles == 0 &&
                    job.TotalTitles == expectedFiles / 2 && job.ProcessedTitles == expectedFiles / 2 &&
                    job.ExportPath is not null && File.Exists(job.ExportPath) && new FileInfo(job.ExportPath).Length > 0,
                    "actual claimed export executor completed every member without errors");
                var context = verification.ServiceProvider.GetRequiredService<RomdDbContext>();
                Require(await context.Jobs.AnyAsync(row => row.Id == accepted.JobId &&
                    row.CompletedAt != null && row.ExecutionFenceToken == null && row.ExecutionLeaseExpiresAtUtc == null, ct),
                    "terminal export persisted and execution lease released");
            }
            return accepted.JobId;
        }
    }

    private sealed record MediaDeliveryAccepted(Guid JobId);
}
