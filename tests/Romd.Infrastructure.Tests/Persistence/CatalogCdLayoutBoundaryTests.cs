using Romd.Application.Common.Content;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Npgsql;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.MediaFacts;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>Synthetic codec facts exercise the database boundary; they do not qualify a native decoder.</summary>
public sealed partial class CatalogCdLayoutBoundaryTests : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();
    private static readonly DateTimeOffset Observed = DateTimeOffset.UnixEpoch.AddDays(1);
    private static readonly RomChdDecoderBuild Decoder = new("linux-x64", Sha1.Parse(new string('a', 40)), 2);
    private static readonly RomLogicalIdentity Bin = MediaFactFixtures.Identity(new byte[64 * 2352]);
    private static readonly RomLogicalIdentity Cue = MediaFactFixtures.Identity("generated cue bytes"u8);
    private static readonly RomLogicalIdentity ExpectedCue = MediaFactFixtures.Identity("independently recorded original cue"u8);

    public async Task InitializeAsync()
    {
        await using var db = database.CreateContext();
        db.Platforms.Add(new() { Id = 1, Name = "Fixture", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Fixture", BaseCompactLabel = "FX", ShortName = "fixture", CanonicalKey = "fixture" });
        db.Titles.Add(new() { Id = 1, PlatformId = 1, Name = "Fixture", NormalizedName = "fixture", EnrichmentStatus = "Completed" });
        db.CatalogReleases.Add(new() { Id = 1, PlatformId = 1, CatalogTitleId = 1, Fingerprint = "sha1:fixture", Name = "Fixture" });
        db.CatalogReleaseFiles.AddRange(
            new() { Id = 1, CatalogReleaseId = 1, FileFingerprint = "bin", Name = "track.bin", Size = Bin.Size, Sha1 = Bin.Sha1, Md5 = Bin.Md5, Crc = Bin.Crc32 },
            new() { Id = 2, CatalogReleaseId = 1, FileFingerprint = "cue", Name = "disc.cue", Size = ExpectedCue.Size, Sha1 = ExpectedCue.Sha1, Md5 = ExpectedCue.Md5, Crc = ExpectedCue.Crc32 });
        db.Files.AddRange(new() { Id = 1, Sha256 = Sha256.Parse(new string('b', 64)), Size = 512, SizeOnDisk = 512 },
            new() { Id = 2, Sha256 = Cue.Sha256, Size = Cue.Size, SizeOnDisk = Cue.Size });
        await db.SaveChangesAsync();
    }
    public Task DisposeAsync() => database.DisposeAsync().AsTask();

    [Fact]
    public async Task Publish_DatMismatch_RetainsExactDescriptorWithoutInventingAnOriginalRom()
    {
        await using var db = database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var graph = await GraphAsync(db, Decoder, preferred: true, ObservedContainer(canonical: false));
        await RawPublishAsync(db, graph.Id, ExpectedCue, 1);
        await transaction.CommitAsync();

        await using var observer = database.CreateContext();
        (await CountAsync(observer, "CatalogCdLayoutBindings")).ShouldBe(1);
        (await CountAsync(observer, "Roms")).ShouldBe(1);
        (await CountAsync(observer, "Verifications")).ShouldBe(1);
        var retained = (await new MediaFactReader(observer).ReadAsync([graph.Id]))[graph.Id];
        retained.Descriptors.Single().Sha256.ShouldBe(Cue.Sha256);
        retained.Manifest.ShouldBeOfType<RomReconstructionManifest.CdTracks>().Members[1].Identity.Sha1.ShouldNotBe(ExpectedCue.Sha1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Publish_DatExactGeneratedCueWithAnOwnedOriginal_BindsLayoutWithoutClaimingEncodingCreation(bool canonical, bool withdrawOriginal)
    {
        await using var db = database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var graph = await GraphAsync(db, Decoder, preferred: true, ObservedContainer(canonical));
        await db.CatalogReleaseFiles.Where(row => row.Id == 2).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.Size, Cue.Size).SetProperty(row => row.Sha1, Cue.Sha1)
            .SetProperty(row => row.Md5, Cue.Md5).SetProperty(row => row.Crc, Cue.Crc32));
        var originalProof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), Cue.Sha256, Cue.Size, Cue,
            RomContainerFacts.Identity.Instance, new RomDecoderIdentity.Managed("fixture-hasher"), "identity/v1", "fixture-verifier",
            Observed, null, null);
        var original = await db.CreatePreferredMeasuredWholeAsync("disc.cue", 2, RomContainerFacts.Identity.Instance,
            originalProof, Guid.Empty, Observed, CancellationToken.None);

        await RawPublishAsync(db, graph.Id, Cue, 1);
        if (withdrawOriginal)
            await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM romd."Roms" WHERE "Id" = {original.RomId}""");

        (await new DerivedDescriptorReader(db).ReadForReleaseAsync(1, CancellationToken.None)).ShouldBeEmpty();
        (await db.Database.SqlQuery<int>($"""SELECT "RepresentationId" AS "Value" FROM romd."CurrentCatalogCdLayoutBindings" WHERE "CatalogFileId"=2""").SingleAsync()).ShouldBe(graph.Id);
        (await CountAsync(db, "Roms")).ShouldBe(withdrawOriginal ? 1 : 2);
        (await CountAsync(db, "CatalogCdLayoutBindings")).ShouldBe(1);
        (await db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM romd."RepresentationCreations" """).SingleAsync()).ShouldBe(0);
        await transaction.CommitAsync();
        await using var observer = database.CreateContext();
        var retained = (await new MediaFactReader(observer).ReadAsync([graph.Id]))[graph.Id];
        retained.Verifications.Single().Decoder.ShouldBe(new RomDecoderIdentity.Native(Decoder));
        retained.Descriptors.Single().Sha256.ShouldBe(Cue.Sha256);

        var subject = new MediaReconciliationSubject.CatalogCdLayout(2);
        var facts = (await CatalogSnapshots(CatalogComposition(ConversionMode.Automatic), Substitute.For<IRomCdDescriptorIdentityReader>()).ReadAsync(subject)).Value;
        if (withdrawOriginal)
        {
            facts.ShouldBeOfType<MediaReconciliationFacts.None>().Code.ShouldBe("catalog-original-withdrawn");
            var directive = CatalogDecision(CatalogComposition(ConversionMode.Automatic), canEncode: true).Decide(Guid.NewGuid(), new(subject, 1, true, false), facts).Value;
            directive.ShouldBeOfType<MediaReconciliationDirective.Settle>().Outcome
                .ShouldBe(MediaReconciliationOutcomeKind.Superseded);
            await using (var restore = await observer.Database.BeginTransactionAsync())
            {
                var restoredProof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), Cue.Sha256, Cue.Size, Cue,
                    RomContainerFacts.Identity.Instance, new RomDecoderIdentity.Managed("fixture-hasher/restored"), "identity/v1",
                    "fixture-verifier/restored", Observed.AddSeconds(1), null, null);
                var restored = await observer.CreatePreferredMeasuredWholeAsync("disc.cue", 2, RomContainerFacts.Identity.Instance,
                    restoredProof, Guid.Empty, Observed.AddSeconds(1), CancellationToken.None);
                restored.RomId.ShouldNotBe(original.RomId);
                await restore.CommitAsync();
            }
            var restoredFacts = (await CatalogSnapshots(CatalogComposition(ConversionMode.Automatic), Substitute.For<IRomCdDescriptorIdentityReader>()).ReadAsync(subject)).Value;
            restoredFacts.ShouldBeOfType<MediaReconciliationFacts.Layout>().CurrentArchiveId.ShouldBe(graph.Id);
            var retried = CatalogDecision(CatalogComposition(ConversionMode.Automatic), canEncode: true).Decide(Guid.NewGuid(), new(subject, 2, true, false), restoredFacts).Value;
            if (canonical)
                retried.ShouldBeOfType<MediaReconciliationDirective.Settle>().Code.ShouldBe("Canonical");
            else
                retried.ShouldBeOfType<MediaReconciliationDirective.Encode>();
            (await new MediaFactReader(observer).ReadAsync([graph.Id]))[graph.Id].Verifications.ShouldHaveSingleItem();
        }
        else
            facts.ShouldBeOfType<MediaReconciliationFacts.Layout>();
    }

    [Theory]
    [InlineData("release-fingerprint")]
    [InlineData("requirement-fingerprint")]
    [InlineData("cue-digest")]
    [InlineData("cue-is-original")]
    [InlineData("bin-digest")]
    [InlineData("bin-name")]
    [InlineData("extra-sidecar")]
    [InlineData("title-conflict")]
    [InlineData("disk-requirement")]
    [InlineData("withdrawn-bin")]
    [InlineData("wrong-member")]
    public async Task Publish_ContradictoryCatalogOrOwnership_DirectSqlRefuses(string mutation)
    {
        await using var db = database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var graph = await GraphAsync(db, Decoder, preferred: false, ObservedContainer(canonical: false));
        if (mutation == "cue-is-original")
            await db.CatalogReleaseFiles.Where(row => row.Id == 2).ExecuteUpdateAsync(set => set
                .SetProperty(row => row.Size, Cue.Size).SetProperty(row => row.Sha1, Cue.Sha1)
                .SetProperty(row => row.Md5, Cue.Md5).SetProperty(row => row.Crc, Cue.Crc32));
        else if (mutation != "wrong-member")
            await MutateAsync(db, mutation);

        var error = await Should.ThrowAsync<PostgresException>(() => RawPublishAsync(db, graph.Id,
            mutation == "cue-is-original" ? Cue : ExpectedCue, mutation == "wrong-member" ? 0 : 1));

        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        await transaction.RollbackAsync();
        await using var observer = database.CreateContext();
        (await CountAsync(observer, "CatalogCdLayoutBindings")).ShouldBe(0);
        (await CountAsync(observer, "Verifications")).ShouldBe(0);
    }

    [Fact]
    public async Task Publish_CallerRollsBack_RemovesBindingGraphAndProofTogether()
    {
        await using (var db = database.CreateContext())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var graph = await GraphAsync(db, Decoder, preferred: true, ObservedContainer(canonical: false));
            var published = await new CatalogCdLayoutStore(db).PublishAsync(Publication(graph.Id, Guid.NewGuid()), CancellationToken.None);
            published.IsError.ShouldBeFalse();
            await using var outsider = database.CreateContext();
            (await CountAsync(outsider, "CatalogCdLayoutBindings")).ShouldBe(0);
            (await CountAsync(outsider, "Roms")).ShouldBe(0);
            await transaction.RollbackAsync();
        }
        await using var observer = database.CreateContext();
        (await CountAsync(observer, "CatalogCdLayoutBindings")).ShouldBe(0);
        (await CountAsync(observer, "Roms")).ShouldBe(0);
        (await CountAsync(observer, "Verifications")).ShouldBe(0);
    }

    [Fact]
    public async Task Publish_OutsideCallerTransaction_RefusesBeforeWriting()
    {
        await using var db = database.CreateContext();
        await Should.ThrowAsync<InvalidOperationException>(() => new CatalogCdLayoutStore(db)
            .PublishAsync(Publication(1, Guid.NewGuid()), CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => new DerivedDescriptorReader(db).ReadForReleaseAsync(1, CancellationToken.None));
        (await CountAsync(db, "CatalogCdLayoutBindings")).ShouldBe(0);
    }

    [Fact]
    public async Task Publish_RepeatedAndDifferentGenerator_PreservesFirstRevisionAndRequiresExplicitReplacement()
    {
        var (graph, id, revision) = await RetainAsync(preferred: true, ObservedContainer(canonical: false));
        await using var db = database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var store = new CatalogCdLayoutStore(db);
        var repeated = await store.PublishAsync(Publication(graph.Id, Guid.NewGuid()), CancellationToken.None);
        repeated.IsError.ShouldBeFalse();
        repeated.Value.ShouldBe(id);
        var conflict = await store.PublishAsync(Publication(graph.Id, Guid.NewGuid()) with { GeneratorProfile = "another-generator/v1" }, CancellationToken.None);
        conflict.IsError.ShouldBeTrue();
        conflict.FirstError.Code.ShouldBe("CatalogCdLayout.BindingChanged");
        var current = (await new DerivedDescriptorReader(db).ReadForReleaseAsync(1, CancellationToken.None)).Single();
        current.Revision.ShouldBe(revision);
        current.Identity.ShouldBe(Cue);
        current.RelativePath.ShouldBe("disc.cue");
        await transaction.CommitAsync();
    }

    [Theory]
    [InlineData("release-fingerprint")]
    [InlineData("requirement-fingerprint")]
    [InlineData("cue-digest")]
    [InlineData("bin-digest")]
    [InlineData("bin-name")]
    [InlineData("extra-sidecar")]
    [InlineData("title-conflict")]
    [InlineData("disk-requirement")]
    public async Task Read_CurrentCatalogChanged_DoesNotServeStaleDerivedBytes(string mutation)
    {
        await RetainAsync(preferred: true, ObservedContainer(canonical: false));
        await using var db = database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        (await new DerivedDescriptorReader(db).ReadForReleaseAsync(1, CancellationToken.None)).Count.ShouldBe(1);
        await MutateAsync(db, mutation);

        (await new DerivedDescriptorReader(db).ReadForReleaseAsync(1, CancellationToken.None)).ShouldBeEmpty();
        (await CountAsync(db, "CatalogCdLayoutBindings")).ShouldBe(1);
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task Read_DatExactOriginalArrives_ExplicitlyPrefersTheVerifiedOriginal()
    {
        await RetainAsync(preferred: true, ObservedContainer(canonical: false));
        await using var db = database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Files.Add(new() { Id = 3, Sha256 = ExpectedCue.Sha256, Size = ExpectedCue.Size, SizeOnDisk = ExpectedCue.Size });
        await db.SaveChangesAsync();
        var proof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), ExpectedCue.Sha256, ExpectedCue.Size, ExpectedCue,
            RomContainerFacts.Identity.Instance, new RomDecoderIdentity.Managed("fixture-hasher"), "identity/v1", "fixture-verifier",
            Observed.AddSeconds(1), null, null);
        var original = await db.CreatePreferredMeasuredWholeAsync("disc.cue", 3, RomContainerFacts.Identity.Instance, proof, Guid.Empty, Observed, CancellationToken.None);

        (await new DerivedDescriptorReader(db).ReadForReleaseAsync(1, CancellationToken.None)).ShouldBeEmpty();
        (await CountAsync(db, "Roms")).ShouldBe(2);
        original.Graph.Outputs.Single().Identity.ShouldBe(ExpectedCue);
        (await CountAsync(db, "CatalogCdLayoutBindings")).ShouldBe(1); // Retained history is not rewritten into an original claim.
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task Withdraw_LastOwnerAfterLogicalWithdrawal_RetainsEveryBuildUntilExactBindingReleaseThenCollects()
    {
        var (graph, id, revision) = await RetainAsync(preferred: true, ObservedContainer(canonical: false));
        var nextDecoder = new RomChdDecoderBuild(Decoder.Rid, Sha1.Parse(new string('e', 40)), 3);
        await using (var db = database.CreateContext())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var earlier = graph.Verifications.Single();
            var second = new RomRepresentationVerification(Guid.NewGuid(), graph.ArtifactSha256, graph.ArtifactSize,
                new RomDecoderIdentity.Native(nextDecoder), earlier.CheckedProfile, "fixture-verifier/new", Observed.AddSeconds(1),
                earlier.Outputs, graph.Container, earlier.ComputedRawSha1, earlier.ComputedOverallSha1);
            (await new MediaFactPublisher(db).AppendVerificationAsync(graph.Id, second)).ShouldBeTrue();
            await db.Database.ExecuteSqlRawAsync("""DELETE FROM romd."Roms" """);
            await transaction.CommitAsync();
        }
        await using var observer = database.CreateContext();
        (await new MediaRepresentationCleanup(observer).CollectAsync(8)).ShouldBe(0);
        var retained = (await new MediaFactReader(observer).ReadAsync([graph.Id]))[graph.Id];
        retained.Verifications.Count.ShouldBe(2);
        retained.Verifications.Select(proof => proof.Decoder).ShouldContain(new RomDecoderIdentity.Native(Decoder));
        retained.Verifications.Select(proof => proof.Decoder).ShouldContain(new RomDecoderIdentity.Native(nextDecoder));
        retained.Descriptors.Single().FileId.ShouldBe(2);
        await using (var transaction = await observer.Database.BeginTransactionAsync())
        {
            var store = new CatalogCdLayoutStore(observer);
            (await store.WithdrawAsync(id, Guid.NewGuid(), CancellationToken.None)).ShouldBeFalse();
            (await store.WithdrawAsync(id, revision, CancellationToken.None)).ShouldBeTrue();
            await transaction.CommitAsync();
        }
        (await new MediaRepresentationCleanup(observer).CollectAsync(8)).ShouldBe(1);
        (await CountAsync(observer, "Verifications")).ShouldBe(0);
        (await observer.Files.CountAsync()).ShouldBe(2); // Ledger/CAS collection remains the storage service's responsibility.
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("descriptor")]
    [InlineData("representation")]
    public async Task Mutate_RetainedBindingOrDependencies_DirectSqlRefuses(string target)
    {
        var (graph, _, _) = await RetainAsync(preferred: true, ObservedContainer(canonical: false));
        await using var db = database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var error = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(target switch
        {
            "binding" => $"UPDATE romd.\"CatalogCdLayoutBindings\" SET \"GeneratorProfile\" = 'rewritten' WHERE \"RepresentationId\" = {graph.Id}",
            "descriptor" => $"UPDATE romd.\"Files\" SET \"Size\" = 1 WHERE \"Id\" = {2}",
            _ => $"DELETE FROM romd.\"Representations\" WHERE \"Id\" = {graph.Id}"
        }));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Publish_ConcurrentSameRequirement_SerializesAndKeepsTheFirstRevision()
    {
        RomRepresentationFacts graph;
        await using (var setup = database.CreateContext())
        await using (var transaction = await setup.Database.BeginTransactionAsync())
        {
            graph = await GraphAsync(setup, Decoder, preferred: true, ObservedContainer(canonical: false));
            await transaction.CommitAsync();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        await using var firstTransaction = await first.Database.BeginTransactionAsync(ct);
        await using var secondTransaction = await second.Database.BeginTransactionAsync(ct);
        Guid revision = Guid.NewGuid();
        var accepted = await new CatalogCdLayoutStore(first).PublishAsync(Publication(graph.Id, revision), ct);
        accepted.IsError.ShouldBeFalse();
        int pid = await second.OpenBackendProcessIdAsync(ct);
        var pending = new CatalogCdLayoutStore(second).PublishAsync(Publication(graph.Id, Guid.NewGuid()), ct);
        try
        {
            (await database.WaitsForALockAsync(pid, pending, ct)).ShouldBeTrue();
            await firstTransaction.CommitAsync(ct);
            var repeated = await pending;
            repeated.IsError.ShouldBeFalse();
            repeated.Value.ShouldBe(accepted.Value);
            (await new DerivedDescriptorReader(second).ReadForReleaseAsync(1, ct)).Single().Revision.ShouldBe(revision);
            await secondTransaction.CommitAsync(ct);
        }
        finally
        {
            await firstTransaction.DisposeAsync();
            try
            { await pending; }
            catch (Exception) { /* Observe the pending operation during cleanup. */ }
        }
    }

    [Fact]
    public async Task Delete_CatalogRequirement_RemovesOnlyItsBindingWithoutRewritingRetainedFacts()
    {
        var (graph, _, _) = await RetainAsync(preferred: true, ObservedContainer(canonical: false));
        await using var db = database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await CatalogTopologyFence.AcquireAsync(db, CancellationToken.None);
        await db.CatalogReleaseFiles.Where(file => file.Id == 2).ExecuteDeleteAsync();
        await transaction.CommitAsync();
        (await CountAsync(db, "CatalogCdLayoutBindings")).ShouldBe(0);
        (await CountAsync(db, "Verifications")).ShouldBe(1);
        (await new MediaFactReader(db).ReadAsync([graph.Id])).ShouldContainKey(graph.Id);
    }

    [Fact]
    public async Task Publish_CapturedRequirementChanged_ReturnsConflictWithoutAbortingTheCallerTransaction()
    {
        await using var db = database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var graph = await GraphAsync(db, Decoder, preferred: true, ObservedContainer(canonical: false));
        await MutateAsync(db, "cue-digest");
        var result = await new CatalogCdLayoutStore(db).PublishAsync(Publication(graph.Id, Guid.NewGuid()), CancellationToken.None);
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("CatalogCdLayout.CatalogChanged");
        (await CountAsync(db, "CatalogCdLayoutBindings")).ShouldBe(0);
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task Publish_NewCatalogSnapshotWithSameGraph_RequiresWithdrawalInsteadOfReportingAStaleBindingAsSuccess()
    {
        var (graph, _, _) = await RetainAsync(preferred: true, ObservedContainer(canonical: false));
        await using var db = database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await MutateAsync(db, "requirement-fingerprint");
        var publication = Publication(graph.Id, Guid.NewGuid());
        publication = publication with { Requirement = publication.Requirement with { FileFingerprint = "different" } };
        var store = new CatalogCdLayoutStore(db);
        (await store.LockScopeAsync(publication.Requirement, CancellationToken.None)).ShouldBeTrue();

        var result = await store.PublishAsync(publication, CancellationToken.None);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("CatalogCdLayout.BindingChanged");
        (await new DerivedDescriptorReader(db).ReadForReleaseAsync(1, CancellationToken.None)).ShouldBeEmpty();
        (await CountAsync(db, "CatalogCdLayoutBindings")).ShouldBe(1);
        await transaction.CommitAsync();
    }

    private async Task<(RomRepresentationFacts Graph, int Id, Guid Revision)> RetainAsync(bool preferred, RomContainerFacts.Chd container)
    {
        await using var db = database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var graph = await GraphAsync(db, Decoder, preferred, container);
        Guid revision = Guid.NewGuid();
        var result = await new CatalogCdLayoutStore(db).PublishAsync(Publication(graph.Id, revision), CancellationToken.None);
        result.IsError.ShouldBeFalse();
        await transaction.CommitAsync();
        return (graph, result.Value, revision);
    }
    private static CatalogCdLayoutPublication Publication(int representation, Guid revision) => new(revision,
        new(2, 1, "sha1:fixture", "cue", "disc.cue", ExpectedCue.Size, ExpectedCue.Sha1, ExpectedCue.Md5, ExpectedCue.Crc32),
        representation, 1, "fixture-generator/v1", Observed);
    private static Task MutateAsync(RomdDbContext db, string mutation) => db.Database.ExecuteSqlRawAsync(mutation switch
    {
        "release-fingerprint" => """UPDATE romd."CatalogReleases" SET "Fingerprint" = 'different' WHERE "Id" = 1""",
        "requirement-fingerprint" => """UPDATE romd."CatalogReleaseFiles" SET "FileFingerprint" = 'different' WHERE "Id" = 2""",
        "cue-digest" => """UPDATE romd."CatalogReleaseFiles" SET "Md5" = decode(repeat('ee',16),'hex') WHERE "Id" = 2""",
        "bin-digest" => """UPDATE romd."CatalogReleaseFiles" SET "Md5" = decode(repeat('ee',16),'hex') WHERE "Id" = 1""",
        "bin-name" => """UPDATE romd."CatalogReleaseFiles" SET "Name" = 'renamed.bin' WHERE "Id" = 1""",
        "extra-sidecar" => """INSERT INTO romd."CatalogReleaseFiles" ("CatalogReleaseId","FileFingerprint","Name","Size","IsDisk") VALUES (1,'sidecar','disc.sbi',4,false)""",
        "title-conflict" => """UPDATE romd."CatalogReleases" SET "HasTitleConflict" = true WHERE "Id" = 1""",
        "disk-requirement" => """UPDATE romd."CatalogReleaseFiles" SET "IsDisk" = true WHERE "Id" = 2""",
        "withdrawn-bin" => """UPDATE romd."RepresentationOutputs" SET "RomId" = NULL""",
        _ => throw new ArgumentException(nameof(mutation))
    });

    private static async Task<RomRepresentationFacts> GraphAsync(RomdDbContext db, RomChdDecoderBuild decoder, bool preferred, RomContainerFacts.Chd container)
    {
        await CatalogTopologyFence.AcquireAsync(db, CancellationToken.None);
        var publisher = new MediaFactPublisher(db);
        int rom = await publisher.CreateLogicalAsync("track.bin", Bin, Guid.Empty, Observed);
        var manifest = new RomReconstructionManifest.CdTracks(64, [new(1, 0, 64, RomCdTrackKind.Mode2Raw)],
            [new RomCdBinMember("track.bin", Cd(Bin), Cd(Bin), [1]), new RomCdStoredMember("disc.cue", Cd(Cue), RomCdStoredRole.Cue)]);
        var proof = new RomRepresentationVerification(Guid.NewGuid(), Sha256.Parse(new string('b', 64)), 512,
            new RomDecoderIdentity.Native(decoder), "fixture-inspection", "fixture-verifier", Observed,
            [new(new RomOutputSelector.Member(0), Bin)], container, container.StoredRawSha1, container.StoredOverallSha1);
        return await publisher.PublishAsync(1, container, manifest, proof, [new(new RomOutputSelector.Member(0), rom, preferred)], [new(1, 2, Cue.Sha256, Cue.Size)]);
    }
    private static RomContainerFacts.Chd ObservedContainer(bool canonical) => MediaFactFixtures.Chd(64 * 2448, canonical ? 39168u : 4 * 2448u, 2448,
        canonical ? [0x63647a73u, 0x6364666cu, 0, 0] : [0, 0, 0, 0], Sha1.Parse(new string('c', 40)), Sha1.Parse(new string('d', 40)),
        [new(0x43485432, 1, "TRACK:1 TYPE:MODE2_RAW SUBTYPE:NONE FRAMES:64 PREGAP:0 PGTYPE:MODE2_RAW PGSUB:NONE POSTGAP:0"u8)]);
    private static RomCdFileIdentity Cd(RomLogicalIdentity identity) => new(identity.Size, identity.Sha256, identity.Sha1, identity.Md5, identity.Crc32);
    private static Task RawPublishAsync(RomdDbContext db, int representation, RomLogicalIdentity expected, int ordinal) => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO romd."CatalogCdLayoutBindings" ("Revision","CatalogFileId","CatalogReleaseId","CatalogFingerprint","RequirementFingerprint","Path",
            "ExpectedSize","ExpectedSha1","ExpectedMd5","ExpectedCrc32","RepresentationId","MemberOrdinal","GeneratorProfile","CreatedAtUtcTicks")
        VALUES ({Guid.NewGuid()},2,1,'sha1:fixture','cue','disc.cue',{expected.Size},{expected.Sha1.ToArray()},
            {expected.Md5.ToArray()},{expected.Crc32.ToArray()},{representation},{ordinal},'fixture-generator/v1',{Observed.UtcTicks})
        """);
    private static Task<int> CountAsync(RomdDbContext db, string table) => db.Database.SqlQueryRaw<int>(table switch
    {
        "CatalogCdLayoutBindings" => """SELECT count(*)::int AS "Value" FROM romd."CatalogCdLayoutBindings" """,
        "Roms" => """SELECT count(*)::int AS "Value" FROM romd."Roms" """,
        "Verifications" => """SELECT count(*)::int AS "Value" FROM romd."Verifications" """,
        "MediaReconciliationAttempts" => """SELECT count(*)::int AS "Value" FROM romd."MediaReconciliationAttempts" """,
        _ => throw new ArgumentException(nameof(table))
    }).SingleAsync();
}
