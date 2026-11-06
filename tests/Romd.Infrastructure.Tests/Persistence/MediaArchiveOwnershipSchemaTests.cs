using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class MediaArchiveOwnershipSchemaTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    public void Dispose() => _database.Dispose();

    private static async Task<Guid> ScopeAsync(RomdDbContext db)
    {
        db.Platforms.Add(new() { Id = 1, Name = "Fixture", ShortName = "fixture" });
        db.Titles.Add(new() { Id = 1, PlatformId = 1, Name = "Fixture", NormalizedName = "fixture", EnrichmentStatus = "Pending" });
        db.CatalogReleases.Add(new() { Id = 1, PlatformId = 1, CatalogTitleId = 1, Name = "Fixture", Fingerprint = "fixture-release" });
        Guid revision = Guid.NewGuid();
        db.OriginalDiscLayouts.Add(new()
        {
            Id = 1,
            SourceJobId = Guid.NewGuid(),
            SourceRelativePath = "disc.cue",
            EntryPoint = "disc.cue",
            DescriptorFileId = 3,
            State = "pending",
            Revision = revision
        });
        await db.SaveChangesAsync();
        return revision;
    }
    private static Task OwnerAsync(RomdDbContext db, int representation, Guid verification, Guid revision, int layout = 1,
        int platform = 1, string fingerprint = "fixture-release", string entry = "disc.cue") => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO romd."CdArchiveOwners" VALUES ({layout},{representation},{verification},{Guid.NewGuid()},{revision},{platform},1,
            {fingerprint},{entry},{DateTimeOffset.UnixEpoch.UtcTicks})
        """);
    private static async Task RetainEncodingHistoryAsync(RomdDbContext db, MediaFactGraphFixture graph)
    {
        var writer = new MediaCreationFactWriter(db);
        Guid boot = await writer.StageCdBootAsync(graph.Cd.Id, graph.Cd.Verifications.Single().Id, "fixture-inspector", DateTimeOffset.UnixEpoch, MediaFactGraphFixture.Boot, default);
        var creation = graph.Creation(boot);
        await writer.StageCreationAsync(graph.Cd.Id, creation, default);
    }

    [Fact]
    public async Task Owner_ComparedNativeGraphWithoutEncodingCreation_RetainsEveryDecoderBuild()
    {
        await using var db = _database.CreateContext();
        var graph = await MediaFactGraphFixture.CreateAsync(db);
        Guid revision = await ScopeAsync(db);
        var prior = graph.Cd.Verifications.Single();
        var next = new RomRepresentationVerification(Guid.NewGuid(), graph.Cd.ArtifactSha256, graph.Cd.ArtifactSize,
            new RomDecoderIdentity.Native(new("linux-arm64", MediaFactGraphFixture.Short('a'), 2)), prior.CheckedProfile, "second-fixture-verifier",
            prior.ObservedAt.AddTicks(1), prior.Outputs, graph.Cd.Container, ((RomContainerFacts.Chd)graph.Cd.Container).StoredRawSha1,
            ((RomContainerFacts.Chd)graph.Cd.Container).StoredOverallSha1);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            (await new MediaFactPublisher(db).AppendVerificationAsync(graph.Cd.Id, next)).ShouldBeTrue();
            await OwnerAsync(db, graph.Cd.Id, prior.Id, revision);
            await transaction.CommitAsync();
        }
        await db.RomFiles.ExecuteDeleteAsync();
        (await new MediaRepresentationCleanup(db).CollectAsync(256)).ShouldBe(1);
        var retained = (await new MediaFactReader(db).ReadAsync([graph.Cd.Id]))[graph.Cd.Id];
        retained.Verifications.Select(row => row.Id).Order().ShouldBe(new[] { prior.Id, next.Id }.Order());
        retained.Creations.ShouldBeEmpty();
        retained.Descriptors.Single().FileId.ShouldBe(3);
        retained.Outputs.ShouldAllBe(output => output.RomId == null);
        (await db.CdArchiveOwners.SingleAsync()).RepresentationId.ShouldBe(graph.Cd.Id);
        (await db.CdArchiveOwners.SingleAsync()).VerificationId.ShouldBe(prior.Id);
    }

    [Fact]
    public async Task Owner_RawInspectionProofCannotClaimOriginalComparison_RefusesOwnership()
    {
        await using var db = _database.CreateContext();
        var graph = await MediaFactGraphFixture.CreateAsync(db);
        Guid revision = await ScopeAsync(db);
        var prior = graph.Cd.Verifications.Single();
        var raw = new RomRepresentationVerification(Guid.NewGuid(), graph.Cd.ArtifactSha256, graph.Cd.ArtifactSize,
            new RomDecoderIdentity.Native(new("linux-arm64", MediaFactGraphFixture.Short('a'), 2)), Romd.Media.PlayStation.ChdCdInspectionService.Profile,
            "raw-fixture-verifier", prior.ObservedAt.AddTicks(1), prior.Outputs, graph.Cd.Container,
            ((RomContainerFacts.Chd)graph.Cd.Container).StoredRawSha1, ((RomContainerFacts.Chd)graph.Cd.Container).StoredOverallSha1);
        await using var transaction = await db.Database.BeginTransactionAsync();
        (await new MediaFactPublisher(db).AppendVerificationAsync(graph.Cd.Id, raw)).ShouldBeTrue();
        var error = await Should.ThrowAsync<PostgresException>(() => OwnerAsync(db, graph.Cd.Id, raw.Id, revision));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Owner_DescriptorDiffersFromAuthenticLayoutDescriptor_RefusesOwnership()
    {
        await using var db = _database.CreateContext();
        var graph = await MediaFactGraphFixture.CreateAsync(db);
        Guid revision = await ScopeAsync(db);
        await db.OriginalDiscLayouts.ExecuteUpdateAsync(set => set.SetProperty(row => row.DescriptorFileId, 2));
        await using var transaction = await db.Database.BeginTransactionAsync();
        var error = await Should.ThrowAsync<PostgresException>(() => OwnerAsync(db, graph.Cd.Id, graph.Cd.Verifications.Single().Id, revision));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Owner_LogicalWithdrawalAndLaterScopeDecisions_RetainsRecoveryArchive()
    {
        await using var db = _database.CreateContext();
        var graph = await MediaFactGraphFixture.CreateAsync(db);
        Guid revision = await ScopeAsync(db);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await RetainEncodingHistoryAsync(db, graph);
            await OwnerAsync(db, graph.Cd.Id, graph.Cd.Verifications.Single().Id, revision);
            await transaction.CommitAsync();
        }
        await db.RomFiles.ExecuteDeleteAsync();
        await db.OriginalDiscLayouts.ExecuteUpdateAsync(set => set.SetProperty(row => row.State, "detached").SetProperty(row => row.Revision, Guid.NewGuid()));
        await db.CatalogReleases.ExecuteUpdateAsync(set => set.SetProperty(row => row.Fingerprint, "later-release").SetProperty(row => row.HasTitleConflict, true));
        (await new MediaRepresentationCleanup(db).CollectAsync(256)).ShouldBe(1);
        var owner = await db.CdArchiveOwners.SingleAsync();
        owner.LayoutRevision.ShouldBe(revision);
        owner.CatalogFingerprint.ShouldBe("fixture-release");
        var retained = (await new MediaFactReader(db).ReadAsync([graph.Cd.Id]))[graph.Cd.Id];
        retained.Outputs.ShouldAllBe(output => output.RomId == null);
        retained.Creations.Count.ShouldBe(1);
        retained.Descriptors.Single().FileId.ShouldBe(3);
    }

    [Fact]
    public async Task Owner_TwoIndependentLayouts_SharedArtifactSurvivesUntilBothOwnersRelease()
    {
        await using var db = _database.CreateContext();
        var graph = await MediaFactGraphFixture.CreateAsync(db);
        Guid revision = await ScopeAsync(db);
        db.OriginalDiscLayouts.Add(new()
        {
            Id = 2,
            SourceJobId = Guid.NewGuid(),
            SourceRelativePath = "copy.cue",
            EntryPoint = "disc.cue",
            DescriptorFileId = 3,
            State = "pending",
            Revision = revision
        });
        await db.SaveChangesAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await RetainEncodingHistoryAsync(db, graph);
            await OwnerAsync(db, graph.Cd.Id, graph.Cd.Verifications.Single().Id, revision);
            await OwnerAsync(db, graph.Cd.Id, graph.Cd.Verifications.Single().Id, revision, layout: 2);
            await transaction.CommitAsync();
        }
        await db.RomFiles.ExecuteDeleteAsync();
        var cleanup = new MediaRepresentationCleanup(db);
        (await cleanup.CollectAsync(256)).ShouldBe(1);
        await db.OriginalDiscLayouts.Where(row => row.Id == 1).ExecuteDeleteAsync();
        (await cleanup.CollectAsync(256)).ShouldBe(0);
        (await db.CdArchiveOwners.SingleAsync()).LayoutId.ShouldBe(2);
        await db.OriginalDiscLayouts.ExecuteDeleteAsync();
        (await cleanup.CollectAsync(256)).ShouldBe(1);
        (await new MediaFactReader(db).ReadAsync([graph.Cd.Id])).ShouldBeEmpty();
        (await db.Files.CountAsync()).ShouldBe(3);
    }

    [Theory]
    [InlineData("missing-verification")]
    [InlineData("wrong-representation")]
    [InlineData("stale-revision")]
    [InlineData("wrong-platform")]
    [InlineData("stale-catalog")]
    [InlineData("missing-cue")]
    [InlineData("unowned-output")]
    public async Task Owner_DirectSqlWrongVerificationScopeOrOwnership_Rejects(string mutation)
    {
        await using var db = _database.CreateContext();
        var graph = await MediaFactGraphFixture.CreateAsync(db);
        Guid revision = await ScopeAsync(db);
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (mutation == "unowned-output")
            await db.RepresentationOutputs.Where(row => row.RepresentationId == graph.Cd.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.RomId, (int?)null));
        var error = await Should.ThrowAsync<PostgresException>(() => OwnerAsync(db, mutation == "wrong-representation" ? graph.Source.Id : graph.Cd.Id,
            mutation == "missing-verification" ? Guid.NewGuid() : graph.Cd.Verifications.Single().Id, mutation == "stale-revision" ? Guid.NewGuid() : revision,
            platform: mutation == "wrong-platform" ? 2 : 1, fingerprint: mutation == "stale-catalog" ? "stale" : "fixture-release",
            entry: mutation == "missing-cue" ? "missing.cue" : "disc.cue"));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }
}
