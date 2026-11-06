using Romd.Admin.Application.Export.Commands.QueueLibraryExport;
using Romd.PostgreSql.TestSupport;
using Romd.Application.Common.Systems;
using System.Reflection;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.TestHost;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Admin.Application.Source.Rom.Commands.DeleteRom;
using Romd.Admin.Application.Source.Rom.Commands.BatchDelete;
using Romd.Admin.Application.Source.Rom.Commands.PurgeUnidentified;
using Romd.Admin.Application.Export.Queries.ResolveExportScope;
using Romd.Admin.Application.Export.Queries.AuthorizeExportDownload;
using Romd.Admin.Application.Export.Queries.PrepareReleaseDownload;
using Romd.Domain.Jobs;
using Romd.Host.Authorization;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Domain.Hashing;
using Romd.Domain.Identity;
using Romd.Domain.Source.Rom;
using Romd.Contracts.Management.Export;
using Romd.Host.Endpoints;
using Romd.Infrastructure.Export;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;
using Romd.Infrastructure.Storage;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class PerFileDeliveryEndpointsTests
{
    private static readonly RomDeliveryPolicy DeliveryPolicy = RomMediaPolicyComposition.CreateDeliveryPolicy();

    private static readonly Sha256 Logical = Sha256.Parse(new string('a', 64));
    private static readonly Sha256 Artifact = Sha256.Parse(new string('b', 64));
    private readonly IReferenceCatalogService _reference = Substitute.For<IReferenceCatalogService>();
    private readonly IRomChdDeliveryAdmission _admission = Substitute.For<IRomChdDeliveryAdmission>();
    private readonly IRomRepository _roms = Substitute.For<IRomRepository>();
    private readonly IRomContentCatalog _catalog = Substitute.For<IRomContentCatalog>();
    private readonly IRomContentReader _reader = Substitute.For<IRomContentReader>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IFileStorageService _storage = Substitute.For<IFileStorageService>();
    private readonly IExportAuthorizationReader _authorization = Substitute.For<IExportAuthorizationReader>();

    public PerFileDeliveryEndpointsTests()
    {
        _reference.GetSystemKeysAsync(Arg.Any<CancellationToken>()).Returns(new SystemKeys(new Dictionary<int, string> { [1] = "ps2", [2] = "gc" }));
        _reference.ResolveSystemIdAsync("ps2", Arg.Any<CancellationToken>()).Returns((int?)1);
        _admission.AssessManaged(Arg.Any<RomContentArtifact>()).Returns(call =>
        {
            var artifact = call.Arg<RomContentArtifact>();
            return artifact.Container is RomContainerFacts.Rvz && artifact.Facts.Verifications.Any(proof =>
                proof.Decoder is RomDecoderIdentity.Managed managed && managed.Build == "endpoint-fixture-managed/v1")
                ? artifact.BindManaged("endpoint-fixture-managed/v1") : null;
        });
        _user.UserId.Returns(Guid.NewGuid());
        _user.HasRole(RomdRoleType.Contributor).Returns(true);
        _roms.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(new RomFileEntity
        {
            Id = 7,
            OriginalFilename = "disc.iso",
            ContentSha256 = Logical,
            ContentSize = 4,
            Sha1 = Sha1.Parse(new string('a', 40)),
            Md5 = Md5.Parse(new string('a', 32)),
            Crc32 = Crc32.Parse("aaaaaaaa")
        }.ToDomain(11, RomRepresentationEncoding.Identity));
        _roms.IsAccessibleAsync(7, null, Arg.Any<CancellationToken>()).Returns(true);
        _catalog.FindByRomIdAsync(7, Arg.Any<CancellationToken>()).Returns(OriginalAndRvz(7, Logical, 4, 11, 12, Artifact, 2));
        _storage.ExistsAsync(12, Arg.Any<CancellationToken>()).Returns(true);
        _storage.ExistsAsync(11, Arg.Any<CancellationToken>()).Returns(true);
    }

    [Theory]
    [InlineData("RVZ")]
    [InlineData("iso")]
    [InlineData(" rvz")]
    [InlineData("")]
    public async Task PerFileEndpoints_InvalidFormat_ReturnBadRequestBeforeStorage(string format)
    {
        using var tickets = new AdminDownloadTickets(TimeProvider.System);
        var direct = await DirectAsync(new DefaultHttpContext(), format);
        var ticket = await PrepareAsync(new DefaultHttpContext(), tickets, format);
        direct.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(400);
        ticket.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(400);
        _roms.ReceivedCalls().ShouldBeEmpty();
        _catalog.ReceivedCalls().ShouldBeEmpty();
        _storage.ReceivedCalls().ShouldBeEmpty();
        _reader.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task PrepareFileDownload_Rvz_BindsExactArtifactAndRedemptionDoesNotReplan()
    {
        using var tickets = new AdminDownloadTickets(TimeProvider.System);
        var prepared = await PrepareAsync(new DefaultHttpContext(), tickets, "rvz");
        var ticket = prepared.ShouldBeAssignableTo<IValueHttpResult<AdminDownloadTicket>>().Value.ShouldNotBeNull();
        string token = ticket.DownloadUrl.Split('/').Last();
        var target = tickets.Resolve(token)!.Entries.ShouldHaveSingleItem().Target.ShouldNotBeNull();
        target.Format.ShouldBe(RomDeliveryFormat.Rvz);
        target.Filename.ShouldBe("disc.rvz");
        target.Sha256.ShouldBe(Artifact);
        target.SizeBytes.ShouldBe(2);
        target.ContentSha256.ShouldBe(Logical);
        target.ContentSizeBytes.ShouldBe(4);
        target.FileId.ShouldBe(12);
        _catalog.ClearReceivedCalls();
        _roms.ClearReceivedCalls();
        _reader.OpenAsync(target, Arg.Any<CancellationToken>()).Returns(new MemoryStream([7, 8]));
        var context = new DefaultHttpContext();
        var result = await RedeemAsync(context, tickets, token);
        await AssertFileAsync(result, context, [7, 8], "disc.rvz");
        context.Response.ContentLength.ShouldBe(2);
        context.Response.Headers.CacheControl.ToString().ShouldBe("private, no-store");
        context.Response.Headers["Referrer-Policy"].ToString().ShouldBe("no-referrer");
        context.Response.Headers["X-Content-Type-Options"].ToString().ShouldBe("nosniff");
        _roms.ReceivedCalls().ShouldBeEmpty();
        _catalog.ReceivedCalls().ShouldBeEmpty();
        await _storage.DidNotReceiveWithAnyArgs().RetrieveByIdAsync(default, default);
        _reader.OpenAsync(target, Arg.Any<CancellationToken>()).Returns((Stream?)null);
        (await RedeemAsync(new DefaultHttpContext(), tickets, token))
            .ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(404);
    }

    [Theory]
    [InlineData(null, RomDeliveryFormat.Original, "disc.iso", 4)]
    [InlineData("rvz", RomDeliveryFormat.Rvz, "disc.rvz", 2)]
    public async Task Download_ExplicitOrDefaultFormat_UsesBoundDeliveredFacts(string? format,
        RomDeliveryFormat expected, string filename, long size)
    {
        RomDeliveryTarget? captured = null;
        _reader.OpenAsync(Arg.Any<RomDeliveryTarget>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            captured = call.Arg<RomDeliveryTarget>();
            return new MemoryStream(expected == RomDeliveryFormat.Rvz ? [7, 8] : [1, 2, 3, 4]);
        });
        var context = new DefaultHttpContext();
        var result = await DirectAsync(context, format);
        captured.ShouldNotBeNull().Format.ShouldBe(expected);
        captured.SizeBytes.ShouldBe(size);
        captured.Filename.ShouldBe(filename);
        captured.Sha256.ShouldBe(expected == RomDeliveryFormat.Rvz ? Artifact : Logical);
        await AssertFileAsync(result, context, expected == RomDeliveryFormat.Rvz ? [7, 8] : [1, 2, 3, 4], filename);
        context.Response.ContentLength.ShouldBe(size);
        context.Response.Headers.CacheControl.ToString().ShouldBe("private, no-store");
    }

    [Fact]
    public async Task PerFileEndpoints_AccessDenied_DoesNotPlanOrOpen()
    {
        _roms.IsAccessibleAsync(7, null, Arg.Any<CancellationToken>()).Returns(false);
        using var tickets = new AdminDownloadTickets(TimeProvider.System);
        (await DirectAsync(new DefaultHttpContext(), "rvz")).ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(403);
        (await PrepareAsync(new DefaultHttpContext(), tickets, "rvz")).ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(403);
        _catalog.ReceivedCalls().ShouldBeEmpty();
        _reader.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task PerFileEndpoints_NoRetainedRvz_ReturnsUnavailableWithoutPreparing()
    {
        _catalog.FindByRomIdAsync(7, Arg.Any<CancellationToken>()).Returns(new RomContentDescriptor(7, 181, OriginalAndRvz(7, Logical, 4, 11, 12, Artifact, 2).Identity, "disc.iso",
                [OriginalAndRvz(7, Logical, 4, 11, 12, Artifact, 2).Preferred], [], []));
        using var tickets = new AdminDownloadTickets(TimeProvider.System);
        (await DirectAsync(new DefaultHttpContext(), "rvz")).ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(409);
        (await PrepareAsync(new DefaultHttpContext(), tickets, "rvz")).ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(409);
        _reader.ReceivedCalls().ShouldBeEmpty();
        _storage.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task LiveHttp_PerFileQueries_RejectInvalidValuesBeforeStorageAndRequireAuthentication()
    {
        using var tickets = new AdminDownloadTickets(TimeProvider.System);
        await using var app = BuildHttpApp(tickets);
        await app.StartAsync();
        using var client = app.GetTestClient();
        string id = new Sqid(7).ToString();
        using var denied = await client.GetAsync($"/api/roms/{id}/download?format=rvz");
        denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var deniedTicket = await PostTicketAsync(client, id, "rvz");
        deniedTicket.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        client.WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.Contributor]);
        foreach (string value in new[] { "RVZ", "Original", " rvz", "rvz ", "1", "null", "iso", "" })
        {
            string query = "?format=" + Uri.EscapeDataString(value);
            using var direct = await client.GetAsync($"/api/roms/{id}/download" + query);
            using var prepared = await PostTicketAsync(client, id, value);
            direct.StatusCode.ShouldBe(HttpStatusCode.BadRequest, value);
            using var problem = await ProblemEnvelope.ReadAsync(prepared, 400, "Export.InvalidDownloadTicket");
            problem.RootElement.GetProperty("errors").EnumerateObject().Select(field => field.Name).ShouldBe(["format"], value);
        }
        _roms.ReceivedCalls().ShouldBeEmpty();
        _catalog.ReceivedCalls().ShouldBeEmpty();
        _storage.ReceivedCalls().ShouldBeEmpty();
        _reader.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task LiveHttp_FileTicket_UnknownOrInaccessibleRom_AnswersTheEnvelope()
    {
        using var tickets = new AdminDownloadTickets(TimeProvider.System);
        await using var app = BuildHttpApp(tickets);
        await app.StartAsync();
        using var client = app.GetTestClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.Contributor]);

        using (var unknown = await PostTicketAsync(client, new Sqid(8).ToString()))
        using (await ProblemEnvelope.ReadAsync(unknown, 404, "Export.FileNotFound"))
        { }
        _roms.IsAccessibleAsync(7, null, Arg.Any<CancellationToken>()).Returns(false);
        using (var denied = await PostTicketAsync(client, new Sqid(7).ToString()))
        using (await ProblemEnvelope.ReadAsync(denied, 403, "Export.AccessDenied"))
        { }
    }

    [Fact]
    public async Task LiveHttp_BoundRvzTicketArtifactDisappears_ReturnsClean404WithoutSuccessLength()
    {
        using var tickets = new AdminDownloadTickets(TimeProvider.System);
        await using var app = BuildHttpApp(tickets);
        await app.StartAsync();
        using var client = app.GetTestClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.Contributor]);
        using var prepared = await PostTicketAsync(client, new Sqid(7).ToString(), "rvz");
        prepared.StatusCode.ShouldBe(HttpStatusCode.Created);
        var ticket = (await prepared.Content.ReadFromJsonAsync<AdminDownloadTicket>()).ShouldNotBeNull();
        prepared.Headers.Location!.OriginalString.ShouldBe(ticket.DownloadUrl);
        _reader.OpenAsync(Arg.Any<RomDeliveryTarget>(), Arg.Any<CancellationToken>()).Returns((Stream?)null);
        using var anonymous = app.GetTestClient();

        using var response = await anonymous.GetAsync(ticket.DownloadUrl);

        using var problem = await ProblemEnvelope.ReadAsync(response, 404, "Delivery.ContentNotFound");
        problem.RootElement.GetProperty("detail").GetString().ShouldBe("The download is unavailable.");
        string body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain(ticket.DownloadUrl);
        body.ShouldNotContain(Artifact.ToString());
        body.ShouldNotContain("disc.rvz");
        response.Content.Headers.ContentDisposition.ShouldBeNull();
        response.Content.Headers.ContentLength.ShouldBe((long)System.Text.Encoding.UTF8.GetByteCount(body));
        response.Content.Headers.ContentLength.ShouldNotBe(2L); // The bound RVZ's successful payload length.
        response.Headers.CacheControl.ShouldNotBeNull().Private.ShouldBeTrue();
        response.Headers.CacheControl.NoStore.ShouldBeTrue();
        response.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
    }

    [Fact]
    public async Task LiveHttp_DefaultOriginalAndExplicitRvz_BindQueryToMeasuredMetadataAndTicketRedemption()
    {
        _reader.OpenAsync(Arg.Any<RomDeliveryTarget>(), Arg.Any<CancellationToken>()).Returns(call =>
            new MemoryStream(call.Arg<RomDeliveryTarget>().Format == RomDeliveryFormat.Rvz ? [7, 8] : [1, 2, 3, 4]));
        using var tickets = new AdminDownloadTickets(TimeProvider.System);
        await using var app = BuildHttpApp(tickets);
        await app.StartAsync();
        using var client = app.GetTestClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.Contributor]);
        using var anonymous = app.GetTestClient();
        string id = new Sqid(7).ToString();
        foreach (var (wire, format, filename, bytes) in new[]
        {
            ((string?)null, RomDeliveryFormat.Original, "disc.iso", new byte[] { 1, 2, 3, 4 }),
            ("rvz", RomDeliveryFormat.Rvz, "disc.rvz", new byte[] { 7, 8 })
        })
        {
            using var direct = await client.GetAsync($"/api/roms/{id}/download" + (wire is null ? "" : $"?format={wire}"));
            direct.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await direct.Content.ReadAsByteArrayAsync()).ShouldBe(bytes);
            direct.Content.Headers.ContentLength.ShouldBe(bytes.Length);
            direct.Content.Headers.ContentDisposition!.FileName.ShouldBe(filename);
            using var prepared = await PostTicketAsync(client, id, wire);
            prepared.StatusCode.ShouldBe(HttpStatusCode.Created);
            prepared.Headers.CacheControl!.NoStore.ShouldBeTrue();
            var ticket = (await prepared.Content.ReadFromJsonAsync<AdminDownloadTicket>()).ShouldNotBeNull();
            var target = tickets.Resolve(ticket.DownloadUrl.Split('/').Last())!.Entries.ShouldHaveSingleItem().Target.ShouldNotBeNull();
            target.Format.ShouldBe(format);
            target.SizeBytes.ShouldBe(bytes.Length);
            target.Sha256.ShouldBe(format == RomDeliveryFormat.Rvz ? Artifact : Logical);
            using var redeemed = await anonymous.GetAsync(ticket.DownloadUrl);
            redeemed.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await redeemed.Content.ReadAsByteArrayAsync()).ShouldBe(bytes);
            redeemed.Content.Headers.ContentLength.ShouldBe(bytes.Length);
            redeemed.Headers.CacheControl!.Private.ShouldBeTrue();
            redeemed.Headers.CacheControl.NoStore.ShouldBeTrue();
            redeemed.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
            redeemed.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        }
    }

    private WebApplication BuildHttpApp(IAdminDownloadTickets tickets)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        // Body enums bind as the hosts bind them.
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new Romd.Hosting.Serialization.StrictJsonStringEnumConverter()));
        builder.Services.AddTestAuthentication();
        builder.Services.AddSingleton<IAuthorizationHandler, MinimumRoleHandler>();
        builder.Services.AddSingleton(_roms);
        builder.Services.AddSingleton(_catalog);
        builder.Services.AddSingleton(_reader);
        builder.Services.AddSingleton(_user);
        builder.Services.AddSingleton(_storage);
        builder.Services.AddSingleton(_authorization);
        builder.Services.AddSingleton(tickets);
        builder.Services.AddSingleton(_reference);
        builder.Services.AddSingleton(_admission);
        builder.Services.AddSingleton(DeliveryPolicy);
        builder.Services.AddSingleton(Substitute.For<ICommandHandler<DeleteRomCommand, ErrorOr.Deleted>>());
        builder.Services.AddSingleton(Substitute.For<ICommandHandler<BatchDeleteRomsCommand, BatchDeleteResult>>());
        builder.Services.AddSingleton(Substitute.For<ICommandHandler<PurgeUnidentifiedRomsCommand, PurgeUnidentifiedRomsResult>>());
        builder.Services.AddSingleton(Substitute.For<IExportRepository>());
        builder.Services.AddSingleton(Substitute.For<IExportArchiveWriter>());
        builder.Services.AddSingleton(Substitute.For<ICommandHandler<QueueLibraryExportCommand, Guid>>());
        builder.Services.AddSingleton(Substitute.For<IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope>>());
        builder.Services.AddSingleton(Substitute.For<IQueryHandler<AuthorizeExportDownloadQuery, ExportJob>>());
        builder.Services.AddSingleton(Substitute.For<IQueryHandler<PrepareReleaseDownloadQuery, AdminDownloadTicket>>());
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        var api = app.MapGroup("/api");
        api.MapRomEndpoints();
        api.MapExportEndpoints();
        return app;
    }

    private Task<IResult> DirectAsync(HttpContext context, string? format) => InvokeAsync(typeof(RomEndpoints), "Download",
        [new Sqid(7), context, _roms, _reader, _user, _catalog, _reference, _admission, DeliveryPolicy, CancellationToken.None, format]);
    /// <summary>Creates a file ticket for ROM 7 through the ticket collection, which validates the format first.</summary>
    private Task<IResult> PrepareAsync(HttpContext context, IAdminDownloadTickets tickets, string? format) => InvokeAsync(
        typeof(ExportEndpoints), "CreateDownloadTicket", [new CreateDownloadTicketRequest(DownloadTicketTarget.Rom, new Sqid(7).ToString(), format),
            context, Substitute.For<IExportRepository>(), Substitute.For<IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope>>(),
            _catalog, _storage, tickets, Substitute.For<IQueryHandler<PrepareReleaseDownloadQuery, AdminDownloadTicket>>(), _roms,
            _user, _authorization, _reference, _admission, DeliveryPolicy, Substitute.For<IQueryHandler<AuthorizeExportDownloadQuery, ExportJob>>(), CancellationToken.None]);

    /// <summary>Posts a file ticket for <paramref name="id" />, sending <paramref name="format" /> only when given.</summary>
    private static Task<HttpResponseMessage> PostTicketAsync(HttpClient client, string id, string? format = null) =>
        client.PostAsJsonAsync("/api/download-tickets", format is null
            ? new Dictionary<string, string> { ["target"] = "Rom", ["id"] = id }
            : new Dictionary<string, string> { ["target"] = "Rom", ["id"] = id, ["format"] = format });
    private Task<IResult> RedeemAsync(HttpContext context, IAdminDownloadTickets tickets, string token) => InvokeAsync(
        typeof(ExportEndpoints), "RedeemDownload", [token, context, tickets, Substitute.For<IExportArchiveWriter>(),
            _storage, _reader, CancellationToken.None]);
    private static async Task<IResult> InvokeAsync(Type type, string method, object?[] arguments) =>
        await type.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static).ShouldNotBeNull()
            .Invoke(null, arguments).ShouldBeOfType<Task<IResult>>();
    private static async Task AssertFileAsync(IResult result, DefaultHttpContext context, byte[] bytes, string filename)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.RequestServices = services;
        context.Request.Method = "GET";
        context.Request.Headers.Range = "bytes=0-0";
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.StatusCode.ShouldBe(200);
        ((MemoryStream)context.Response.Body).ToArray().ShouldBe(bytes);
        context.Response.Headers.ContentDisposition.ToString().ShouldContain(filename);
        context.Response.Headers.ContainsKey("Accept-Ranges").ShouldBeFalse();
        context.Response.Headers.ContainsKey("Content-Range").ShouldBeFalse();
    }
    [Fact]
    public async Task PerFileEndpoints_NoChdRepresentation_ReturnUnavailableWithoutOpening()
    {
        using var tickets = new AdminDownloadTickets(TimeProvider.System);
        (await DirectAsync(new DefaultHttpContext(), "chd")).ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(409);
        (await PrepareAsync(new DefaultHttpContext(), tickets, "chd")).ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(409);
        _reader.ReceivedCalls().ShouldBeEmpty();
        _storage.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("original", RomDeliveryFormat.Original, "disc.iso", 8192)]
    [InlineData("chd", RomDeliveryFormat.Chd, "disc.chd", 4096)]
    public async Task PerFileEndpoints_AdmittedPs2Chd_BindExactSelectedBytesAndProof(string wire, RomDeliveryFormat format, string filename, int length)
    {
        var content = ConfigureChd();
        _reader.OpenAsync(Arg.Any<RomDeliveryTarget>(), Arg.Any<CancellationToken>()).Returns(new MemoryStream(new byte[length]));
        using var tickets = new AdminDownloadTickets(TimeProvider.System);
        var context = new DefaultHttpContext();
        var result = await DirectAsync(context, wire);
        result.ShouldBeAssignableTo<IFileHttpResult>().FileDownloadName.ShouldBe(filename);
        await _reader.Received(1).OpenAsync(Arg.Is<RomDeliveryTarget>(target => target.Format == format && target.FileId == 12
            && target.SizeBytes == length && target.Sha256 == (format == RomDeliveryFormat.Chd ? Artifact : Logical)
            && ((RomChdDeliveryBinding)target.ReadBinding!).MeasurementId == content.Artifacts![0].ChdEvidence![0].Measurement.Id), CancellationToken.None);
        var prepared = await PrepareAsync(new DefaultHttpContext(), tickets, wire);
        var ticket = prepared.ShouldBeAssignableTo<IValueHttpResult<AdminDownloadTicket>>().Value.ShouldNotBeNull();
        var target = tickets.Resolve(ticket.DownloadUrl.Split('/').Last()).ShouldNotBeNull().Entries.ShouldHaveSingleItem().Target.ShouldNotBeNull();
        target.Format.ShouldBe(format);
        target.Filename.ShouldBe(filename);
        target.SizeBytes.ShouldBe(length);
        target.Sha256.ShouldBe(format == RomDeliveryFormat.Chd ? Artifact : Logical);
        target.Md5.ShouldBe(format == RomDeliveryFormat.Chd ? content.Artifacts![0].ArtifactMd5 : content.ContentMd5);
        target.Sha1.ShouldBe(format == RomDeliveryFormat.Chd ? content.Artifacts![0].ArtifactSha1 : content.ContentSha1);
        target.ReadBinding.ShouldBe(content.Artifacts![0].ChdEvidence![0].Bind());
    }

    [Theory]
    [InlineData("unqualified")]
    [InlineData("missing-evidence")]
    public async Task PerFileEndpoints_ChdNotAdmitted_ReturnUnavailableWithoutStorage(string refusal)
    {
        var content = ConfigureChd();
        if (refusal == "unqualified")
            _admission.Assess(Arg.Any<RomChdDeliveryEvidence>()).Returns((RomChdDeliveryAssessment?)null);
        if (refusal == "missing-evidence")
            _catalog.FindByRomIdAsync(7, Arg.Any<CancellationToken>()).Returns(new RomContentDescriptor(content.RomId, content.OutputId, content.Identity, content.OriginalFilename,
                [MediaDeliveryFactFixtures.UnmeasuredWhole(content.RepresentationId, content.FileId, content.OutputId, content.RomId,
                    true, content.Identity, content.ArtifactSha256, content.ArtifactSize, content.Preferred.Container,
                    content.Preferred.ArtifactMd5, content.Preferred.ArtifactSha1)], [], []));
        _storage.ClearReceivedCalls();
        using var tickets = new AdminDownloadTickets(TimeProvider.System);
        (await DirectAsync(new DefaultHttpContext(), "chd")).ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(409);
        (await PrepareAsync(new DefaultHttpContext(), tickets, "chd")).ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(409);
        _reader.ReceivedCalls().ShouldBeEmpty();
        _storage.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task GetById_AdmittedPs2Chd_AdvertisesArtifactAndLogicalMetadata()
    {
        ConfigureChd();
        var result = await InvokeAsync(typeof(RomEndpoints), "GetById",
            [_reference, new Sqid(7), _roms, _catalog, _admission, DeliveryPolicy, CancellationToken.None]);
        var rom = result.ShouldBeAssignableTo<IValueHttpResult<Romd.Contracts.Management.Models.Rom>>().Value.ShouldNotBeNull();
        var chd = rom.Formats.ShouldNotBeNull().Single(option => option.Format == "chd");
        chd.State.ShouldBe("available");
        chd.Label.ShouldBe("CHD");
        chd.FileExtension.ShouldBe(".chd");
        chd.Filename.ShouldBe("disc.chd");
        chd.SizeBytes.ShouldBe((Romd.Contracts.Common.Models.ByteCount)4096);
        chd.Sha256.ShouldBe(Artifact.ToString());
        chd.RuntimeApplicability.ShouldBe(["pcsx2"]);
        var original = rom.Formats.Single(option => option.Format == "original");
        original.SizeBytes.ShouldBe((Romd.Contracts.Common.Models.ByteCount)8192);
        original.Label.ShouldBe("Original");
        original.FileExtension.ShouldBeNull();
    }

    /// <summary>
    ///     A verified CHD matched on two platforms has no unique kind, so every per-ROM surface offers its bytes and the
    ///     decoded original with no runtime claim.
    /// </summary>
    [Fact]
    public async Task PerFileEndpoints_ChdMatchedOnTwoPlatforms_DeliverTheVerifiedBytesWithoutARuntimeClaim()
    {
        var content = ConfigureChd();
        _reference.ResolveSystemIdAsync("gc", Arg.Any<CancellationToken>()).Returns((int?)2);
        _roms.GetMatchesAsync(7, Arg.Any<CancellationToken>()).Returns([
            new(1, "DAT", 1, "Game", "disc.iso", 1, "Game", 1, "PS2"),
            new(2, "DAT", 2, "Game", "disc.iso", 2, "Game", 2, "GC")]);
        _reader.OpenAsync(Arg.Any<RomDeliveryTarget>(), Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(new byte[4096]));
        using var tickets = new AdminDownloadTickets(TimeProvider.System);

        var result = await InvokeAsync(typeof(RomEndpoints), "GetById",
            [_reference, new Sqid(7), _roms, _catalog, _admission, DeliveryPolicy, CancellationToken.None]);
        var direct = await DirectAsync(new DefaultHttpContext(), "chd");
        var prepared = await PrepareAsync(new DefaultHttpContext(), tickets, "chd");

        var formats = result.ShouldBeAssignableTo<IValueHttpResult<Romd.Contracts.Management.Models.Rom>>().Value.ShouldNotBeNull()
            .Formats.ShouldNotBeNull();
        formats.Select(option => (option.Format, option.State, option.RuntimeApplicability.Count))
            .ShouldBe([("original", "available", 0), ("chd", "available", 0)]);
        direct.ShouldBeAssignableTo<IFileHttpResult>().ShouldNotBeNull().FileDownloadName.ShouldBe("disc.chd");
        var ticket = prepared.ShouldBeAssignableTo<IValueHttpResult<AdminDownloadTicket>>().ShouldNotBeNull().Value.ShouldNotBeNull();
        var entry = tickets.Resolve(ticket.DownloadUrl.Split('/').Last()).ShouldNotBeNull().Entries.ShouldHaveSingleItem();
        entry.Target.ShouldNotBeNull().ReadBinding.ShouldBe(content.Artifacts![0].ChdEvidence![0].Bind());
        entry.PlatformKeys.ShouldBe(["ps2", "gc"]);
    }

    private RomContentDescriptor ConfigureChd()
    {
        var content = SyntheticChdContent();
        _catalog.FindByRomIdAsync(7, Arg.Any<CancellationToken>()).Returns(content);
        _roms.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(new RomFileEntity
        {
            Id = 7,
            OriginalFilename = "disc.iso",
            ContentSha256 = Logical,
            ContentSize = 8192,
            Sha1 = content.ContentSha1,
            Md5 = content.ContentMd5,
            Crc32 = content.ContentCrc32
        }.ToDomain(12, RomRepresentationEncoding.Chd));
        _roms.GetStatusAsync(7, Arg.Any<CancellationToken>()).Returns(RomCatalogStatus.Cataloged);
        _roms.GetMatchesAsync(7, Arg.Any<CancellationToken>()).Returns([new(1, "DAT", 1, "Game", "disc.iso", 1, "Game", 1, "PS2")]);
        _admission.Assess(Arg.Any<RomChdDeliveryEvidence>())
            .Returns(new RomChdDeliveryAssessment(content.Artifacts![0].ChdEvidence![0].Decoder));
        return content;
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
    [InlineData("original", RomDeliveryFormat.Original, "disc.iso", 4, 11)]
    [InlineData("rvz", RomDeliveryFormat.Rvz, "disc.rvz", 2, 12)]
    public async Task PerFileEndpoints_UnregisteredPlatform_PreservesOriginalAndRvz(string wire,
        RomDeliveryFormat format, string filename, long size, int fileId)
    {
        _reference.GetSystemKeysAsync(Arg.Any<CancellationToken>()).Returns(new SystemKeys(new Dictionary<int, string>()));
        _reference.ResolveSystemIdAsync("ps2", Arg.Any<CancellationToken>()).Returns((int?)41);
        _roms.GetMatchesAsync(7, Arg.Any<CancellationToken>()).Returns([
            new(1, "DAT", 1, "Game", "disc.iso", 1, "Game", 99, "Unregistered")]);
        _reader.OpenAsync(Arg.Any<RomDeliveryTarget>(), Arg.Any<CancellationToken>()).Returns(new MemoryStream(new byte[size]));
        using var tickets = new AdminDownloadTickets(TimeProvider.System);
        IResult? direct = null;
        IResult? prepared = null;
        var error = await Record.ExceptionAsync(async () =>
        {
            direct = await DirectAsync(new DefaultHttpContext(), wire);
            prepared = await PrepareAsync(new DefaultHttpContext(), tickets, wire);
        });

        error.ShouldBeNull();
        direct.ShouldBeAssignableTo<IFileHttpResult>().ShouldNotBeNull().FileDownloadName.ShouldBe(filename);
        var ticket = prepared.ShouldBeAssignableTo<IValueHttpResult<AdminDownloadTicket>>().ShouldNotBeNull().Value.ShouldNotBeNull();
        var entry = tickets.Resolve(ticket.DownloadUrl.Split('/').Last()).ShouldNotBeNull().Entries.ShouldHaveSingleItem();
        var target = entry.Target.ShouldNotBeNull();
        target.Format.ShouldBe(format);
        target.FileId.ShouldBe(fileId);
        target.Filename.ShouldBe(filename);
        target.SizeBytes.ShouldBe(size);
        target.Sha256.ShouldBe(format == RomDeliveryFormat.Rvz ? Artifact : Logical);
        target.ContentSha256.ShouldBe(Logical);
        target.ContentSizeBytes.ShouldBe(4);
        entry.PlatformKeys.ShouldBe([null]);
        await _reader.Received(1).OpenAsync(target, CancellationToken.None);
        await _storage.Received(1).ExistsAsync(fileId, CancellationToken.None);
        await _reference.DidNotReceiveWithAnyArgs().GetSystemKeysAsync(default);
    }

    /// <summary>
    ///     A ROM matched only on a platform no media kind claims resolves no kind for its verified DVD CHD, so its bytes are
    ///     admitted from evidence alone; without the serving authority they are refused before opening.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PerFileEndpoints_UnregisteredPlatform_DeliverTheChdOnlyThroughTheServingAuthority(bool authority)
    {
        ConfigureChd();
        if (!authority)
            _admission.Assess(Arg.Any<RomChdDeliveryEvidence>()).Returns((RomChdDeliveryAssessment?)null);
        _reference.GetSystemKeysAsync(Arg.Any<CancellationToken>()).Returns(new SystemKeys(new Dictionary<int, string>()));
        _reference.ResolveSystemIdAsync("ps2", Arg.Any<CancellationToken>()).Returns((int?)41);
        _roms.GetMatchesAsync(7, Arg.Any<CancellationToken>()).Returns([
            new(1, "DAT", 1, "Game", "disc.iso", 1, "Game", 99, "Unregistered")]);
        _reader.OpenAsync(Arg.Any<RomDeliveryTarget>(), Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(new byte[4096]));
        _storage.ClearReceivedCalls();
        using var tickets = new AdminDownloadTickets(TimeProvider.System);

        var direct = await DirectAsync(new DefaultHttpContext(), "chd");
        var prepared = await PrepareAsync(new DefaultHttpContext(), tickets, "chd");

        if (authority)
        {
            direct.ShouldBeAssignableTo<IFileHttpResult>().ShouldNotBeNull().FileDownloadName.ShouldBe("disc.chd");
            var ticket = prepared.ShouldBeAssignableTo<IValueHttpResult<AdminDownloadTicket>>().ShouldNotBeNull().Value.ShouldNotBeNull();
            tickets.Resolve(ticket.DownloadUrl.Split('/').Last()).ShouldNotBeNull().Entries.ShouldHaveSingleItem()
                .Target.ShouldNotBeNull().Format.ShouldBe(RomDeliveryFormat.Chd);
        }
        else
        {
            direct.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(409);
            prepared.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(409);
            _reader.ReceivedCalls().ShouldBeEmpty();
            _storage.ReceivedCalls().ShouldBeEmpty();
        }
        await _reference.DidNotReceiveWithAnyArgs().GetSystemKeysAsync(default);
    }
}
