using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Export.Queries.GetArchiveFormats;
using Romd.Admin.Application.Export.Queries.ResolveExportScope;
using Romd.Application.Common.Content;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Application.Tests.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Identity;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Export;

public sealed class GetArchiveFormatsTests
{
    private static readonly Sha256 Logical = Sha256.Parse(new string('a', 64));
    private static readonly Sha256 Artifact = Sha256.Parse(new string('b', 64));
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope> _scopes =
        Substitute.For<IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope>>();
    private readonly IExportRepository _exports = Substitute.For<IExportRepository>();
    private readonly IReleaseDownloadRepository _releases = Substitute.For<IReleaseDownloadRepository>();
    private readonly IRomContentCatalog _catalog = Substitute.For<IRomContentCatalog>();

    public GetArchiveFormatsTests()
    {
        _user.UserId.Returns(UserId);
        _user.HasRole(RomdRoleType.Contributor).Returns(true);
        _scopes.HandleAsync(Arg.Any<ResolveExportScopeQuery>(), Arg.Any<CancellationToken>())
            .Returns(new AuthorizedExportScope(new ExportScope.AllCatalog(), null));
        _catalog.FindByRomIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, RomContentDescriptor> { [8] = Rvz(8, 18), [9] = Original(9, 19) });
        _releases.GetManyAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, ReleaseDownloadPlan>());
    }

    [Fact]
    public async Task HandleAsync_TitleAndReleases_LabelsWhatEachArchiveBindsFromOneContentRead()
    {
        _exports.GetExportFilesForTitleAsync(3, Arg.Any<AuthorizedExportScope>(), Arg.Any<CancellationToken>())
            .Returns([new ExportFileData(3, "Game", 1, "GameCube", 8, 18, "Game.iso"), new ExportFileData(3, "Game", 1, "GameCube", 9, 19, "Manual.bin")]);
        _releases.GetManyAsync(Arg.Is<IReadOnlyCollection<int>>(ids => ids.Order().SequenceEqual(new[] { 5, 6, 7 })), UserId, true,
            Arg.Any<CancellationToken>()).Returns(new Dictionary<int, ReleaseDownloadPlan>
            {
                [5] = new("Disc", [new(18, "Game.iso", 8, PlatformKeys: ["gc"])], 0),
                [6] = new("Manual", [new(19, "Manual.bin", 9, PlatformKeys: ["gc"])], 1)
            });

        var result = await Handler().HandleAsync(new GetArchiveFormatsQuery(3, [5, 6, 7]));

        result.IsError.ShouldBeFalse();
        Describe(result.Value.Title!).ShouldBe(["original:Original", "rvz:RVZ"]);
        result.Value.Releases.Keys.Order().ShouldBe([5, 6, 7]);
        Describe(result.Value.Releases[5]).ShouldBe(["original:Original", "rvz:RVZ"]);
        Describe(result.Value.Releases[6]).ShouldBe(["original:Original"]);
        Describe(result.Value.Releases[7]).ShouldBe(["original:Original"]);
        await _catalog.Received(1).FindByRomIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_TitleArchiveRefused_OffersOnlyOriginal()
    {
        _scopes.HandleAsync(Arg.Any<ResolveExportScopeQuery>(), Arg.Any<CancellationToken>())
            .Returns(ExportErrors.LibraryRequired());

        var result = await Handler().HandleAsync(new GetArchiveFormatsQuery(3, []));

        Describe(result.Value.Title!).ShouldBe(["original:Original"]);
        result.Value.Releases.ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_ReleasesOnly_ReadsNoTitleAndUsesTheUsersLibraryScope()
    {
        _user.HasRole(RomdRoleType.Contributor).Returns(false);
        _releases.GetManyAsync(Arg.Any<IReadOnlyCollection<int>>(), UserId, false, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, ReleaseDownloadPlan> { [5] = new("Disc", [new(18, "Game.iso", 8, PlatformKeys: ["gc"])], 0) });

        var result = await Handler().HandleAsync(new GetArchiveFormatsQuery(null, [5]));

        result.Value.Title.ShouldBeNull();
        Describe(result.Value.Releases[5]).ShouldBe(["original:Original", "rvz:RVZ"]);
        _scopes.ReceivedCalls().ShouldBeEmpty();
        _exports.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_TitleTooLargeForATicket_OffersOnlyOriginalWithoutPlanningIt()
    {
        _exports.GetExportFilesForTitleAsync(3, Arg.Any<AuthorizedExportScope>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, ExportArchivePlanner.MaximumTicketEntries + 1)
                .Select(id => new ExportFileData(3, "Game", 1, "GameCube", 8, 18, $"Game {id}.iso")).ToArray());

        var result = await Handler().HandleAsync(new GetArchiveFormatsQuery(3, []));

        Describe(result.Value.Title!).ShouldBe(["original:Original"]);
        await _catalog.DidNotReceiveWithAnyArgs().FindByRomIdsAsync(default!, default);
    }

    [Fact]
    public async Task HandleAsync_ReleaseTooLargeForATicket_OffersOnlyOriginalWithoutPlanningIt()
    {
        var entries = Enumerable.Range(0, ExportArchivePlanner.MaximumTicketEntries + 1)
            .Select(index => new ExportArchiveEntry(18, $"Disc {index}/Game.iso", 8, PlatformKeys: ["gc"])).ToArray();
        _releases.GetManyAsync(Arg.Any<IReadOnlyCollection<int>>(), UserId, true, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, ReleaseDownloadPlan> { [5] = new("Large", entries, 0) });

        var result = await Handler().HandleAsync(new GetArchiveFormatsQuery(null, [5]));

        Describe(result.Value.Releases[5]).ShouldBe(["original:Original"]);
        await _catalog.DidNotReceiveWithAnyArgs().FindByRomIdsAsync(default!, default);
    }

    [Fact]
    public async Task HandleAsync_NoCurrentUser_OffersOnlyOriginalForEveryRelease()
    {
        _user.UserId.Returns((Guid?)null);

        var result = await Handler().HandleAsync(new GetArchiveFormatsQuery(null, [5]));

        Describe(result.Value.Releases[5]).ShouldBe(["original:Original"]);
        await _releases.DidNotReceiveWithAnyArgs().GetManyAsync(default!, default, default, default);
    }

    [Fact]
    public async Task HandleAsync_InconsistentMemberContent_OffersOnlyOriginalInsteadOfFailingTheTitle()
    {
        _exports.GetExportFilesForTitleAsync(3, Arg.Any<AuthorizedExportScope>(), Arg.Any<CancellationToken>())
            .Returns([new ExportFileData(3, "Game", 1, "GameCube", 8, 18, "Game.iso")]);
        _releases.GetManyAsync(Arg.Any<IReadOnlyCollection<int>>(), UserId, true, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, ReleaseDownloadPlan> { [5] = new("Disc", [new(18, "Game.iso", 8, PlatformKeys: ["gc"])], 0) });
        _catalog.FindByRomIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyDictionary<int, RomContentDescriptor>>(_ => throw new InvalidDataException("Preferred CD output is missing."));

        var result = await Handler().HandleAsync(new GetArchiveFormatsQuery(3, [5]));

        Describe(result.Value.Title!).ShouldBe(["original:Original"]);
        Describe(result.Value.Releases[5]).ShouldBe(["original:Original"]);
    }

    private GetArchiveFormatsQueryHandler Handler() =>
        new(_user, _scopes, _exports, Substitute.For<IReferenceCatalogService>(), _releases, _catalog,
            TestDeliveryPolicies.WithoutRuntimes, new DeliveryAdmission(DeliveryFacts.ManagedBuild, null),
            NullLogger<GetArchiveFormatsQueryHandler>.Instance);

    private static string[] Describe(IEnumerable<Romd.Contracts.Management.Export.ArchiveFormat> formats) =>
        formats.Select(format => $"{format.Format}:{format.Label}").ToArray();

    private static RomContentDescriptor Original(int romId, int fileId) => DeliveryFacts.OriginalContent(romId, fileId, 100);
    private static RomContentDescriptor Rvz(int romId, int fileId) => DeliveryFacts.RvzContent(romId, fileId, 100, 50);
}
