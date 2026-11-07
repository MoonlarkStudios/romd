using Romd.Infrastructure.Tests.Helpers;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Catalog.Commands.DecideDiscSet;
using Romd.Application.Common.Concurrency;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Libraries.Commands.UpdateLibrary;
using Romd.Infrastructure.Libraries;
using Romd.Persistence.ReferenceData;
using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Pagination;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Romd.Contracts.Management.Models;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Catalog;
using Romd.Domain.Libraries;
using Romd.Persistence;
using Romd.Persistence.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;
using Romd.Infrastructure.Storage;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class DiscSetTests : IDisposable
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_RepeatedSameContext_CommitOrRollbackKeepsDiscSnapshotAndReleasesOwnedTracking(bool rollback)
    {
        await Refresh();
        var accepted = await Decide(await First(), "accept");
        await using var db = database.CreateContext();
        var repository = new LibraryRepository(db);
        var initialGeneration = (await repository.GetByIdAsync(1))!.MaterializationGeneration;
        var definition = (await db.MaterializedLibraryDiscSets.SingleAsync()).DefinitionJson;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var library = (await repository.GetByIdAsync(1))!;
            var rows = await db.MaterializedLibraryReleases.ToListAsync();
            var releases = rows.Select(x => new MaterializedLibraryRelease(x.LibraryId, x.TitleId, x.CatalogReleaseId,
                x.DatGameId, x.DatFileId, x.PlatformId, x.IsEligible, x.IsComplete, x.IsOwned, x.IsPlayable, x.IsBlocked,
                x.BlockReason, x.IsExposed, x.ExposureReason)).ToArray();
            var unitOfWork = new EfUnitOfWork(db);
            await using var transaction = await unitOfWork.BeginTransactionAsync();
            using var publication = await repository.StageMaterializedProjectionsAndActivateAsync(
                1, new([], releases), rows.Count, library.MaterializationRevision);
            publication.Applied.ShouldBeTrue();
            db.ChangeTracker.Entries<MaterializedLibraryDiscSetEntity>().Single().State.ShouldBe(EntityState.Added);
            await unitOfWork.FlushAsync();
            (await db.MaterializedLibraryDiscSets.SingleAsync()).DefinitionJson.ShouldBe(definition);
            if (rollback)
                await transaction.RollbackAsync();
            else
                await transaction.CommitAsync();
        }

        db.ChangeTracker.Entries<MaterializedLibraryDiscSetEntity>().ShouldBeEmpty();
        db.ChangeTracker.Entries<MaterializedLibraryReleaseEntity>().ShouldBeEmpty();
        (await repository.GetByIdAsync(1))!.MaterializationGeneration.ShouldBe(initialGeneration + (rollback ? 0 : 2));
        (await db.MaterializedLibraryDiscSets.SingleAsync()).DefinitionJson.ShouldBe(definition);
        (await Read(accepted)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found>();
    }

    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();
    private readonly Guid userId = Guid.NewGuid();
    private readonly Guid actor = Guid.NewGuid();
    public DiscSetTests()
    {
        using var db = database.CreateContext();
        db.Platforms.Add(new() { Id = 1, Name = "PlayStation", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "PlayStation", BaseCompactLabel = "PS", ShortName = "psx", CanonicalKey = "psx", CatalogRebuildState = CatalogRebuildState.Clean });
        db.Titles.Add(new() { Id = 1, PlatformId = 1, Name = "Game", NormalizedName = "game", EnrichmentStatus = "Completed" });
        db.CatalogSources.Add(new() { Id = 1, Kind = "Dat", Status = "Active" });
        db.Libraries.Add(new() { Id = 1, Name = "Library", ConfigurationState = LibraryConfigurationState.Valid.ToString(), ConfigurationJson = "{}", NeedsMaterialization = false });
        db.Users.Add(new RomdUser { Id = userId, UserName = "disc-user", LibraryId = 1 });
        db.SaveChanges();
        AddClaim(db, 1, "Game (USA) (Disc 1)");
        AddClaim(db, 2, "Game (USA) (Disc 2)");
        db.SaveChanges();
    }
    private static void AddClaim(RomdDbContext db, int id, string name, int source = 1)
    {
        db.SourceEntries.Add(new() { Id = id, CatalogSourceId = source, EntryKey = name, Name = name, PlatformId = 1 });
        db.CatalogReleases.Add(new() { Id = id, PlatformId = 1, CatalogTitleId = 1, Fingerprint = "sha1:" + id, Name = name });
        db.CatalogReleaseSources.Add(new() { SourceEntryId = id, CatalogReleaseId = id, ProviderClaimKey = "claim:" + id, AssertedTitleId = 1 });
        db.CatalogReleaseFiles.Add(new() { CatalogReleaseId = id, FileFingerprint = "file:" + id, Name = "disc.iso", Size = 64 });
        db.MaterializedLibraryReleases.Add(new()
        {
            LibraryId = 1,
            TitleId = 1,
            PlatformId = 1,
            CatalogReleaseId = id,
            DatGameId = id,
            DatFileId = 1,
            IsOwned = true,
            IsExposed = true,
            IsComplete = true,
            IsEligible = true
        });
    }
    private async Task Refresh()
    {
        await using var db = database.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await CatalogTopologyFence.AcquireAsync(db, default);
        await RefreshInTransaction(db);
        await tx.CommitAsync();
    }
    private static async Task RefreshInTransaction(RomdDbContext db)
    {
        var service = new DiscSetService(db);
        var claims = await service.ClaimsAsync(1, default);
        await service.RefreshStagedAsync(1, Romd.Infrastructure.Source.DiscSetProposalBuilder.Create(claims), default);
        await new EfUnitOfWork(db).FlushAsync();
    }
    private async Task<DiscSet> First()
    {
        await using var db = database.CreateContext();
        var result = await new DiscSetService(db).ListAsync(1, null, 100, default);
        result.IsError.ShouldBeFalse();
        return result.Value.Items[0];
    }
    private async Task<DiscSet> Decide(DiscSet set, string decision, IReadOnlyList<DiscSetMemberEdit>? members = null, int? total = null, bool publish = true)
    {
        await using var db = database.CreateContext();
        var result = await DecideAsync(db, DecodeId(set.Id), new(set.Revision, decision, "Manual Game", total, members, "Reviewed"), actor, default);
        result.IsError.ShouldBeFalse(result.IsError ? string.Join(';', result.Errors.Select(x => x.Description)) : "");
        if (publish)
            await Publish(result.Value);
        return result.Value;
    }
    private async Task Publish(DiscSet set)
    {
        await using var db = database.CreateContext();
        var selected = set.Decision == "accepted" ? new[] { DecodeId(set.Id) } : Array.Empty<int>();
        await db.Libraries.Where(x => x.Id == 1).ExecuteUpdateAsync(x => x.SetProperty(l => l.ConfigurationJson,
            JsonSerializer.Serialize(new LibraryConfiguration { SelectedDiscSetIds = selected })));
        var library = await db.Libraries.SingleAsync(x => x.Id == 1);
        var rows = await db.MaterializedLibraryReleases.Where(x => x.LibraryId == 1).ToListAsync();
        var releases = rows.Select(x => new MaterializedLibraryRelease(x.LibraryId, x.TitleId, x.CatalogReleaseId,
            x.DatGameId, x.DatFileId, x.PlatformId, x.IsEligible, x.IsComplete, x.IsOwned, x.IsPlayable, x.IsBlocked,
            x.BlockReason, x.IsExposed, x.ExposureReason)).ToArray();
        (await new LibraryRepository(db).PublishProjectionAsync(db, 1,
            new MaterializedLibraryProjection([], releases), rows.Count, library.MaterializationRevision)).ShouldBeTrue();
    }
    private sealed record VersionedDecision(Guid Revision, string Decision, string? Name, int? ExpectedDiscCount,
        IReadOnlyList<DiscSetMemberEdit>? Members, string? Reason);

    private static Task<ErrorOr.ErrorOr<DiscSet>> DecideAsync(RomdDbContext db, int id, VersionedDecision decision, Guid actor, CancellationToken ct) =>
        new DecideDiscSetCommandHandler(new DiscSetService(db), new EfUnitOfWork(db), TimeProvider.System, NullLogger<DecideDiscSetCommandHandler>.Instance).HandleAsync(
            new(id, new(decision.Decision, decision.Name, decision.ExpectedDiscCount, decision.Members, decision.Reason),
                RevisionPrecondition.For(decision.Revision.ToString()), actor), ct);

    private static int DecodeId(string id) { Romd.Application.Common.Ids.IdCoder.TryDecode(id, out int result).ShouldBeTrue(); return result; }
    /// <summary>The published plan of <paramref name="set" />'s revision; another published revision reads as not found.</summary>
    private async Task<ConsumerLibraryReadResult<ConsumerDiscSetPlan>> Read(DiscSet set, RomdDbContext? context = null)
    {
        var db = context ?? database.CreateContext();
        try
        {
            var read = await new ConsumerDiscSetRepository(db, Romd.PostgreSql.TestSupport.MediaTestServices.CreateContentCatalog(db), RomMediaPolicyComposition.CreateDeliveryPolicy(), new Romd.Persistence.MediaFacts.DerivedDescriptorReader(db), null, null).ReadStatusAsync(new(userId), DecodeId(set.Id), default);
            return read is ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found found && found.Value.Revision != set.Revision
                ? new ConsumerLibraryReadResult<ConsumerDiscSetPlan>.ItemNotFound(found.LibraryId)
                : read;
        }
        finally { if (context is null) await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Refresh_UnknownCount_ProducesOrderedStableProposalWithoutInferringTotal()
    {
        await Refresh();
        var first = await First();
        first.Proposal.ExpectedDiscCount.ShouldBeNull();
        first.Proposal.Members.Select(x => x.Ordinal).ShouldBe([1, 2]);
        first.Proposal.ReviewReasons.ShouldBeEmpty();
        await Refresh();
        var next = await First();
        next.Id.ShouldBe(first.Id);
        next.Revision.ShouldBe(first.Revision);
    }
    [Fact]
    public async Task Refresh_RegionsRevisionsAndSources_NeverMergeByCleanedTitle()
    {
        using (var db = database.CreateContext())
        {
            db.CatalogSources.Add(new() { Id = 2, Kind = "Dat", Status = "Active" });
            db.SaveChanges();
            AddClaim(db, 3, "Game (Europe) (Disc 1)");
            AddClaim(db, 4, "Game (USA) (Rev 1) (Disc 2)");
            AddClaim(db, 5, "Game (USA) (Disc 1)", 2);
            db.SaveChanges();
        }
        await Refresh();
        using var read = database.CreateContext();
        var sets = await read.DiscSets.ToListAsync();
        sets.Count.ShouldBe(4);
        sets.Single(x => x.CatalogSourceId == 2).ProposalJson.ShouldContain("SingleObservedDisc");
    }
    [Fact]
    public async Task Refresh_ConflictingTotalsAndMissingOrdinals_RequiresCorrection()
    {
        using (var db = database.CreateContext())
        {
            await db.SourceEntries.Where(x => x.Id == 1).ExecuteUpdateAsync(x => x.SetProperty(e => e.Name, "Game (USA) (Disc 1 of 3)"));
            await db.SourceEntries.Where(x => x.Id == 2).ExecuteUpdateAsync(x => x.SetProperty(e => e.Name, "Game (USA) (Disc 3 of 4)"));
        }
        await Refresh();
        var set = await First();
        set.Proposal.ReviewReasons.ShouldContain("ConflictingTotals");
        set.Proposal.ReviewReasons.ShouldContain("MissingDiscNumbers");
        set.Proposal.ExpectedDiscCount.ShouldBeNull();
        using var db2 = database.CreateContext();
        var accepted = await DecideAsync(db2, DecodeId(set.Id), new(set.Revision, "accept", null, null, null, null), actor, default);
        accepted.IsError.ShouldBeTrue();
    }
    [Fact]
    public async Task Refresh_DiscZeroSibling_KeepsUnusualRoleVisibleAndBlocksBlindAcceptance()
    {
        using (var db = database.CreateContext())
        { AddClaim(db, 3, "Game (USA) (Disc 0)"); db.SaveChanges(); }
        await Refresh();
        using var read = database.CreateContext();
        var result = await new DiscSetService(read).ListAsync(1, null, 100, default);
        result.Value.Items.Count.ShouldBe(2);
        result.Value.Items.ShouldContain(x => x.Proposal.ReviewReasons.Contains("UnresolvedDiscRoleSibling"));
        result.Value.Items.ShouldContain(x => x.Proposal.ReviewReasons.Contains("DiscNumberOutOfRange"));
    }
    [Fact]
    public async Task Refresh_DuplicateOrdinals_KeepsBothClaimsForReview()
    {
        using (var db = database.CreateContext())
        { AddClaim(db, 3, "Game (USA) (Disc 1 of 2)"); db.SaveChanges(); }
        await Refresh();
        var set = await First();
        set.Proposal.Members.Count.ShouldBe(3);
        set.Proposal.ReviewReasons.ShouldContain("DuplicateDiscNumber");
    }
    [Fact]
    public async Task Refresh_ReplacementWithSameNaturalKeyAndContent_ReconnectsCorrection()
    {
        await Refresh();
        var accepted = await Decide(await First(), "accept");
        using (var db = database.CreateContext())
        {
            await db.SourceEntries.Where(x => x.Id == 2).ExecuteDeleteAsync();
            const string name = "Game (USA) (Disc 2)";
            db.SourceEntries.Add(new() { Id = 99, CatalogSourceId = 1, EntryKey = name, Name = name, PlatformId = 1 });
            db.CatalogReleaseSources.Add(new() { SourceEntryId = 99, CatalogReleaseId = 2, ProviderClaimKey = "replacement:2", AssertedTitleId = 1 });
            await db.SaveChangesAsync();
        }
        await Refresh();
        var next = await First();
        next.Id.ShouldBe(accepted.Id);
        next.Correction!.Members.All(x => x.Issue is null).ShouldBeTrue();
        (await Read(next)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found>();
    }
    [Fact]
    public async Task Decide_Reset_ExplicitlyRemovesPinnedCorrection()
    {
        await Refresh();
        var accepted = await Decide(await First(), "accept");
        var reset = await Decide(accepted, "reset");
        reset.Decision.ShouldBe("proposed");
        reset.Correction.ShouldBeNull();
        (await Read(reset)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.ItemNotFound>();
    }
    [Fact]
    public async Task Read_SourceDormantOrCatalogDirty_PreservesPublishedGroupUntilRefresh()
    {
        await Refresh();
        var accepted = await Decide(await First(), "accept");
        using (var db = database.CreateContext())
            await db.CatalogSources.ExecuteUpdateAsync(x => x.SetProperty(e => e.Status, "Dormant"));
        (await Read(accepted)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found>();
        using (var db = database.CreateContext())
        {
            await db.CatalogSources.ExecuteUpdateAsync(x => x.SetProperty(e => e.Status, "Active"));
            await db.Platforms.ExecuteUpdateAsync(x => x.SetProperty(e => e.CatalogRebuildState, CatalogRebuildState.Dirty));
        }
        (await Read(accepted)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found>();
    }
    [Fact]
    public async Task Decide_RejectAndRebuild_PreservesNegativeDecisionAcrossContexts()
    {
        await Refresh();
        var rejected = await Decide(await First(), "reject");
        await Refresh();
        var next = await First();
        next.Id.ShouldBe(rejected.Id);
        next.Decision.ShouldBe("rejected");
        next.DecidedBy.ShouldBe(actor);
        (await Read(next)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.ItemNotFound>();
    }
    [Fact]
    public async Task Decide_CorrectReorderAndRefresh_PinsMembershipAndExplicitTotal()
    {
        await Refresh();
        var set = await First();
        var corrected = await Decide(set, "correct",
            [new(set.Proposal.Members[1].EntryKey, 1), new(set.Proposal.Members[0].EntryKey, 2)], 2);
        using (var db = database.CreateContext())
        { AddClaim(db, 3, "Game (USA) (Disc 3)"); db.SaveChanges(); }
        await Refresh();
        var next = await First();
        next.Proposal.Members.Count.ShouldBe(3);
        next.Correction!.Members.Count.ShouldBe(2);
        next.Correction.Members[0].EntryKey.ShouldBe(corrected.Correction!.Members[0].EntryKey);
        next.Correction.ExpectedDiscCount.ShouldBe(2);
        (await Read(corrected)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found>().Value.Discs.Select(x => x.Release.ReleaseId).ShouldBe([2, 1]);
    }
    [Fact]
    public async Task Decide_StaleRevision_RejectsLostUpdate()
    {
        await Refresh();
        var stale = await First();
        await Decide(stale, "reject");
        using var db = database.CreateContext();
        var result = await DecideAsync(db, DecodeId(stale.Id), new(stale.Revision, "reset", null, null, null, null), actor, default);
        result.FirstError.Code.ShouldBe("DiscSet.PreconditionFailed");
    }
    [Theory]
    [InlineData("changed")]
    [InlineData("removed")]
    [InlineData("renamed")]
    public async Task Refresh_ChangedOrMissingSource_PreservesCorrectionAndFailsClosed(string change)
    {
        await Refresh();
        var accepted = await Decide(await First(), "accept");
        using (var db = database.CreateContext())
        {
            if (change == "changed")
                await db.CatalogReleases.Where(x => x.Id == 2).ExecuteUpdateAsync(x => x.SetProperty(e => e.Fingerprint, "changed-hashes"));
            else if (change == "removed")
                await db.SourceEntries.Where(x => x.Id == 2).ExecuteDeleteAsync();
            else
                await db.SourceEntries.Where(x => x.Id == 2).ExecuteUpdateAsync(x => x.SetProperty(e => e.EntryKey, "renamed source key"));
        }
        await Refresh();
        var next = await First();
        next.Id.ShouldBe(accepted.Id);
        next.Correction!.Members.Count.ShouldBe(2);
        next.Correction.Members.ShouldContain(x => x.Issue == (change == "changed" ? "ContentChanged" : "SourceUnavailable"));
        if (change == "changed")
            (await Read(accepted)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.ProjectionInconsistent>();
        else
        {
            (await Read(accepted)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found>();
            await Publish(next);
            (await Read(next)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.ItemNotFound>();
        }
    }
    [Fact]
    public async Task Refresh_SourceDeletion_RetainsRejectedDecisionTombstone()
    {
        await Refresh();
        await Decide(await First(), "reject");
        using (var db = database.CreateContext())
        { await db.SourceEntries.ExecuteDeleteAsync(); await db.CatalogSources.ExecuteDeleteAsync(); }
        await Refresh();
        var next = await First();
        next.Decision.ShouldBe("rejected");
        next.Proposal.ReviewReasons.ShouldContain("SourceUnavailable");
    }
    [Theory]
    [InlineData("accept")]
    [InlineData("reject")]
    public async Task SourceRemoval_ManualDecision_RetainsTitleForUnresolvedReview(string decision)
    {
        await Refresh();
        await Decide(await First(), decision);
        using (var db = database.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await CatalogTopologyFence.AcquireAsync(db, default);
            await db.SourceEntries.ExecuteDeleteAsync();
            await db.CatalogSources.ExecuteDeleteAsync();
            (await new TitleOrphanRepository(db).DeleteOrRetainAsync(1)).ShouldBeFalse();
            await tx.CommitAsync();
        }
        await Refresh();
        var set = await First();
        set.Proposal.ReviewReasons.ShouldContain("SourceUnavailable");
        using var read = database.CreateContext();
        (await read.Titles.SingleAsync()).CatalogState.ShouldBe(TitleCatalogState.UserOnly);
    }

    [Fact]
    public async Task Read_OneUnexposedMember_RejectsEntireSet()
    {
        await Refresh();
        var set = await Decide(await First(), "accept");
        using (var db = database.CreateContext())
            await db.MaterializedLibraryReleases.Where(x => x.CatalogReleaseId == 2).ExecuteUpdateAsync(x => x.SetProperty(e => e.IsExposed, false));
        (await Read(set)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.ItemNotFound>();
    }
    [Fact]
    public async Task Read_MembersSplitBetweenLibraries_CannotCombineGrants()
    {
        await Refresh();
        var set = await Decide(await First(), "accept");
        using (var db = database.CreateContext())
        {
            db.Libraries.Add(new() { Id = 2, Name = "Other", ConfigurationJson = "{}", ConfigurationState = "Valid" });
            await db.SaveChangesAsync();
            await db.MaterializedLibraryReleases.Where(x => x.CatalogReleaseId == 2).ExecuteUpdateAsync(x => x.SetProperty(e => e.LibraryId, 2));
        }
        (await Read(set)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.ProjectionInconsistent>();
    }
    [Fact]
    public async Task List_InaccessibleFirstBatch_ContinuesToAuthorizedSets()
    {
        await Refresh();
        await Decide(await First(), "accept");
        using (var db = database.CreateContext())
        {
            var original = await db.MaterializedLibraryDiscSets.SingleAsync();
            var definition = JsonSerializer.Deserialize<PublishedDiscSetDefinition>(original.DefinitionJson)!;
            var unresolved = definition with { Members = definition.Members.Select(m => m with { Fingerprint = "unresolved" }).ToArray() };
            await db.MaterializedLibraryDiscSets.ExecuteUpdateAsync(x => x.SetProperty(e => e.DefinitionJson, JsonSerializer.Serialize(unresolved)));
            for (int id = 2; id <= 102; id++)
                db.MaterializedLibraryDiscSets.Add(new()
                {
                    LibraryId = 1,
                    DiscSetId = id,
                    PlatformId = 1,
                    TitleId = 1,
                    Revision = original.Revision,
                    DefinitionJson = JsonSerializer.Serialize(unresolved)
                });
            db.MaterializedLibraryDiscSets.Add(new()
            {
                LibraryId = 1,
                DiscSetId = 1000,
                PlatformId = 1,
                TitleId = 1,
                Revision = original.Revision,
                DefinitionJson = original.DefinitionJson
            });
            await db.SaveChangesAsync();
        }
        using var read = database.CreateContext();
        var result = await new ConsumerDiscSetRepository(read, Romd.PostgreSql.TestSupport.MediaTestServices.CreateContentCatalog(read), RomMediaPolicyComposition.CreateDeliveryPolicy(), new Romd.Persistence.MediaFacts.DerivedDescriptorReader(read), null, null).ListPageAsync(new(userId), 1, null, 100, default);
        result.ShouldBeOfType<ConsumerLibraryReadResult<PagedList<ConsumerDiscSetPlan>>.Found>().Value.Items.Single().Id.ShouldBe(1000);
    }

    [Fact]
    public async Task Read_ConcurrentGroupFilesAndAuthorizationMutation_UsesOneSnapshot()
    {
        await Refresh();
        var set = await Decide(await First(), "accept");
        var hook = new AfterFirstFileRead(async () =>
        {
            using var writer = database.CreateContext();
            await writer.MaterializedLibraryReleases.Where(x => x.CatalogReleaseId == 2).ExecuteUpdateAsync(x => x.SetProperty(e => e.IsExposed, false));
            await writer.CatalogReleaseFiles.Where(x => x.CatalogReleaseId == 2).ExecuteUpdateAsync(x => x.SetProperty(e => e.Name, "changed.iso"));
            await writer.DiscSets.ExecuteUpdateAsync(x => x.SetProperty(e => e.Revision, Guid.NewGuid()));
        });
        using var db = new RomdDbContext(database.CreateOptions(b => b.AddInterceptors(hook)));
        var result = (await Read(set, db)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found>();
        hook.Fired.ShouldBeTrue();
        result.Value.Revision.ShouldBe(set.Revision);
        result.Value.Discs[1].Release.Items.Single().RelativePath.ShouldBe("disc.iso");
        (await Read(set)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.ItemNotFound>();
    }
    [Fact]
    public async Task Refresh_Rollback_PreservesOriginalProposalAndDecision()
    {
        await Refresh();
        var set = await Decide(await First(), "accept");
        using (var db = database.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.SourceEntries.Where(x => x.Id == 2).ExecuteDeleteAsync();
            await RefreshInTransaction(db);
            await tx.RollbackAsync();
        }
        var next = await First();
        next.Revision.ShouldBe(set.Revision);
        next.Correction!.Members.All(x => x.Issue is null).ShouldBeTrue();
    }
    [Fact]
    public async Task Publication_AcceptingOrCorrectingDoesNotLeakIntoPublishedLibrary()
    {
        await Refresh();
        var accepted = await Decide(await First(), "accept", publish: false);
        (await Read(accepted)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.ItemNotFound>();
        await Publish(accepted);
        var original = (await Read(accepted)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found>().Value;
        var correction = await Decide(accepted, "correct", [new(accepted.Correction!.Members[1].EntryKey, 1),
            new(accepted.Correction.Members[0].EntryKey, 2)], 2, publish: false);
        var stillPublished = (await Read(accepted)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found>().Value;
        stillPublished.Revision.ShouldBe(original.Revision);
        stillPublished.Discs.Select(x => x.Release.ReleaseId).ShouldBe([1, 2]);
        (await Read(correction)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.ItemNotFound>();
        using (var pending = database.CreateContext())
            (await pending.Libraries.SingleAsync()).NeedsMaterialization.ShouldBeTrue();
        await Publish(correction);
        (await Read(correction)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found>().Value.Discs.Select(x => x.Release.ReleaseId).ShouldBe([2, 1]);
    }
    [Fact]
    public async Task Publication_PendingDeselectionKeepsSnapshotUntilExplicitRefresh()
    {
        await Refresh();
        var accepted = await Decide(await First(), "accept");
        using (var db = database.CreateContext())
            await db.Libraries.ExecuteUpdateAsync(x => x.SetProperty(l => l.ConfigurationJson, "{}"));
        (await Read(accepted)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found>();
        using (var db = database.CreateContext())
        {
            var token = await db.Libraries.Select(x => x.MaterializationRevision).SingleAsync();
            var releases = await db.MaterializedLibraryReleases.ToListAsync();
            await new LibraryRepository(db).PublishProjectionAsync(db, 1, new([], releases.Select(x =>
                new MaterializedLibraryRelease(1, x.TitleId, x.CatalogReleaseId, x.DatGameId, x.DatFileId, x.PlatformId,
                    x.IsEligible, x.IsComplete, x.IsOwned, x.IsPlayable, x.IsBlocked, x.BlockReason, x.IsExposed, x.ExposureReason)).ToArray()), 2, token);
        }
        (await Read(accepted)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.ItemNotFound>();
    }
    [Fact]
    public async Task Status_ExposedMissingMemberIsVisibleWithoutGrantingOwnership()
    {
        await Refresh();
        var accepted = await Decide(await First(), "accept");
        using (var db = database.CreateContext())
            await db.MaterializedLibraryReleases.Where(x => x.CatalogReleaseId == 2)
            .ExecuteUpdateAsync(x => x.SetProperty(r => r.IsOwned, false).SetProperty(r => r.IsComplete, false));
        var plan = (await Read(accepted)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found>().Value;
        plan.Discs[1].Readiness.ShouldBe("missing-media");
        plan.Discs[1].Release.Items.ShouldBeEmpty();
    }
    [Fact]
    public async Task LibraryOptions_UsesEditablePolicyAndKeepsUnresolvedSelectionsVisible()
    {
        await Refresh();
        var accepted = await Decide(await First(), "accept");
        await using var db = database.CreateContext();
        var service = new DiscSetService(db);
        var eligible = await service.ListLibraryOptionsAsync(1, new HashSet<int> { 1, 2 }, [DecodeId(accepted.Id)], null, null, 100, default);
        eligible.Value.Page.Items.Single().Name.ShouldBe(accepted.Correction!.Name);
        eligible.Value.Selected.Single().IsEligible.ShouldBeTrue();
        var blocked = await service.ListLibraryOptionsAsync(1, new HashSet<int> { 1 }, [DecodeId(accepted.Id), 999], null, null, 100, default);
        blocked.Value.Page.Items.ShouldBeEmpty();
        blocked.Value.Selected[0].Issue.ShouldBe("OutsideEditableLibraryPolicy");
        blocked.Value.Selected[1].Issue.ShouldBe("MissingDiscSet");
        blocked.Value.SelectedIds.ShouldContain(Romd.Application.Common.Ids.IdCoder.Encode(999));
    }
    [Theory]
    [InlineData("fingerprint")]
    [InlineData("missing-projection")]
    [InlineData("duplicate-projection")]
    [InlineData("title-mismatch")]
    public async Task Read_PublishedSnapshotInconsistent_IsUnavailableRatherThanRevoked(string mutation)
    {
        await Refresh();
        var accepted = await Decide(await First(), "accept");
        using (var db = database.CreateContext())
        {
            if (mutation == "fingerprint")
                await db.CatalogReleases.Where(x => x.Id == 2).ExecuteUpdateAsync(x => x.SetProperty(e => e.Fingerprint, "changed"));
            else if (mutation == "missing-projection")
                await db.MaterializedLibraryReleases.Where(x => x.CatalogReleaseId == 2).ExecuteDeleteAsync();
            else if (mutation == "title-mismatch")
                await db.CatalogReleases.Where(x => x.Id == 2).ExecuteUpdateAsync(x => x.SetProperty(e => e.HasTitleConflict, true));
            else
            {
                db.MaterializedLibraryReleases.Add(new()
                {
                    LibraryId = 1,
                    TitleId = 1,
                    PlatformId = 1,
                    CatalogReleaseId = 2,
                    DatGameId = 999,
                    DatFileId = 1,
                    IsOwned = true,
                    IsExposed = true,
                    IsComplete = true,
                    IsEligible = true
                });
                await db.SaveChangesAsync();
            }
        }
        (await Read(accepted)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.ProjectionInconsistent>();
    }
    [Fact]
    public async Task Correction_ConfiguredUnpublishedSetInvalidatesInFlightPublication()
    {
        await Refresh();
        var accepted = await Decide(await First(), "accept", publish: false);
        Guid prior;
        using (var db = database.CreateContext())
        {
            await db.Libraries.ExecuteUpdateAsync(x => x.SetProperty(l => l.ConfigurationJson,
                JsonSerializer.Serialize(new LibraryConfiguration { SelectedDiscSetIds = [DecodeId(accepted.Id)] }))
                .SetProperty(l => l.NeedsMaterialization, false));
            prior = await db.Libraries.Select(x => x.MaterializationRevision).SingleAsync();
            (await db.MaterializedLibraryDiscSets.CountAsync()).ShouldBe(0);
        }
        await Decide(accepted, "correct", [new(accepted.Correction!.Members[0].EntryKey, 1),
            new(accepted.Correction.Members[1].EntryKey, 2)], 2, publish: false);
        using (var db = database.CreateContext())
        {
            var library = await db.Libraries.SingleAsync();
            library.NeedsMaterialization.ShouldBeTrue();
            library.MaterializationRevision.ShouldNotBe(prior);
            (await new LibraryRepository(db).PublishProjectionAsync(db, 1, new([], []), 0, prior)).ShouldBeFalse();
            (await db.Libraries.SingleAsync()).NeedsMaterialization.ShouldBeTrue();
        }
    }
    [Theory]
    [InlineData("reject")]
    [InlineData("reset")]
    public async Task LibraryUpdateAndEvaluation_RetainedUnacceptedSet_PreservesChoiceWithoutPublishingIt(string decision)
    {
        await Refresh();
        var accepted = await Decide(await First(), "accept");
        int setId = DecodeId(accepted.Id);
        await Decide(accepted, decision, publish: false);
        var configuration = new LibraryConfiguration { SelectedDiscSetIds = [setId], ShowMissingGames = true };
        await using (var db = database.CreateContext())
        {
            var handler = new UpdateLibraryCommandHandler(new ReferenceCatalogService(db, TestReferenceBlobs.For(db)),
                new LibraryRepository(db), Substitute.For<IAdminEventOutbox>(), new EfUnitOfWork(db),
                NullLogger<UpdateLibraryCommandHandler>.Instance, RomMediaPolicyComposition.CreateDeliveryPolicy(), clock: TimeProvider.System);
            var updated = await handler.HandleAsync(new(1, "Renamed Library", configuration, false));
            updated.IsError.ShouldBeFalse();
        }
        await using (var db = database.CreateContext())
        {
            var library = await new LibraryRepository(db).GetByIdAsync(1);
            library!.Name.ShouldBe("Renamed Library");
            library.Configuration.SelectedDiscSetIds.ShouldBe([setId]);
            var provider = Substitute.For<ILibraryCandidateReader>();
            provider.GetCandidatesAsync(Arg.Any<LibraryConfiguration>(), Arg.Any<CancellationToken>()).Returns(new List<TitleCandidates>());
            var catalog = Substitute.For<ICatalogProjectionService>();
            catalog.GetPlatformIdsNeedingRebuildAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
            var evaluation = await new LibraryDraftEvaluator(db, new LibraryRepository(db), provider, catalog)
                .EvaluateAsync(1, configuration, "all", null, 0, default);
            evaluation.IsError.ShouldBeFalse();
            db.ChangeTracker.HasChanges().ShouldBeFalse();
        }
        // The saved choice remains reviewable, but an explicit refresh cannot publish
        // a grouping whose current decision is rejected or reset.
        await using (var db = database.CreateContext())
        {
            var library = await db.Libraries.SingleAsync(x => x.Id == 1);
            var rows = await db.MaterializedLibraryReleases.Where(x => x.LibraryId == 1).ToListAsync();
            var releases = rows.Select(x => new MaterializedLibraryRelease(x.LibraryId, x.TitleId, x.CatalogReleaseId,
                x.DatGameId, x.DatFileId, x.PlatformId, x.IsEligible, x.IsComplete, x.IsOwned, x.IsPlayable, x.IsBlocked,
                x.BlockReason, x.IsExposed, x.ExposureReason)).ToArray();
            (await new LibraryRepository(db).PublishProjectionAsync(db, 1,
                new([], releases), rows.Count, library.MaterializationRevision)).ShouldBeTrue();
        }
        (await Read(accepted)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.ItemNotFound>();
        await using var read = database.CreateContext();
        LibraryEntity.DeserializeConfig((await read.Libraries.SingleAsync()).ConfigurationJson).SelectedDiscSetIds.ShouldBe([setId]);
    }

    [Theory]
    [InlineData("reject")]
    [InlineData("reset")]
    public async Task LibraryUpdateAndEvaluation_NewUnacceptedSet_CannotBorrowAnotherLibrarysRetainedChoice(string decision)
    {
        await Refresh();
        var accepted = await Decide(await First(), "accept");
        await Decide(accepted, decision, publish: false);
        var configuration = new LibraryConfiguration { SelectedDiscSetIds = [DecodeId(accepted.Id)] };
        await using var db = database.CreateContext();
        db.Libraries.Add(new() { Id = 2, Name = "Other Library", ConfigurationJson = "{}", ConfigurationState = "Valid" });
        await db.SaveChangesAsync();
        var repository = new LibraryRepository(db);
        (await repository.ValidateConfigurationReferencesAsync(configuration)).ShouldNotBeNull(); // Creation remains strict.
        (await repository.ValidateConfigurationReferencesAsync(configuration with { SelectedDiscSetIds = [DecodeId(accepted.Id), 999] },
            existingLibraryId: 1)).ShouldNotBeNull(); // Retention never creates missing identities.
        var handler = new UpdateLibraryCommandHandler(new ReferenceCatalogService(db, TestReferenceBlobs.For(db)),
            repository, Substitute.For<IAdminEventOutbox>(), new EfUnitOfWork(db), NullLogger<UpdateLibraryCommandHandler>.Instance, RomMediaPolicyComposition.CreateDeliveryPolicy(), clock: TimeProvider.System);
        (await handler.HandleAsync(new(2, "Changed", configuration, false))).FirstError.Code.ShouldBe("Libraries.InvalidConfiguration");
        var provider = Substitute.For<ILibraryCandidateReader>();
        var evaluation = await new LibraryDraftEvaluator(db, repository, provider, Substitute.For<ICatalogProjectionService>())
            .EvaluateAsync(2, configuration, "all", null, 0, default);
        evaluation.FirstError.Code.ShouldBe("Libraries.InvalidConfiguration");
        await provider.DidNotReceiveWithAnyArgs().GetCandidatesAsync(default!, default);
        (await repository.GetByIdAsync(2))!.Configuration.SelectedDiscSetIds.ShouldBeEmpty();
    }

    public void Dispose() => database.Dispose();
    private sealed class AfterFirstFileRead(Func<Task> mutate) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("\"CatalogReleaseFiles\"", StringComparison.Ordinal))
            { Fired = true; await mutate(); }
            return result;
        }
    }
}
