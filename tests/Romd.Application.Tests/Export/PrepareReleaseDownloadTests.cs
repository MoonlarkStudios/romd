using NSubstitute;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Export.Queries.PrepareReleaseDownload;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Security;
using Romd.Application.Common.Content;
using Romd.Application.Tests.Content;
using Romd.Domain.Identity;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Export;

public sealed class PrepareReleaseDownloadTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleAsync_RvzAlternate_BindsItsExactFileAndNeverFallsBackAfterSelection(bool available)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Guid.NewGuid());
        var repository = Substitute.For<IReleaseDownloadRepository>();
        repository.GetAsync(2, Arg.Any<Guid>(), false, Arg.Any<CancellationToken>())
            .Returns(new ReleaseDownloadPlan("Release", [new(3, "discs/game.iso", 7)], 0));
        var logical = Sha256.Parse(new string('a', 64));
        var artifact = Sha256.Parse(new string('b', 64));
        var catalog = Substitute.For<IRomContentCatalog>();
        var identity = DeliveryFacts.Identity(100);
        var original = DeliveryFacts.Original(7, 3, 3, 3, identity, true);
        var encoded = DeliveryFacts.Rvz(7, 4, 4, 4, identity, artifact, 50, 1, false, null, null);
        SetCatalogContent(catalog, new RomContentDescriptor(7, 3, identity, "disc.iso", [original, encoded], [], []));
        var storage = Substitute.For<IFileStorageService>();
        storage.ExistsAsync(3, Arg.Any<CancellationToken>()).Returns(true);
        storage.ExistsAsync(4, Arg.Any<CancellationToken>()).Returns(available);
        var tickets = Substitute.For<IAdminDownloadTickets>();
        tickets.Issue(Arg.Any<AdminDownloadPlan>()).Returns(new AdminDownloadTicket("/ticket", DateTimeOffset.UtcNow));

        var result = await new PrepareReleaseDownloadQueryHandler(user, repository, storage, tickets, catalog, TestDeliveryPolicies.WithoutRuntimes, Admission())
            .HandleAsync(new(2, RomDeliveryFormat.Rvz));

        await storage.Received(1).ExistsAsync(4, CancellationToken.None);
        await storage.DidNotReceive().ExistsAsync(3, Arg.Any<CancellationToken>());
        if (available)
        {
            result.IsError.ShouldBeFalse();
            tickets.Received(1).Issue(Arg.Is<AdminDownloadPlan>(plan => plan.Entries.Count == 1
                && plan.Entries[0].Path == "discs/game.rvz" && plan.Entries[0].FileId == 4
                && plan.Entries[0].Target!.Format == RomDeliveryFormat.Rvz
                && plan.Entries[0].Target!.Sha256 == artifact));
        }
        else
        {
            result.FirstError.Code.ShouldBe("Export.FileUnavailable");
            tickets.ReceivedCalls().ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task HandleAsync_RvzNamedOriginalMember_BindsOriginalWithIsoPath()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Guid.NewGuid());
        var repository = Substitute.For<IReleaseDownloadRepository>();
        repository.GetAsync(2, Arg.Any<Guid>(), false, Arg.Any<CancellationToken>())
            .Returns(new ReleaseDownloadPlan("Release", [new(3, "discs/game.rvz", 7)], 0));
        var storage = Substitute.For<IFileStorageService>();
        storage.ExistsAsync(3, Arg.Any<CancellationToken>()).Returns(true);
        var tickets = Substitute.For<IAdminDownloadTickets>();
        AdminDownloadPlan? issued = null;
        tickets.Issue(Arg.Any<AdminDownloadPlan>()).Returns(call =>
        {
            issued = call.Arg<AdminDownloadPlan>();
            return new AdminDownloadTicket("/ticket", DateTimeOffset.UtcNow);
        });

        var result = await new PrepareReleaseDownloadQueryHandler(user, repository, storage, tickets, Catalog(rvz: true), TestDeliveryPolicies.WithoutRuntimes, Admission())
            .HandleAsync(new(2));

        result.IsError.ShouldBeFalse();
        var entry = issued.ShouldNotBeNull().Entries.ShouldHaveSingleItem();
        entry.Path.ShouldBe("discs/game.iso");
        entry.Target.ShouldNotBeNull().Format.ShouldBe(Romd.Domain.Source.Rom.RomDeliveryFormat.Original);
    }

    [Fact]
    public async Task HandleAsync_PartialRelease_IssuesExplicitPartialArchive()
    {
        var user = Substitute.For<ICurrentUser>();
        var id = Guid.NewGuid();
        user.UserId.Returns(id);
        user.HasRole(RomdRoleType.Contributor).Returns(true);
        var repository = Substitute.For<IReleaseDownloadRepository>();
        repository.GetAsync(2, id, true, Arg.Any<CancellationToken>()).Returns(new ReleaseDownloadPlan("Release", [new(3, "disc/file", 7)], 1));
        var storage = Substitute.For<IFileStorageService>();
        storage.ExistsAsync(3, Arg.Any<CancellationToken>()).Returns(true);
        var tickets = Substitute.For<IAdminDownloadTickets>();
        tickets.Issue(Arg.Any<AdminDownloadPlan>()).Returns(new AdminDownloadTicket("/ticket", DateTimeOffset.UtcNow));
        var result = await new PrepareReleaseDownloadQueryHandler(user, repository, storage, tickets, Catalog(), TestDeliveryPolicies.WithoutRuntimes, Admission()).HandleAsync(new(2));
        result.IsError.ShouldBeFalse();
        tickets.Received().Issue(Arg.Is<AdminDownloadPlan>(p => p.Filename == "Release-available-files.zip" && p.Entries.Count == 1 && p.IsArchive));
    }

    [Theory]
    [InlineData("../escape", true)]
    [InlineData("file", false)]
    public async Task HandleAsync_InvalidPathsOrMissingStorage_DoesNotIssueTicket(string path, bool exists)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Guid.NewGuid());
        var repository = Substitute.For<IReleaseDownloadRepository>();
        repository.GetAsync(2, Arg.Any<Guid>(), false, Arg.Any<CancellationToken>()).Returns(new ReleaseDownloadPlan("Release", [new(3, path, 7)], 0));
        var storage = Substitute.For<IFileStorageService>();
        storage.ExistsAsync(3, Arg.Any<CancellationToken>()).Returns(exists);
        var tickets = Substitute.For<IAdminDownloadTickets>();
        var result = await new PrepareReleaseDownloadQueryHandler(user, repository, storage, tickets, Catalog(), TestDeliveryPolicies.WithoutRuntimes, Admission()).HandleAsync(new(2));
        result.IsError.ShouldBeTrue();
        tickets.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_ReleaseAuthorizationDenied_DoesNotReadFormatFactsOrStorage()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Guid.NewGuid());
        var repository = Substitute.For<IReleaseDownloadRepository>();
        var catalog = Catalog();
        var storage = Substitute.For<IFileStorageService>();
        var tickets = Substitute.For<IAdminDownloadTickets>();

        var result = await new PrepareReleaseDownloadQueryHandler(user, repository, storage, tickets, catalog, TestDeliveryPolicies.WithoutRuntimes, Admission())
            .HandleAsync(new(2, RomDeliveryFormat.Rvz));

        result.FirstError.Code.ShouldBe("Export.ReleaseUnavailable");
        catalog.ReceivedCalls().ShouldBeEmpty();
        storage.ReceivedCalls().ShouldBeEmpty();
        tickets.ReceivedCalls().ShouldBeEmpty();
    }

    private static IRomContentCatalog Catalog(bool rvz = false)
    {
        var catalog = Substitute.For<IRomContentCatalog>();
        SetCatalogContent(catalog, rvz ? DeliveryFacts.RvzContent(7, 3, 100, 50) : DeliveryFacts.OriginalContent(7, 3, 100));
        return catalog;
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleAsync_AdmittedPs2Chd_BindsExactArtifactAndRefusesDisappearance(bool available)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Guid.NewGuid());
        var repository = Substitute.For<IReleaseDownloadRepository>();
        repository.GetAsync(2, Arg.Any<Guid>(), false, Arg.Any<CancellationToken>())
            .Returns(new ReleaseDownloadPlan("Release", [new(12, "Discs/game.iso", 7, PlatformKeys: ["ps2"])], 0));
        var content = SyntheticChdContent();
        var catalog = Substitute.For<IRomContentCatalog>();
        SetCatalogContent(catalog, content);
        var storage = Substitute.For<IFileStorageService>();
        storage.ExistsAsync(12, Arg.Any<CancellationToken>()).Returns(available);
        var tickets = Substitute.For<IAdminDownloadTickets>();
        AdminDownloadPlan? issued = null;
        tickets.Issue(Arg.Any<AdminDownloadPlan>()).Returns(call =>
        {
            issued = call.Arg<AdminDownloadPlan>();
            return new AdminDownloadTicket("/ticket", DateTimeOffset.UtcNow);
        });
        var admission = new DeliveryAdmission(DeliveryFacts.ManagedBuild, content.Artifacts![0].ChdEvidence![0].Decoder);
        var result = await new PrepareReleaseDownloadQueryHandler(user, repository, storage, tickets, catalog, TestDeliveryPolicies.WithoutRuntimes, admission)
            .HandleAsync(new(2, RomDeliveryFormat.Chd));
        if (available)
        {
            result.IsError.ShouldBeFalse();
            var entry = issued.ShouldNotBeNull().Entries.ShouldHaveSingleItem();
            entry.Path.ShouldBe("Discs/game.chd");
            entry.FileId.ShouldBe(12);
            entry.Target.ShouldNotBeNull().Format.ShouldBe(RomDeliveryFormat.Chd);
            entry.Target.ReadBinding.ShouldBe(content.Artifacts![0].ChdEvidence![0].Bind());
        }
        else
        {
            result.FirstError.Code.ShouldBe("Export.FileUnavailable");
            issued.ShouldBeNull();
            tickets.ReceivedCalls().ShouldBeEmpty();
        }
        await storage.Received(1).ExistsAsync(12, CancellationToken.None);
    }
    private static RomContentDescriptor SyntheticChdContent(int romId = 7, int fileId = 12) => DeliveryFacts.ChdContent(romId, fileId);
    private static DeliveryAdmission Admission() => new(DeliveryFacts.ManagedBuild, null);

    private static void SetCatalogContent(IRomContentCatalog catalog, params RomContentDescriptor[] descriptors)
    {
        catalog.FindByRomIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<int, RomContentDescriptor>)descriptors
                .Where(content => call.ArgAt<IReadOnlyCollection<int>>(0).Contains(content.RomId))
                .ToDictionary(content => content.RomId));
    }
}
