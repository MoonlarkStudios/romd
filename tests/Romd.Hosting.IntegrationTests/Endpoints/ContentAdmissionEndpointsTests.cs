using Romd.PostgreSql.TestSupport;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Systems;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Delivery;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Host.Endpoints;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;
using Romd.Infrastructure.Storage;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class ContentAdmissionEndpointsTests
{
    private static readonly RomDeliveryPolicy DeliveryPolicy = RomMediaPolicyComposition.CreateDeliveryPolicy();

    private static readonly Sha256 Hash = Sha256.Parse(new string('a', 64));
    private static readonly Sha256 Artifact = Sha256.Parse(new string('b', 64));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ContentGrant_SaturationReturns503OnlyAfterValidBoundGrant(bool authorized, bool bound)
    {
        var target = OriginalTarget(4);
        var issuer = Substitute.For<IConsumerContentGrantIssuer>();
        if (authorized && bound)
            issuer.ValidateDownloadGrant("token").Returns(new ConsumerValidatedContentGrant(
                new(Guid.NewGuid(), 1, 2, 3, target), "key", Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5)));
        else
            issuer.ValidateDownloadGrant("token").Returns(authorized ? ConsumerErrors.InvalidContentGrant() : ConsumerErrors.ContentNotFound());
        var resolver = Substitute.For<IConsumerContentArtifactResolver>();
        resolver.ResolveAsync(Arg.Any<RomDeliveryTarget>()).Returns(Task.FromException<Stream?>(new ResourceCapacityException("occupied")));
        var context = new DefaultHttpContext();
        var result = await Invoke(typeof(ConsumerStorageEndpoints), "RedeemConsumerContentGrant", "token", context, issuer, resolver, CancellationToken.None);
        AssertStatus(result, authorized && bound ? 503 : authorized ? 401 : 404);
        if (authorized && bound)
        {
            AssertRetryHeaders(context);
            await resolver.Received(1).ResolveAsync(target, CancellationToken.None);
            await resolver.DidNotReceiveWithAnyArgs().ResolveAsync(default(Sha256), default);
        }
        else
            resolver.ReceivedCalls().ShouldBeEmpty();
        context.Response.ContentLength.ShouldBeNull();
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "original")]
    [InlineData(true, "original")]
    public async Task AdminDownload_SaturationReturns503AfterLibraryAuthorization(bool authorized, string? format)
    {
        var rom = StoredRom(47);
        var repository = Substitute.For<IRomRepository>();
        repository.GetByIdAsync(47).Returns(rom);
        repository.IsAccessibleAsync(rom.Id, Arg.Any<int?>()).Returns(authorized);
        var catalog = Substitute.For<IRomContentCatalog>();
        catalog.FindByRomIdAsync(rom.Id).Returns(StoredContent(rom.Id));
        var reader = SaturatedReader(rom.Id);
        var context = new DefaultHttpContext();
        var result = await Invoke(typeof(RomEndpoints), "Download", new Sqid(47), context,
            repository, reader, Substitute.For<ICurrentUser>(), catalog, ReferenceCatalog(), new ManagedMediaTestAdmission("endpoint-fixture-managed/v1"), DeliveryPolicy, CancellationToken.None, format!);
        AssertStatus(result, authorized ? 503 : 403);
        if (authorized)
        {
            AssertRetryHeaders(context);
            await reader.Received(1).OpenAsync(OriginalTarget(rom.Id), CancellationToken.None);
            await reader.DidNotReceiveWithAnyArgs().OpenVerifiedByRomIdAsync(default, default);
        }
        else
        {
            catalog.ReceivedCalls().ShouldBeEmpty();
            reader.ReceivedCalls().ShouldBeEmpty();
        }
        context.Response.ContentLength.ShouldBeNull();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AdminTicket_SaturationReturns503AfterTicketValidation(bool authorized, bool bound)
    {
        var target = bound ? OriginalTarget(47) : null;
        var tickets = Substitute.For<IAdminDownloadTickets>();
        tickets.Resolve("token").Returns(authorized
            ? new AdminDownloadPlan("disc.iso", [new(5, "disc.iso", 47, Target: target)], false)
            : null);
        var reader = SaturatedReader(47);
        var storage = Substitute.For<IFileStorageService>();
        var context = new DefaultHttpContext();
        var result = await Invoke(typeof(ExportEndpoints), "RedeemDownload", "token", context,
            tickets, Substitute.For<IExportArchiveWriter>(), storage, reader, CancellationToken.None);
        AssertStatus(result, authorized ? 503 : 404);
        if (authorized)
        {
            AssertRetryHeaders(context);
            if (target is not null)
            {
                await reader.Received(1).OpenAsync(target, CancellationToken.None);
                await reader.DidNotReceiveWithAnyArgs().OpenVerifiedByRomIdAsync(default, default);
            }
            else
            {
                await reader.Received(1).OpenVerifiedByRomIdAsync(47, CancellationToken.None);
                await reader.DidNotReceiveWithAnyArgs().OpenAsync(default(RomDeliveryTarget)!, default);
            }
        }
        else
            reader.ReceivedCalls().ShouldBeEmpty();
        storage.ReceivedCalls().ShouldBeEmpty();
        context.Response.ContentLength.ShouldBeNull();
    }

    [Fact]
    public async Task AdminDownload_RvzTarget_DoesNotOpenSaturatedOriginalFormat()
    {
        var rom = StoredRom(47);
        var repository = Substitute.For<IRomRepository>();
        repository.GetByIdAsync(47).Returns(rom);
        repository.IsAccessibleAsync(rom.Id, Arg.Any<int?>()).Returns(true);
        var catalog = Substitute.For<IRomContentCatalog>();
        catalog.FindByRomIdAsync(rom.Id).Returns(StoredContent(rom.Id));
        var reader = SaturatedReader(rom.Id);
        using var raw = new MemoryStream(new byte[60]);
        var target = new RomDeliveryTarget(rom.Id, 5, RomDeliveryFormat.Rvz, "disc.rvz",
            Artifact, 60, Hash, 100, Artifact, 60, RomRepresentationEncoding.Rvz,
            ReadBinding: StoredContent(rom.Id).Preferred.BindManaged("endpoint-fixture-managed/v1"));
        reader.OpenAsync(target).Returns(raw);
        var context = new DefaultHttpContext();

        var result = await Invoke(typeof(RomEndpoints), "Download", new Sqid(47), context,
            repository, reader, Substitute.For<ICurrentUser>(), catalog, ReferenceCatalog(), new ManagedMediaTestAdmission("endpoint-fixture-managed/v1"), DeliveryPolicy, CancellationToken.None, "rvz");

        result.ShouldBeAssignableTo<IFileHttpResult>().FileDownloadName.ShouldBe("disc.rvz");
        context.Response.ContentLength.ShouldBe(60);
        await reader.Received(1).OpenAsync(target, CancellationToken.None);
        await reader.DidNotReceive().OpenAsync(OriginalTarget(rom.Id), CancellationToken.None);
        await reader.DidNotReceiveWithAnyArgs().OpenVerifiedByRomIdAsync(default, default);
    }

    private static IRomContentReader SaturatedReader(int id)
    {
        var reader = Substitute.For<IRomContentReader>();
        reader.OpenVerifiedByRomIdAsync(id).Returns(Task.FromException<Stream?>(new ResourceCapacityException("occupied")));
        reader.OpenAsync(OriginalTarget(id)).Returns(Task.FromException<Stream?>(new ResourceCapacityException("occupied")));
        return reader;
    }

    private static RomDeliveryTarget OriginalTarget(int romId) => new(romId, 5,
        RomDeliveryFormat.Original, "disc.iso", Hash, 100, Hash, 100, Artifact, 60, RomRepresentationEncoding.Rvz,
        Md5.Parse(new string('c', 32)), Sha1.Parse(new string('b', 40)),
        StoredContent(romId).Preferred.BindManaged("endpoint-fixture-managed/v1"));

    private static RomContentDescriptor StoredContent(int romId)
    {
        var identity = new RomLogicalIdentity(100, Hash, Sha1.Parse(new string('b', 40)), Md5.Parse(new string('c', 32)), Crc32.FromUInt32(1));
        var container = MediaFactFixtures.Rvz(100, 1, 5, 19, 131072, false);
        var measurement = MediaFactFixtures.WholeMeasurement(Guid.Parse("cff66314-2fbb-4e2b-a3cd-b0ea57c395bb"), Artifact, 60,
            identity, container, new RomDecoderIdentity.Managed("endpoint-fixture-managed/v1"), "logical-sha256/v1",
            "endpoint-fixture-verifier/v1", DateTimeOffset.UnixEpoch, null, null);
        var artifact = MediaDeliveryFactFixtures.MeasuredWhole(15, 5, 25, romId, true, container, measurement, null, null);
        return new(romId, artifact.OutputId, identity, "disc.iso", [artifact], [], []);
    }

    private static RomFile StoredRom(int romId) => new RomFileEntity
    {
        Id = romId,
        OriginalFilename = "disc.iso",
        ContentSha256 = Hash,
        ContentSize = 100,
        Sha1 = Sha1.Parse(new string('b', 40)),
        Md5 = Md5.Parse(new string('c', 32)),
        Crc32 = Crc32.FromUInt32(1)
    }.ToDomain(5, RomRepresentationEncoding.Rvz);

    private static void AssertStatus(IResult result, int expected) =>
        result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(expected);
    private static void AssertRetryHeaders(HttpContext context)
    {
        context.Response.Headers.RetryAfter.ToString().ShouldBe("15");
        context.Response.Headers.CacheControl.ToString().ShouldContain("no-store");
    }
    private static async Task<IResult> Invoke(Type endpoint, string name, params object[] arguments) =>
        await (Task<IResult>)endpoint.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments)!;
    private static IReferenceCatalogService ReferenceCatalog()
    {
        var reference = Substitute.For<IReferenceCatalogService>();
        reference.GetSystemKeysAsync(Arg.Any<CancellationToken>()).Returns(new SystemKeys(new Dictionary<int, string>()));
        return reference;
    }

}
