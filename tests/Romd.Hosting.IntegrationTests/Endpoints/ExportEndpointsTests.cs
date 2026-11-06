using Romd.Admin.Application.Export.Commands.QueueLibraryExport;
using Romd.Admin.Application.Common.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.PostgreSql.TestSupport;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Contracts.Management.Export;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Systems;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Content;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Export.Queries.ResolveExportScope;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Identity;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Host.Endpoints;
using Shouldly;
using Xunit;
using Romd.Infrastructure.Storage;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class ExportEndpointsTests
{
    private static readonly RomDeliveryPolicy DeliveryPolicy = RomMediaPolicyComposition.CreateDeliveryPolicy();

    [Theory]
    [InlineData("")]
    [InlineData("not-a-sqid")]
    public async Task ExportLibrary_InvalidBodyIdentity_ReturnsFieldProblemBeforeScheduling(string libraryId)
    {
        var handler = Substitute.For<ICommandHandler<QueueLibraryExportCommand, Guid>>();
        var result = await InvokeExportLibraryAsync(new ExportLibraryRequest { LibraryId = libraryId }, handler);
        result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(400);
        var problem = result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>().Value.ShouldNotBeNull();
        problem.Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe("Export.InvalidLibraryId");
        problem.Extensions["errors"].ShouldBeAssignableTo<IReadOnlyDictionary<string, string[]>>().ShouldNotBeNull().Keys.ShouldBe(["libraryId"]);
        handler.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ExportLibrary_AcceptedJob_UsesCanonicalReferenceAndLocation()
    {
        var id = Guid.NewGuid();
        var handler = Substitute.For<ICommandHandler<QueueLibraryExportCommand, Guid>>();
        handler.HandleAsync(Arg.Any<QueueLibraryExportCommand>(), Arg.Any<CancellationToken>()).Returns(id);
        var result = await InvokeExportLibraryAsync(null, handler);
        var accepted = result.ShouldBeOfType<Microsoft.AspNetCore.Http.HttpResults.Accepted<Romd.Contracts.Management.Models.JobReference>>();
        accepted.Value.ShouldNotBeNull().JobId.ShouldBe(id);
        accepted.Location.ShouldBe($"/api/jobs/{id}");
        await handler.Received(1).HandleAsync(new QueueLibraryExportCommand(null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExportLibrary_NonAdminWithRequestedLibraryId_ReturnsForbiddenBeforeAcceptance()
    {
        var scope = Substitute.For<IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope>>();
        scope.HandleAsync(new ResolveExportScopeQuery(123), Arg.Any<CancellationToken>()).Returns(ExportErrors.AdminRoleRequired());
        var acceptance = Substitute.For<IExportJobAcceptance>();
        var unit = Substitute.For<IUnitOfWork>();
        var handler = new QueueLibraryExportCommandHandler(scope, Substitute.For<ICurrentUser>(), acceptance,
            unit, TimeProvider.System, NullLogger<QueueLibraryExportCommandHandler>.Instance);

        var result = await InvokeExportLibraryAsync(new ExportLibraryRequest { LibraryId = IdCoder.Encode(123) }, handler);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>().Value.ShouldNotBeNull()
            .Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe("Export.AdminRoleRequired");
        acceptance.ReceivedCalls().ShouldBeEmpty();
        unit.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ExportLibrary_NonAdminWithoutAssignedLibrary_DoesNotScheduleAllCatalogExport()
    {
        var current = Substitute.For<ICurrentUser>();
        current.UserId.Returns(Guid.NewGuid());
        current.HasRole(RomdRoleType.Admin).Returns(false);
        var authorization = Substitute.For<IExportAuthorizationReader>();
        authorization.GetEffectiveLibraryScopeAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ExportScope.Library?)null);
        var acceptance = Substitute.For<IExportJobAcceptance>();
        var unit = Substitute.For<IUnitOfWork>();
        var handler = new QueueLibraryExportCommandHandler(new ResolveExportScopeQueryHandler(current, authorization),
            current, acceptance, unit, TimeProvider.System, NullLogger<QueueLibraryExportCommandHandler>.Instance);

        var result = await InvokeExportLibraryAsync(null, handler);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>().Value.ShouldNotBeNull()
            .Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe("Export.LibraryRequired");
        acceptance.ReceivedCalls().ShouldBeEmpty();
        unit.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task TitleTicketRedemption_RequestCancelledDuringLogicalRead_PropagatesTokenAndDisposesStream()
    {
        using var cancellation = new CancellationTokenSource();
        var exportRepository = Substitute.For<IExportRepository>();
        var scopeHandler = Substitute.For<IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope>>();
        var fileStorage = Substitute.For<IFileStorageService>();
        var sourceStream = new CancellingReadStream(cancellation);
        var romReader = Substitute.For<IRomContentReader>();
        scopeHandler
            .HandleAsync(Arg.Any<ResolveExportScopeQuery>(), Arg.Any<CancellationToken>())
            .Returns((ErrorOr<AuthorizedExportScope>)new AuthorizedExportScope(
                new ExportScope.AllCatalog(),
                EffectiveLibraryUserId: null));
        exportRepository
            .GetExportFilesForTitleAsync(7, Arg.Any<AuthorizedExportScope>(), Arg.Any<CancellationToken>())
            .Returns([
                new ExportFileData(
                    TitleId: 7,
                    TitleName: "Cancellation Test",
                    PlatformId: 3,
                    PlatformName: "Test",
                    RomFileId: 11,
                    FileId: 13,
                    OriginalFilename: "test.rom")
            ]);
        fileStorage.ExistsAsync(13, Arg.Any<CancellationToken>()).Returns(true);
        romReader.OpenAsync(Arg.Any<RomDeliveryTarget>(), cancellation.Token, TimeSpan.FromSeconds(30)).Returns(sourceStream);
        using var tickets = new Romd.Infrastructure.Export.AdminDownloadTickets(TimeProvider.System);
        var prepared = await InvokePrepareTitleDownloadAsync(new Sqid(7), exportRepository, scopeHandler, fileStorage,
            tickets, CancellationToken.None);
        var ticket = prepared.ShouldBeAssignableTo<IValueHttpResult<AdminDownloadTicket>>().ShouldNotBeNull().Value
            .ShouldNotBeNull();
        using var services = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext();
        httpContext.RequestServices = services;
        httpContext.Response.Body = new MemoryStream();
        httpContext.RequestAborted = cancellation.Token;

        var result = await Invoke("RedeemDownload", ticket.DownloadUrl.Split('/').Last(), httpContext, tickets,
            new Romd.Infrastructure.Export.ExportArchiveWriter(fileStorage, romReader), fileStorage, romReader,
            cancellation.Token);

        await Should.ThrowAsync<OperationCanceledException>(() => result.ExecuteAsync(httpContext));
        sourceStream.ObservedCancellationToken.ShouldBe(cancellation.Token);
        sourceStream.WasDisposed.ShouldBeTrue();
        await romReader.Received(1).OpenAsync(Arg.Is<RomDeliveryTarget>(target => target.RomId == 11
            && target.Format == RomDeliveryFormat.Original), cancellation.Token, TimeSpan.FromSeconds(30));
        await fileStorage.DidNotReceiveWithAnyArgs().RetrieveByIdAsync(default, default);
    }

    [Fact]
    public async Task TitleTicket_NonAdminWithoutEffectiveLibrary_ReturnsForbiddenBeforeFileLookup()
    {
        var userId = Guid.NewGuid();
        var currentUser = Substitute.For<ICurrentUser>();
        var authorizationReader = Substitute.For<IExportAuthorizationReader>();
        var exportRepository = Substitute.For<IExportRepository>();
        var fileStorage = Substitute.For<IFileStorageService>();
        var tickets = Substitute.For<IAdminDownloadTickets>();
        currentUser.UserId.Returns(userId);
        currentUser.HasRole(RomdRoleType.Admin).Returns(false);
        authorizationReader
            .GetEffectiveLibraryScopeAsync(userId, Arg.Any<CancellationToken>())
            .Returns((ExportScope.Library?)null);

        var result = await InvokePrepareTitleDownloadAsync(
            new Sqid(7),
            exportRepository,
            new ResolveExportScopeQueryHandler(currentUser, authorizationReader),
            fileStorage,
            tickets,
            CancellationToken.None);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>()
            .StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        var problem = result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>().Value;
        problem.ShouldNotBeNull();
        problem.Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe("Export.LibraryRequired");
        exportRepository.ReceivedCalls().ShouldBeEmpty();
        fileStorage.ReceivedCalls().ShouldBeEmpty();
        tickets.ReceivedCalls().ShouldBeEmpty();
    }

    private static async Task<IResult> InvokeExportLibraryAsync(
        ExportLibraryRequest? request, ICommandHandler<QueueLibraryExportCommand, Guid> handler)
    {
        var method = typeof(ExportEndpoints).GetMethod("ExportLibrary", BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull();
        return await method.Invoke(null, [request, handler, CancellationToken.None]).ShouldBeOfType<Task<IResult>>();
    }

    private static async Task<IResult> InvokePrepareTitleDownloadAsync(
        Sqid titleId,
        IExportRepository exportRepository,
        IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope> scopeHandler,
        IFileStorageService fileStorage,
        IAdminDownloadTickets tickets,
        CancellationToken ct)
    {
        var catalog = Substitute.For<IRomContentCatalog>();
        var hash = Sha256.Parse(new string('a', 64));
        var identity = new RomLogicalIdentity(100, hash, Sha1.Parse(new string('b', 40)), Md5.Parse(new string('c', 32)), Crc32.FromUInt32(1));
        var measurement = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), identity.Sha256, identity.Size, identity,
            RomContainerFacts.Identity.Instance, new RomDecoderIdentity.Managed("endpoint-fixture-hasher/v1"), "identity/v1",
            "endpoint-fixture-verifier/v1", DateTimeOffset.UnixEpoch, null, null);
        var original = MediaDeliveryFactFixtures.MeasuredWhole(31, 13, 41, 11, true,
            RomContainerFacts.Identity.Instance, measurement, identity.Md5, identity.Sha1);
        SetCatalogContent(catalog, new RomContentDescriptor(11, 41, identity,
            "test.rom", [original], [], []));
        var referenceCatalog = Substitute.For<IReferenceCatalogService>();
        referenceCatalog.GetSystemKeysAsync(Arg.Any<CancellationToken>()).Returns(new SystemKeys(new Dictionary<int, string> { [3] = "test" }));
        return await Invoke("PrepareTitleDownload", titleId.Value, RomDeliveryFormat.Original, new DefaultHttpContext(),
            exportRepository, scopeHandler, catalog, fileStorage, tickets, referenceCatalog,
            Substitute.For<IRomChdDeliveryAdmission>(), DeliveryPolicy, ct);
    }

    private static async Task<IResult> Invoke(string method, params object?[] arguments) =>
        await typeof(ExportEndpoints).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
            .ShouldNotBeNull().Invoke(null, arguments).ShouldBeOfType<Task<IResult>>();

    private sealed class CancellingReadStream(CancellationTokenSource cancellation) : Stream
    {
        public bool WasDisposed { get; private set; }
        public CancellationToken ObservedCancellationToken { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 1;
        public override long Position { get; set; }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObservedCancellationToken = cancellationToken;
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void SetCatalogContent(IRomContentCatalog catalog, params RomContentDescriptor[] descriptors)
    {
        catalog.FindByRomIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<int, RomContentDescriptor>)descriptors
                .Where(content => call.ArgAt<IReadOnlyCollection<int>>(0).Contains(content.RomId))
                .ToDictionary(content => content.RomId));
    }
}
