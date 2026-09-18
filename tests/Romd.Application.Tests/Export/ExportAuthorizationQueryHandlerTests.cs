using NSubstitute;
using Romd.Application.Common.Security;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Export.Queries.AuthorizeExportDownload;
using Romd.Admin.Application.Export.Queries.ResolveExportScope;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Export;

public sealed class ExportAuthorizationQueryHandlerTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const int LibraryId = 17;
    private const long Generation = 5;

    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IExportAuthorizationReader _authorizationReader =
        Substitute.For<IExportAuthorizationReader>();

    public ExportAuthorizationQueryHandlerTests()
    {
        _currentUser.UserId.Returns(UserId);
        _currentUser.HasRole(RomdRoleType.Admin).Returns(false);
        _authorizationReader
            .GetEffectiveLibraryScopeAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new ExportScope.Library(LibraryId, Generation));
        _authorizationReader
            .GetCurrentLibraryScopeAsync(LibraryId, Arg.Any<CancellationToken>())
            .Returns(new ExportScope.Library(LibraryId, Generation));
    }

    [Theory]
    [InlineData(RomdRoleType.User)]
    [InlineData(RomdRoleType.Contributor)]
    [InlineData(RomdRoleType.Manager)]
    public async Task ResolveScope_NonAdminWithoutEffectiveLibrary_ReturnsStableForbidden(
        RomdRoleType role)
    {
        _currentUser.Role.Returns(role);
        _authorizationReader
            .GetEffectiveLibraryScopeAsync(UserId, Arg.Any<CancellationToken>())
            .Returns((ExportScope.Library?)null);

        var result = await CreateScopeHandler().HandleAsync(new ResolveExportScopeQuery(null));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Export.LibraryRequired");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(LibraryId)]
    public async Task ResolveScope_NonAdminWithOwnEffectiveLibrary_ReturnsLibraryScope(int? requestedLibraryId)
    {
        var result = await CreateScopeHandler().HandleAsync(new ResolveExportScopeQuery(requestedLibraryId));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(new AuthorizedExportScope(
            new ExportScope.Library(LibraryId, Generation),
            UserId));
    }

    [Fact]
    public async Task ResolveScope_NonAdminRequestsAnotherLibrary_ReturnsStableForbidden()
    {
        var result = await CreateScopeHandler().HandleAsync(new ResolveExportScopeQuery(LibraryId + 1));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Export.AdminRoleRequired");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(LibraryId)]
    public async Task ResolveScope_AdminCanSelectAllCatalogOrLibrary(int? requestedLibraryId)
    {
        _currentUser.HasRole(RomdRoleType.Admin).Returns(true);

        var result = await CreateScopeHandler().HandleAsync(new ResolveExportScopeQuery(requestedLibraryId));

        result.IsError.ShouldBeFalse();
        if (requestedLibraryId is null)
        {
            result.Value.ShouldBe(new AuthorizedExportScope(
                new ExportScope.AllCatalog(),
                EffectiveLibraryUserId: null));
        }
        else
        {
            result.Value.ShouldBe(new AuthorizedExportScope(
                new ExportScope.Library(LibraryId, Generation),
                EffectiveLibraryUserId: null));
        }

        await _authorizationReader.DidNotReceiveWithAnyArgs()
            .GetEffectiveLibraryScopeAsync(default, default);
    }

    [Fact]
    public async Task ResolveScope_AdminSelectsNonCurrentLibrary_ReturnsStableForbidden()
    {
        _currentUser.HasRole(RomdRoleType.Admin).Returns(true);
        _authorizationReader
            .GetCurrentLibraryScopeAsync(LibraryId, Arg.Any<CancellationToken>())
            .Returns((ExportScope.Library?)null);

        var result = await CreateScopeHandler().HandleAsync(new ResolveExportScopeQuery(LibraryId));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Export.LibraryUnavailable");
    }

    [Fact]
    public async Task AuthorizeDownload_CreatorStillInOriginalEffectiveLibrary_ReturnsJob()
    {
        var job = CreateCompletedJob(LibraryId, UserId);
        var repository = RepositoryReturning(job);

        var result = await CreateDownloadHandler(repository)
            .HandleAsync(new AuthorizeExportDownloadQuery(job.Id));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBeSameAs(job);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(LibraryId + 1)]
    public async Task AuthorizeDownload_CreatorLibraryRevokedOrReassigned_ReturnsForbidden(
        int? currentLibraryId)
    {
        var job = CreateCompletedJob(LibraryId, UserId);
        var repository = RepositoryReturning(job);
        _authorizationReader
            .GetEffectiveLibraryScopeAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(currentLibraryId is null
                ? null
                : new ExportScope.Library(currentLibraryId.Value, Generation));

        var result = await CreateDownloadHandler(repository)
            .HandleAsync(new AuthorizeExportDownloadQuery(job.Id));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Export.AccessDenied");
    }

    [Fact]
    public async Task AuthorizeDownload_DirtyThenSameLibraryRematerialized_OldArchiveStaysForbidden()
    {
        var job = CreateCompletedJob(LibraryId, UserId);
        var repository = RepositoryReturning(job);
        _authorizationReader
            .GetEffectiveLibraryScopeAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(
                (ExportScope.Library?)null,
                new ExportScope.Library(LibraryId, Generation + 1));

        var dirtyResult = await CreateDownloadHandler(repository)
            .HandleAsync(new AuthorizeExportDownloadQuery(job.Id));
        var rematerializedResult = await CreateDownloadHandler(repository)
            .HandleAsync(new AuthorizeExportDownloadQuery(job.Id));

        dirtyResult.IsError.ShouldBeTrue();
        dirtyResult.FirstError.Code.ShouldBe("Export.AccessDenied");
        rematerializedResult.IsError.ShouldBeTrue();
        rematerializedResult.FirstError.Code.ShouldBe("Export.AccessDenied");
    }

    [Fact]
    public async Task AuthorizeDownload_NonAdminAllCatalogCreator_ReturnsForbidden()
    {
        var job = CreateCompletedJob(null, UserId);

        var result = await CreateDownloadHandler(RepositoryReturning(job))
            .HandleAsync(new AuthorizeExportDownloadQuery(job.Id));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Export.AccessDenied");
    }

    [Fact]
    public async Task AuthorizeDownload_DifferentNonAdminRequester_ReturnsForbidden()
    {
        var job = CreateCompletedJob(LibraryId, Guid.NewGuid());

        var result = await CreateDownloadHandler(RepositoryReturning(job))
            .HandleAsync(new AuthorizeExportDownloadQuery(job.Id));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Export.AccessDenied");
        await _authorizationReader.DidNotReceiveWithAnyArgs()
            .GetEffectiveLibraryScopeAsync(default, default);
    }

    [Fact]
    public async Task AuthorizeDownload_AdminOverride_ReturnsJobWithoutLibraryLookup()
    {
        var job = CreateCompletedJob(null, Guid.NewGuid());
        _currentUser.HasRole(RomdRoleType.Admin).Returns(true);

        var result = await CreateDownloadHandler(RepositoryReturning(job))
            .HandleAsync(new AuthorizeExportDownloadQuery(job.Id));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBeSameAs(job);
        await _authorizationReader.DidNotReceiveWithAnyArgs()
            .GetEffectiveLibraryScopeAsync(default, default);
    }

    private ResolveExportScopeQueryHandler CreateScopeHandler() =>
        new(_currentUser, _authorizationReader);

    private AuthorizeExportDownloadQueryHandler CreateDownloadHandler(
        IJobRepository<ExportJob> repository) =>
        new(repository, _currentUser, _authorizationReader);

    private static IJobRepository<ExportJob> RepositoryReturning(ExportJob job)
    {
        var repository = Substitute.For<IJobRepository<ExportJob>>();
        repository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        return repository;
    }

    private static ExportJob CreateCompletedJob(int? libraryId, Guid createdByUserId)
    {
        var job = libraryId is { } id
            ? ExportJob.CreateLibrary(id, Generation, createdByUserId)
            : ExportJob.CreateAllCatalog(createdByUserId);
        job.SetExportPath("/exports/test.zip");
        job.Fail("test terminal state");
        return job;
    }
}
