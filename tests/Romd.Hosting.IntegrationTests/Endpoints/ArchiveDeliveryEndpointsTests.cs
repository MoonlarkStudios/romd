using Romd.Admin.Application.Export.Commands.QueueLibraryExport;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.PostgreSql.TestSupport;
using Romd.Contracts.Management.Export;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Systems;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ErrorOr;
using Romd.Admin.Application.Export.Queries.ResolveExportScope;
using Romd.Admin.Application.Export.Queries.AuthorizeExportDownload;
using Romd.Admin.Application.Export.Queries.PrepareReleaseDownload;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Domain.Jobs;
using Romd.Host.Endpoints;
using Romd.Infrastructure.Export;
using Shouldly;
using Xunit;
using Romd.Infrastructure.Storage;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class ArchiveDeliveryEndpointsTests
{
    private static readonly RomDeliveryPolicy DeliveryPolicy = RomMediaPolicyComposition.CreateDeliveryPolicy();

    [Fact]
    public async Task PrepareTitleDownload_TooManyMembers_ReturnsValidationBeforeCatalogStorageOrTicket()
    {
        var files = Substitute.For<IExportRepository>();
        files.GetExportFilesForTitleAsync(1, Arg.Any<AuthorizedExportScope>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, 10_001).Select(id => new ExportFileData(
                1, "Title", 1, "Platform", id, id, $"game{id}.bin")).ToArray());
        var scope = Substitute.For<IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope>>();
        scope.HandleAsync(Arg.Any<ResolveExportScopeQuery>(), Arg.Any<CancellationToken>()).Returns(
            (ErrorOr<AuthorizedExportScope>)new AuthorizedExportScope(new ExportScope.AllCatalog(), null));
        var catalog = Substitute.For<IRomContentCatalog>();
        catalog.FindByRomIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(call => (IReadOnlyDictionary<int, RomContentDescriptor>)call.ArgAt<IReadOnlyCollection<int>>(0).ToDictionary(id => id, id =>
        {
            var hash = Sha256.Parse(id.ToString("x64"));
            var identity = new RomLogicalIdentity(1, hash, Sha1.Parse(new string('a', 40)), Md5.Parse(new string('a', 32)), Crc32.FromUInt32(1));
            var original = ManagedArtifact(id, id, id, id, identity, hash, 1, true, RomContainerFacts.Identity.Instance);
            return new RomContentDescriptor(id, id, identity, $"game{id}.bin", [original], [], []);
        }));
        var storage = Substitute.For<IFileStorageService>();
        storage.ExistsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        var tickets = Substitute.For<IAdminDownloadTickets>();
        tickets.Issue(Arg.Any<AdminDownloadPlan>()).Returns(new AdminDownloadTicket("/ticket", DateTimeOffset.UtcNow));

        var result = await Invoke("PrepareTitleDownload", 1, RomDeliveryFormat.Original, new DefaultHttpContext(), files,
            scope, catalog, storage, tickets, ReferenceCatalog(), new SyntheticAdmission(new("linux-x64", Sha1.Parse(new string('f', 40)), 1)), DeliveryPolicy, CancellationToken.None);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(400);
        catalog.ReceivedCalls().ShouldBeEmpty();
        storage.ReceivedCalls().ShouldBeEmpty();
        tickets.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task PrepareExportDownload_RelativeServerOwnedPath_BindsAbsolutePathWithoutChangingCwd()
    {
        string path = Path.Combine(Path.GetTempPath(), "romd-relative-export-" + Guid.NewGuid().ToString("N") + ".zip");
        string cwd = Environment.CurrentDirectory;
        try
        {
            await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });
            string relative = Path.GetRelativePath(cwd, path);
            var job = ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow);
            job.Start("delivery", startedAt: DateTimeOffset.UtcNow);
            job.BeginPackaging();
            job.BeginStoring();
            job.SetExportPath(relative);
            job.Complete(completedAt: DateTimeOffset.UtcNow);
            var handler = Substitute.For<IQueryHandler<AuthorizeExportDownloadQuery, ExportJob>>();
            handler.HandleAsync(new(job.Id), Arg.Any<CancellationToken>()).Returns((ErrorOr<ExportJob>)job);
            using var tickets = new AdminDownloadTickets(TimeProvider.System);

            var result = await Invoke("PrepareExportJobDownload", job.Id, new DefaultHttpContext(), handler, tickets, CancellationToken.None);

            var issued = result.ShouldBeAssignableTo<IValueHttpResult<AdminDownloadTicket>>().Value.ShouldNotBeNull();
            tickets.Resolve(issued.DownloadUrl.Split('/').Last()).ShouldNotBeNull().CompletedExportPath.ShouldBe(Path.GetFullPath(path));
            Environment.CurrentDirectory.ShouldBe(cwd);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(RomDeliveryFormat.Original)]
    [InlineData(RomDeliveryFormat.Rvz)]
    public async Task PrepareTitleDownload_MixedMembers_BindsZipPathsAndRedemptionSnapshot(RomDeliveryFormat selected)
    {
        byte[] logicalBytes = [1, 2, 3, 4];
        byte[] rvzBytes = [7, 8];
        byte[] manualBytes = [9];
        var logical = Sha256.FromSpan(SHA256.HashData(logicalBytes));
        var artifact = Sha256.FromSpan(SHA256.HashData(rvzBytes));
        var manual = Sha256.FromSpan(SHA256.HashData(manualBytes));
        var files = Substitute.For<IExportRepository>();
        files.GetExportFilesForTitleAsync(1, Arg.Any<AuthorizedExportScope>(), Arg.Any<CancellationToken>()).Returns([
            new ExportFileData(1, "Title", 1, "Platform", 7, 3, "disc.rvz"),
            new ExportFileData(1, "Title", 1, "Platform", 8, 4, "manual.bin")
        ]);
        var catalog = Substitute.For<IRomContentCatalog>();
        SetCatalogContent(catalog, new RomContentDescriptor(7, 183, MediaFactFixtures.Identity(logicalBytes), "disc.iso",
                [ManagedArtifact(7, 83, 3, 183, MediaFactFixtures.Identity(logicalBytes), artifact, 2, true,
                    MediaFactFixtures.Rvz(4, 1, 5, 19, 131072, false))], [], []),
            new RomContentDescriptor(8, 184, MediaFactFixtures.Identity(manualBytes), "manual.bin",
                [ManagedArtifact(8, 84, 4, 184, MediaFactFixtures.Identity(manualBytes), manual, 1, true,
                    RomContainerFacts.Identity.Instance)], [], []));
        var scope = Substitute.For<IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope>>();
        scope.HandleAsync(Arg.Any<ResolveExportScopeQuery>(), Arg.Any<CancellationToken>()).Returns(
            (ErrorOr<AuthorizedExportScope>)new AuthorizedExportScope(new ExportScope.AllCatalog(), null));
        var storage = Substitute.For<IFileStorageService>();
        storage.ExistsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        using var tickets = new AdminDownloadTickets(TimeProvider.System);
        var prepared = await Invoke("PrepareTitleDownload", 1, selected, new DefaultHttpContext(), files,
            scope, catalog, storage, tickets, ReferenceCatalog(), new SyntheticAdmission(new("linux-x64", Sha1.Parse(new string('f', 40)), 1)), DeliveryPolicy, CancellationToken.None);
        var ticket = prepared.ShouldBeAssignableTo<IValueHttpResult<AdminDownloadTicket>>().Value.ShouldNotBeNull();
        string token = ticket.DownloadUrl.Split('/').Last();
        var plan = tickets.Resolve(token).ShouldNotBeNull();
        plan.Entries[0].Path.ShouldBe(selected == RomDeliveryFormat.Rvz ? "disc.rvz" : "disc.iso");
        plan.Entries[0].Target.ShouldNotBeNull().Format.ShouldBe(selected);
        plan.Entries[1].Target.ShouldNotBeNull().Format.ShouldBe(RomDeliveryFormat.Original);
        files.ClearReceivedCalls();
        catalog.ClearReceivedCalls();
        storage.ClearReceivedCalls();
        catalog.FindByRomIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, RomContentDescriptor>());
        var reader = Substitute.For<IRomContentReader>();
        foreach (var entry in plan.Entries)
        {
            var data = entry.RomId == 8 ? manualBytes : selected == RomDeliveryFormat.Rvz ? rvzBytes : logicalBytes;
            reader.OpenAsync(entry.Target!, Arg.Any<CancellationToken>(), TimeSpan.FromSeconds(30))
                .Returns(_ => new MemoryStream(data));
        }

        var context = new DefaultHttpContext();
        var redeemed = await Invoke("RedeemDownload", token, context, tickets,
            new ExportArchiveWriter(storage, reader), storage, reader, CancellationToken.None);
        byte[] zip = await ExecuteAsync(redeemed, context);
        using var archive = new ZipArchive(new MemoryStream(zip));
        archive.Entries.Select(entry => entry.FullName).ShouldBe(plan.Entries.Select(entry => entry.Path));
        using var game = new MemoryStream();
        await archive.Entries[0].Open().CopyToAsync(game);
        game.ToArray().ShouldBe(selected == RomDeliveryFormat.Rvz ? rvzBytes : logicalBytes);
        catalog.ReceivedCalls().ShouldBeEmpty();
        files.ReceivedCalls().ShouldBeEmpty();
        storage.ReceivedCalls().ShouldBeEmpty();
        context.Response.Headers.CacheControl.ToString().ShouldBe("private, no-store");
    }

    [Fact]
    public async Task PrepareTitleDownload_AuthorizationDenied_DoesNotSelectMembersOrReadCatalog()
    {
        var files = Substitute.For<IExportRepository>();
        var scope = Substitute.For<IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope>>();
        scope.HandleAsync(Arg.Any<ResolveExportScopeQuery>(), Arg.Any<CancellationToken>())
            .Returns((ErrorOr<AuthorizedExportScope>)ExportErrors.AccessDenied());
        var catalog = Substitute.For<IRomContentCatalog>();
        var storage = Substitute.For<IFileStorageService>();
        var tickets = Substitute.For<IAdminDownloadTickets>();

        var result = await Invoke("PrepareTitleDownload", 1, RomDeliveryFormat.Rvz, new DefaultHttpContext(), files,
            scope, catalog, storage, tickets, ReferenceCatalog(), new SyntheticAdmission(new("linux-x64", Sha1.Parse(new string('f', 40)), 1)), DeliveryPolicy, CancellationToken.None);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(403);
        files.ReceivedCalls().ShouldBeEmpty();
        catalog.ReceivedCalls().ShouldBeEmpty();
        storage.ReceivedCalls().ShouldBeEmpty();
        tickets.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("RVZ")]
    [InlineData("Original")]
    [InlineData("1")]
    [InlineData(" rvz")]
    [InlineData("rvz ")]
    [InlineData("null")]
    [InlineData("")]
    public async Task ArchiveEndpoints_InvalidSelection_RejectBeforeCatalogOrScheduling(string format)
    {
        var files = Substitute.For<IExportRepository>();
        var scope = Substitute.For<IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope>>();
        var catalog = Substitute.For<IRomContentCatalog>();
        var storage = Substitute.For<IFileStorageService>();
        var tickets = Substitute.For<IAdminDownloadTickets>();
        var scheduler = Substitute.For<ICommandHandler<QueueLibraryExportCommand, Guid>>();
        var release = Substitute.For<IQueryHandler<PrepareReleaseDownloadQuery, AdminDownloadTicket>>();
        var roms = Substitute.For<Romd.Admin.Application.Source.Rom.IRomRepository>();
        var results = new List<IResult>
        {
            await Invoke("ExportLibrary", new ExportLibraryRequest { Format = format }, scheduler, CancellationToken.None)
        };
        foreach (var target in Enum.GetValues<Romd.Contracts.Management.Export.DownloadTicketTarget>().Where(target => target != Romd.Contracts.Management.Export.DownloadTicketTarget.ExportJob))
        {
            var ticket = await Invoke("CreateDownloadTicket",
                new Romd.Contracts.Management.Export.CreateDownloadTicketRequest(target, IdCoder.Encode(1), format),
                new DefaultHttpContext(), files, scope, catalog, storage, tickets, release, roms,
                Substitute.For<ICurrentUser>(), Substitute.For<IExportAuthorizationReader>(), ReferenceCatalog(),
                new SyntheticAdmission(new("linux-x64", Sha1.Parse(new string('f', 40)), 1)), DeliveryPolicy, Substitute.For<IQueryHandler<AuthorizeExportDownloadQuery, ExportJob>>(), CancellationToken.None);
            // The ticket is validated in one stage, so the rejected format is keyed by its field.
            ticket.ShouldBeAssignableTo<IValueHttpResult<Microsoft.AspNetCore.Mvc.ProblemDetails>>().ShouldNotBeNull()
                .Value.ShouldNotBeNull().Extensions[ProblemResults.ErrorsExtensionName]
                .ShouldBeAssignableTo<IReadOnlyDictionary<string, string[]>>().ShouldNotBeNull().Keys.ShouldBe(["format"]);
            results.Add(ticket);
        }
        foreach (var result in results)
            result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(400);
        files.ReceivedCalls().ShouldBeEmpty();
        scope.ReceivedCalls().ShouldBeEmpty();
        catalog.ReceivedCalls().ShouldBeEmpty();
        storage.ReceivedCalls().ShouldBeEmpty();
        tickets.ReceivedCalls().ShouldBeEmpty();
        scheduler.ReceivedCalls().ShouldBeEmpty();
        release.ReceivedCalls().ShouldBeEmpty();
        roms.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("missing")]
    [InlineData("revoked")]
    public async Task PrepareExportDownload_CompletedCapability_HonorsSnapshotExpiryMissingAndIssuanceAuthorization(string change)
    {
        string path = Path.Combine(Path.GetTempPath(), "romd-completed-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            byte[] bytes;
            using (var buffer = new MemoryStream())
            {
                using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    await using var member = zip.CreateEntry("member.bin").Open();
                    await member.WriteAsync(new byte[] { 1, 2, 3 });
                }
                bytes = buffer.ToArray();
            }
            await File.WriteAllBytesAsync(path, bytes);
            var job = ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow, format: RomDeliveryFormat.Rvz);
            job.Start("delivery", startedAt: DateTimeOffset.UtcNow);
            job.BeginPackaging();
            job.BeginStoring();
            job.SetExportPath(path);
            job.Complete(completedAt: DateTimeOffset.UtcNow);
            var authorization = Substitute.For<IQueryHandler<AuthorizeExportDownloadQuery, ExportJob>>();
            authorization.HandleAsync(new(job.Id), Arg.Any<CancellationToken>()).Returns((ErrorOr<ExportJob>)job);
            var clock = new Clock();
            using var tickets = new AdminDownloadTickets(clock);
            var prepared = await Invoke("PrepareExportJobDownload", job.Id, new DefaultHttpContext(), authorization, tickets, CancellationToken.None);
            var ticket = prepared.ShouldBeAssignableTo<IValueHttpResult<AdminDownloadTicket>>().Value.ShouldNotBeNull();
            ticket.DownloadUrl.ShouldNotContain(path);
            string token = ticket.DownloadUrl.Split('/').Last();
            tickets.Resolve(token).ShouldNotBeNull().CompletedExportPath.ShouldBe(path);
            var writer = Substitute.For<IExportArchiveWriter>();
            var storage = Substitute.For<IFileStorageService>();
            var reader = Substitute.For<IRomContentReader>();
            authorization.HandleAsync(new(job.Id), Arg.Any<CancellationToken>())
                .Returns((ErrorOr<ExportJob>)ExportErrors.AccessDenied());
            var denied = await Invoke("PrepareExportJobDownload", job.Id, new DefaultHttpContext(), authorization, tickets, CancellationToken.None);
            denied.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(403);
            if (change == "expired")
                clock.Advance(TimeSpan.FromMinutes(2));
            if (change == "missing")
                File.Delete(path);

            var context = new DefaultHttpContext();
            var redeemed = await Invoke("RedeemDownload", token, context, tickets, writer, storage, reader, CancellationToken.None);

            if (change == "revoked")
                (await ExecuteAsync(redeemed, context)).ShouldBe(bytes);
            else
            {
                redeemed.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(404);
                context.Response.ContentLength.ShouldBeNull();
            }
            await authorization.Received(2).HandleAsync(new(job.Id), CancellationToken.None);
            writer.ReceivedCalls().ShouldBeEmpty();
            storage.ReceivedCalls().ShouldBeEmpty();
            reader.ReceivedCalls().ShouldBeEmpty();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task RedeemArchive_BoundOriginalCapturedArtifactReleased_UsesBoundLogicalReader()
    {
        byte[] bytes = [1, 2, 3];
        var hash = Sha256.FromSpan(SHA256.HashData(bytes));
        var target = new RomDeliveryTarget(7, 3, RomDeliveryFormat.Original, "disc.iso",
            hash, 3, hash, 3, hash, 3, RomRepresentationEncoding.Identity);
        var tickets = Substitute.For<IAdminDownloadTickets>();
        tickets.Resolve("token").Returns(new AdminDownloadPlan("Release.zip", [new(3, "disc.iso", 7, target)], true));
        var storage = Substitute.For<IFileStorageService>();
        storage.ExistsAsync(3, Arg.Any<CancellationToken>()).Returns(false);
        var reader = Substitute.For<IRomContentReader>();
        reader.OpenAsync(target, Arg.Any<CancellationToken>(), TimeSpan.FromSeconds(30))
            .Returns(_ => new MemoryStream(bytes));
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        using var output = new MemoryStream();
        context.Response.Body = output;

        var result = await Invoke("RedeemDownload", "token", context, tickets,
            new ExportArchiveWriter(storage, reader), storage, reader, CancellationToken.None);

        result.ShouldBeAssignableTo<PushStreamHttpResult>();
        await result.ExecuteAsync(context);
        output.Position = 0;
        using var archive = new ZipArchive(output);
        using var content = new MemoryStream();
        await archive.Entries.ShouldHaveSingleItem().Open().CopyToAsync(content);
        content.ToArray().ShouldBe(bytes);
        await reader.Received(1).OpenAsync(target, CancellationToken.None, TimeSpan.FromSeconds(30));
    }

    private static async Task<IResult> Invoke(string method, params object?[] arguments) =>
        await (Task<IResult>)typeof(ExportEndpoints).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, arguments)!;

    private static async Task<byte[]> ExecuteAsync(IResult result, HttpContext context)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.RequestServices = services;
        context.Request.Method = "GET";
        using var output = new MemoryStream();
        context.Response.Body = output;
        await result.ExecuteAsync(context);
        return output.ToArray();
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    private static IReferenceCatalogService ReferenceCatalog()
    {
        var reference = Substitute.For<IReferenceCatalogService>();
        reference.GetSystemKeysAsync(Arg.Any<CancellationToken>()).Returns(new SystemKeys(new Dictionary<int, string> { [1] = "ps2" }));
        reference.ResolveSystemIdAsync("ps2", Arg.Any<CancellationToken>()).Returns((int?)1);
        return reference;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrepareTitleDownload_AdmittedPs2Chd_BindsArtifactOrRefusesDisappearance(bool exists)
    {
        var files = Substitute.For<IExportRepository>();
        files.GetExportFilesForTitleAsync(1, Arg.Any<AuthorizedExportScope>(), Arg.Any<CancellationToken>()).Returns([
            new ExportFileData(1, "Title", 1, "Display Name", 7, 12, "disc.iso")]);
        var scope = Substitute.For<IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope>>();
        scope.HandleAsync(Arg.Any<ResolveExportScopeQuery>(), Arg.Any<CancellationToken>()).Returns(
            (ErrorOr<AuthorizedExportScope>)new AuthorizedExportScope(new ExportScope.AllCatalog(), null));
        var content = SyntheticChdContent();
        var catalog = Substitute.For<IRomContentCatalog>();
        SetCatalogContent(catalog, content);
        var storage = Substitute.For<IFileStorageService>();
        storage.ExistsAsync(12, Arg.Any<CancellationToken>()).Returns(exists);
        using var tickets = new AdminDownloadTickets(TimeProvider.System);
        var result = await Invoke("PrepareTitleDownload", 1, RomDeliveryFormat.Chd, new DefaultHttpContext(), files, scope, catalog,
            storage, tickets, ReferenceCatalog(), new SyntheticAdmission(content.Artifacts![0].ChdEvidence![0].Decoder),
            DeliveryPolicy, CancellationToken.None);
        if (exists)
        {
            var ticket = result.ShouldBeAssignableTo<IValueHttpResult<AdminDownloadTicket>>().Value.ShouldNotBeNull();
            var entry = tickets.Resolve(ticket.DownloadUrl.Split('/').Last()).ShouldNotBeNull().Entries.ShouldHaveSingleItem();
            entry.Path.ShouldBe("disc.chd");
            entry.PlatformKeys.ShouldBe(["ps2"]);
            entry.Target.ShouldNotBeNull().Format.ShouldBe(RomDeliveryFormat.Chd);
            entry.Target.Sha256.ShouldBe(content.ArtifactSha256);
            entry.Target.SizeBytes.ShouldBe(4096);
            entry.Target.ReadBinding.ShouldBe(content.Artifacts![0].ChdEvidence![0].Bind());
        }
        else
            result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(409);
        await storage.Received(1).ExistsAsync(12, CancellationToken.None);
    }

    private static RomContentArtifact ManagedArtifact(int romId, int representationId, int fileId, int outputId,
        RomLogicalIdentity logical, Sha256 artifact, long artifactBytes, bool preferred, RomContainerFacts container)
    {
        var measurement = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), artifact, artifactBytes, logical, container,
            new RomDecoderIdentity.Managed("endpoint-fixture-managed/v1"), "logical-sha256/v1", "endpoint-fixture-verifier/v1",
            new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero), null, null);
        return MediaDeliveryFactFixtures.MeasuredWhole(representationId, fileId, outputId, romId, preferred,
            container, measurement, null, null);
    }

    private static RomContentDescriptor OriginalAndRvz(int romId, Sha256 logicalSha256, long logicalBytes,
        int originalFileId, int rvzFileId, Sha256 rvzSha256, long rvzBytes)
    {
        var identity = new RomLogicalIdentity(logicalBytes, logicalSha256, Sha1.Parse(new string('a', 40)),
            Md5.Parse(new string('a', 32)), Crc32.Parse("aaaaaaaa"));
        var original = ManagedArtifact(romId, 81, originalFileId, 181, identity, logicalSha256, logicalBytes, true,
            RomContainerFacts.Identity.Instance);
        var rvz = ManagedArtifact(romId, 82, rvzFileId, 182, identity, rvzSha256, rvzBytes, false,
            MediaFactFixtures.Rvz(logicalBytes, 1, 5, 19, 131072, false));
        return new(romId, original.OutputId, identity, "disc.iso", [original, rvz], [], []);
    }

    private static RomContentDescriptor SyntheticChdContent(int romId = 7, int fileId = 12)
    {
        var logical = new RomLogicalIdentity(8192, Sha256.Parse(new string('a', 64)), Sha1.Parse(new string('a', 40)),
            Md5.Parse(new string('a', 32)), Crc32.Parse("aaaaaaaa"));
        var artifact = Sha256.Parse(new string('b', 64));
        var decoder = new RomChdDecoderBuild("linux-x64", Sha1.Parse(new string('f', 40)), 1);
        var overall = Sha1.Parse(new string('c', 40));
        var container = MediaFactFixtures.Chd(logical.Size, 4096, 2048, [0x7a6c6962, 0, 0, 0],
            logical.Sha1, overall, [new(0x44564420, 1, new byte[] { 0 })]);
        var measurement = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), artifact, 4096, logical, container,
            new RomDecoderIdentity.Native(decoder), "chd-logical/v1", "endpoint-fixture-verifier/v1",
            new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero).AddTicks(42), logical.Sha1, overall);
        var selected = MediaDeliveryFactFixtures.MeasuredWhole(71, fileId, 171, romId, true, container, measurement,
            Md5.Parse(new string('b', 32)), Sha1.Parse(new string('b', 40)));
        return new(romId, selected.OutputId, logical, "disc.iso", [selected], [], []);
    }

    private sealed class SyntheticAdmission(RomChdDecoderBuild decoder) : IRomChdDeliveryAdmission
    {
        public RomChdDeliveryAssessment? Assess(RomChdDeliveryEvidence evidence) => new(decoder);
        public RomManagedReadBinding? AssessManaged(RomContentArtifact artifact) =>
            artifact.Container is RomContainerFacts.Rvz && artifact.Facts.Verifications.Any(proof =>
                proof.Decoder is RomDecoderIdentity.Managed managed && managed.Build == "endpoint-fixture-managed/v1")
                ? artifact.BindManaged("endpoint-fixture-managed/v1") : null;
        public bool AdmitsCd(RomReconstruction.CdMember member) => false;
        public RomChdDecoderBuild? AssessCd(RomReconstruction.CdMember member) => null;
    }


    [Theory]
    [InlineData(RomDeliveryFormat.Original, "disc.iso", 4, 11)]
    [InlineData(RomDeliveryFormat.Rvz, "disc.rvz", 2, 12)]
    public async Task TitleTicket_UnregisteredPlatform_PreservesOriginalAndRvz(RomDeliveryFormat format, string filename,
        long size, int fileId)
    {
        var files = Substitute.For<IExportRepository>();
        files.GetExportFilesForTitleAsync(1, Arg.Any<AuthorizedExportScope>(), Arg.Any<CancellationToken>()).Returns([
            new ExportFileData(1, "Title", 99, "Unregistered", 7, 11, "disc.iso")]);
        var scope = Substitute.For<IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope>>();
        scope.HandleAsync(Arg.Any<ResolveExportScopeQuery>(), Arg.Any<CancellationToken>()).Returns(
            (ErrorOr<AuthorizedExportScope>)new AuthorizedExportScope(new ExportScope.AllCatalog(), null));
        var logical = Sha256.Parse(new string('a', 64));
        var artifact = Sha256.Parse(new string('b', 64));
        var catalog = Substitute.For<IRomContentCatalog>();
        SetCatalogContent(catalog, OriginalAndRvz(7, logical, 4, 11, 12, artifact, 2));
        var reference = Substitute.For<IReferenceCatalogService>();
        reference.GetSystemKeysAsync(Arg.Any<CancellationToken>()).Returns(new SystemKeys(new Dictionary<int, string>()));
        reference.ResolveSystemIdAsync("ps2", Arg.Any<CancellationToken>()).Returns((int?)41);
        var storage = Substitute.For<IFileStorageService>();
        storage.ExistsAsync(fileId, Arg.Any<CancellationToken>()).Returns(true);
        using var tickets = new AdminDownloadTickets(TimeProvider.System);

        var prepared = await Invoke("PrepareTitleDownload", 1, format, new DefaultHttpContext(), files,
            scope, catalog, storage, tickets, reference, new SyntheticAdmission(new("linux-x64", Sha1.Parse(new string('f', 40)), 1)), DeliveryPolicy, CancellationToken.None);

        var ticket = prepared.ShouldBeAssignableTo<IValueHttpResult<AdminDownloadTicket>>().ShouldNotBeNull().Value.ShouldNotBeNull();
        var entry = tickets.Resolve(ticket.DownloadUrl.Split('/').Last()).ShouldNotBeNull().Entries.ShouldHaveSingleItem();
        var target = entry.Target.ShouldNotBeNull();
        entry.PlatformKeys.ShouldBe([null]);
        target.Format.ShouldBe(format);
        target.FileId.ShouldBe(fileId);
        target.Filename.ShouldBe(filename);
        target.SizeBytes.ShouldBe(size);
        target.Sha256.ShouldBe(format == RomDeliveryFormat.Rvz ? artifact : logical);
        target.ContentSha256.ShouldBe(logical);
        target.ContentSizeBytes.ShouldBe(4);
        await reference.DidNotReceiveWithAnyArgs().GetSystemKeysAsync(default);
    }

    /// <summary>
    ///     A title on a platform no media kind claims resolves no kind for a verified DVD CHD, yet its bytes are admitted from
    ///     evidence alone; without the serving authority the CHD is refused before storage is asked.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TitleTicket_UnregisteredPlatform_BindsTheVerifiedChdOnlyThroughTheServingAuthority(bool authority)
    {
        var files = Substitute.For<IExportRepository>();
        files.GetExportFilesForTitleAsync(1, Arg.Any<AuthorizedExportScope>(), Arg.Any<CancellationToken>()).Returns([
            new ExportFileData(1, "Title", 99, "Unregistered", 7, 12, "disc.iso")]);
        var scope = Substitute.For<IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope>>();
        scope.HandleAsync(Arg.Any<ResolveExportScopeQuery>(), Arg.Any<CancellationToken>()).Returns(
            (ErrorOr<AuthorizedExportScope>)new AuthorizedExportScope(new ExportScope.AllCatalog(), null));
        var content = SyntheticChdContent();
        var catalog = Substitute.For<IRomContentCatalog>();
        SetCatalogContent(catalog, content);
        var reference = Substitute.For<IReferenceCatalogService>();
        reference.GetSystemKeysAsync(Arg.Any<CancellationToken>()).Returns(new SystemKeys(new Dictionary<int, string>()));
        reference.ResolveSystemIdAsync("ps2", Arg.Any<CancellationToken>()).Returns((int?)41);
        IRomChdDeliveryAdmission admission = authority
            ? new SyntheticAdmission(content.Artifacts![0].ChdEvidence![0].Decoder)
            : Substitute.For<IRomChdDeliveryAdmission>();
        var storage = Substitute.For<IFileStorageService>();
        storage.ExistsAsync(12, Arg.Any<CancellationToken>()).Returns(true);
        using var tickets = new AdminDownloadTickets(TimeProvider.System);

        var prepared = await Invoke("PrepareTitleDownload", 1, RomDeliveryFormat.Chd, new DefaultHttpContext(), files, scope, catalog,
            storage, tickets, reference, admission, DeliveryPolicy, CancellationToken.None);

        if (authority)
        {
            var ticket = prepared.ShouldBeAssignableTo<IValueHttpResult<AdminDownloadTicket>>().ShouldNotBeNull().Value.ShouldNotBeNull();
            var entry = tickets.Resolve(ticket.DownloadUrl.Split('/').Last()).ShouldNotBeNull().Entries.ShouldHaveSingleItem();
            entry.Path.ShouldBe("disc.chd");
            entry.Target.ShouldNotBeNull().ReadBinding.ShouldBe(content.Artifacts![0].ChdEvidence![0].Bind());
        }
        else
        {
            prepared.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(409);
            storage.ReceivedCalls().ShouldBeEmpty();
        }
        await reference.DidNotReceiveWithAnyArgs().GetSystemKeysAsync(default);
    }

    private static void SetCatalogContent(IRomContentCatalog catalog, params RomContentDescriptor[] descriptors)
    {
        catalog.FindByRomIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<int, RomContentDescriptor>)descriptors
                .Where(content => call.ArgAt<IReadOnlyCollection<int>>(0).Contains(content.RomId))
                .ToDictionary(content => content.RomId));
    }
}
