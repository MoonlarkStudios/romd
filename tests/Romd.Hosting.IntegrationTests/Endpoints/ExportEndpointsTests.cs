using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Export.Queries.ResolveExportScope;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Identity;
using Romd.Host.Endpoints;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class ExportEndpointsTests
{
    [Fact]
    public async Task ExportLibrary_NonAdminWithRequestedLibraryId_ReturnsForbiddenBeforeScheduling()
    {
        var exportScheduler = Substitute.For<IExportScheduler>();
        var currentUser = Substitute.For<ICurrentUser>();
        var scopeHandler = Substitute.For<IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope>>();
        currentUser.HasRole(RomdRoleType.Admin).Returns(false);
        scopeHandler
            .HandleAsync(Arg.Any<ResolveExportScopeQuery>(), Arg.Any<CancellationToken>())
            .Returns((ErrorOr<AuthorizedExportScope>)ExportErrors.AdminRoleRequired());

        var result = await InvokeExportLibraryAsync(
            new ExportLibraryRequest { LibraryId = IdCoder.Encode(123) },
            exportScheduler,
            currentUser,
            scopeHandler);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>()
            .StatusCode.ShouldBe(StatusCodes.Status403Forbidden);

        var problem = result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>().Value;
        problem.ShouldNotBeNull();
        problem.Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe("Export.AdminRoleRequired");

        exportScheduler.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ExportLibrary_NonAdminWithoutAssignedLibrary_DoesNotScheduleAllCatalogExport()
    {
        var userId = Guid.NewGuid();
        var exportScheduler = Substitute.For<IExportScheduler>();
        var currentUser = Substitute.For<ICurrentUser>();
        var authorizationReader = Substitute.For<IExportAuthorizationReader>();
        currentUser.UserId.Returns(userId);
        currentUser.HasRole(RomdRoleType.Admin).Returns(false);
        authorizationReader
            .GetEffectiveLibraryScopeAsync(userId, Arg.Any<CancellationToken>())
            .Returns((ExportScope.Library?)null);
        var scopeHandler = new ResolveExportScopeQueryHandler(currentUser, authorizationReader);

        var result = await InvokeExportLibraryAsync(
            request: null,
            exportScheduler,
            currentUser,
            scopeHandler);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>()
            .StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        var problem = result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>().Value;
        problem.ShouldNotBeNull();
        problem.Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe("Export.LibraryRequired");
        await exportScheduler.DidNotReceiveWithAnyArgs()
            .EnqueueLibraryExportAsync(default!, default, default);
    }

    [Fact]
    public async Task ExportTitle_RequestCancelledDuringCasRead_PropagatesTokenAndDisposesStream()
    {
        using var cancellation = new CancellationTokenSource();
        var exportRepository = Substitute.For<IExportRepository>();
        var scopeHandler = Substitute.For<IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope>>();
        var fileStorage = Substitute.For<IFileStorageService>();
        var sourceStream = new CancellingReadStream(cancellation);
        scopeHandler
            .HandleAsync(Arg.Any<ResolveExportScopeQuery>(), cancellation.Token)
            .Returns((ErrorOr<AuthorizedExportScope>)new AuthorizedExportScope(
                new ExportScope.AllCatalog(),
                EffectiveLibraryUserId: null));
        exportRepository
            .GetExportFilesForTitleAsync(7, Arg.Any<AuthorizedExportScope>(), cancellation.Token)
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
        fileStorage.RetrieveByIdAsync(13, cancellation.Token).Returns(sourceStream);

        var result = await InvokeExportTitleAsync(
            new Sqid(7),
            exportRepository,
            scopeHandler,
            fileStorage,
            cancellation.Token);
        using var services = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext();
        httpContext.RequestServices = services;
        httpContext.Response.Body = new MemoryStream();
        httpContext.RequestAborted = cancellation.Token;

        await Should.ThrowAsync<OperationCanceledException>(() => result.ExecuteAsync(httpContext));

        sourceStream.ObservedCancellationToken.ShouldBe(cancellation.Token);
        sourceStream.WasDisposed.ShouldBeTrue();
        await fileStorage.Received(1).RetrieveByIdAsync(13, cancellation.Token);
    }

    [Fact]
    public async Task ExportTitle_NonAdminWithoutEffectiveLibrary_ReturnsForbiddenBeforeFileLookup()
    {
        var userId = Guid.NewGuid();
        var currentUser = Substitute.For<ICurrentUser>();
        var authorizationReader = Substitute.For<IExportAuthorizationReader>();
        var exportRepository = Substitute.For<IExportRepository>();
        var fileStorage = Substitute.For<IFileStorageService>();
        currentUser.UserId.Returns(userId);
        currentUser.HasRole(RomdRoleType.Admin).Returns(false);
        authorizationReader
            .GetEffectiveLibraryScopeAsync(userId, Arg.Any<CancellationToken>())
            .Returns((ExportScope.Library?)null);

        var result = await InvokeExportTitleAsync(
            new Sqid(7),
            exportRepository,
            new ResolveExportScopeQueryHandler(currentUser, authorizationReader),
            fileStorage,
            CancellationToken.None);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>()
            .StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        var problem = result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>().Value;
        problem.ShouldNotBeNull();
        problem.Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe("Export.LibraryRequired");
        exportRepository.ReceivedCalls().ShouldBeEmpty();
        fileStorage.ReceivedCalls().ShouldBeEmpty();
    }

    private static async Task<IResult> InvokeExportLibraryAsync(
        ExportLibraryRequest? request,
        IExportScheduler exportScheduler,
        ICurrentUser currentUser,
        IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope> scopeHandler)
    {
        var method = typeof(ExportEndpoints).GetMethod(
            "ExportLibrary",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.ShouldNotBeNull();

        var task = method.Invoke(null,
        [
            request,
            exportScheduler,
            currentUser,
            scopeHandler,
            CancellationToken.None
        ]);

        return await task.ShouldBeOfType<Task<IResult>>();
    }

    private static async Task<IResult> InvokeExportTitleAsync(
        Sqid titleId,
        IExportRepository exportRepository,
        IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope> scopeHandler,
        IFileStorageService fileStorage,
        CancellationToken ct)
    {
        var method = typeof(ExportEndpoints).GetMethod(
            "ExportTitle",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.ShouldNotBeNull();

        var task = method.Invoke(null, [titleId, exportRepository, scopeHandler, fileStorage, ct]);
        return await task.ShouldBeOfType<Task<IResult>>();
    }

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
}
