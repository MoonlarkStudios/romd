using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Persistence;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Npgsql;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Romd.Persistence.Repositories.Reconciliation;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class CatalogCdLayoutBoundaryTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task Publish_CatalogEncoding_RebindsTheNaturalScopeWithPreferencesAndJournalAtomically(bool commit, bool sharedCatalogOwner, bool foreignOriginalBacking)
    {
        var (source, bindingId, revision) = await RetainAsync(preferred: true, ObservedContainer(canonical: false));
        await using var db = database.CreateContext();
        var native = source.Verifications.Single();
        var retainedBuild = new RomChdDecoderBuild(Decoder.Rid, Decoder.UpstreamCommit, Decoder.RecipeVersion + 1);
        var anotherBuild = new RomRepresentationVerification(Guid.NewGuid(), source.ArtifactSha256, source.ArtifactSize, new RomDecoderIdentity.Native(retainedBuild),
            native.CheckedProfile, "earlier-native-verifier", Observed.AddTicks(1), native.Outputs, source.Container, native.ComputedRawSha1, native.ComputedOverallSha1);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            (await new MediaFactPublisher(db).AppendVerificationAsync(source.Id, anotherBuild)).ShouldBeTrue();
            await transaction.CommitAsync();
        }
        if (sharedCatalogOwner)
        {
            await using var shared = await db.Database.BeginTransactionAsync();
            await StageAdditionalCatalogBindingAsync(db, source.Id);
            await shared.CommitAsync();
        }
        int? foreignLayoutId = null;
        if (foreignOriginalBacking)
        {
            await using var originals = await db.Database.BeginTransactionAsync();
            var originalCue = MediaFactFixtures.Identity("FILE \"disc.bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00\r\n"u8);
            db.Files.Add(new() { Id = 4, Sha256 = originalCue.Sha256, Size = originalCue.Size, SizeOnDisk = originalCue.Size });
            await db.SaveChangesAsync();
            var originalProof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), originalCue.Sha256, originalCue.Size, originalCue,
                RomContainerFacts.Identity.Instance, new RomDecoderIdentity.Managed("foreign-original-hasher/v1"), "identity/v1",
                "foreign-original-verifier", Observed, null, null);
            await db.CreatePreferredMeasuredWholeAsync("foreign.cue", 4, RomContainerFacts.Identity.Instance, originalProof, Guid.Empty, Observed, default);
            var originalLayout = new OriginalDiscLayoutEntity
            {
                SourceJobId = Guid.NewGuid(),
                SourceRelativePath = "foreign.cue",
                EntryPoint = "foreign.cue",
                DescriptorFileId = 4,
                DependencyPathsJson = "[\"disc.bin\"]",
                State = "pending",
                CreatedAt = Observed,
                Files = [new() { Path = "disc.bin", RomId = source.Outputs.Single().RomId!.Value, FileId = source.FileId, BindingRevision = Guid.Empty }]
            };
            db.OriginalDiscLayouts.Add(originalLayout);
            await db.SaveChangesAsync();
            foreignLayoutId = originalLayout.Id;
            await originals.CommitAsync();
        }
        var container = ObservedContainer(canonical: true);
        db.Files.Add(new() { Id = 3, Sha256 = MediaFactGraphFixture.Hash('e'), Size = 512, SizeOnDisk = 512 });
        await db.SaveChangesAsync();
        var targetProof = new RomRepresentationVerification(Guid.NewGuid(), MediaFactGraphFixture.Hash('e'), 512, new RomDecoderIdentity.Native(Decoder),
            native.CheckedProfile, "independent-target-verifier", Observed.AddTicks(2), native.Outputs, container, container.StoredRawSha1, container.StoredOverallSha1);
        var measured = new RomMeasuredRepresentation(container, source.Manifest, targetProof);
        var artifact = Substitute.For<ITempFile>();
        artifact.FileSha256.Returns(targetProof.ArtifactSha256);
        artifact.FileSize.Returns(targetProof.ArtifactSize);
        var descriptor = Substitute.For<ITempFile>();
        descriptor.FileSha256.Returns(Cue.Sha256);
        descriptor.FileSize.Returns(Cue.Size);
        var encoded = new MediaReconciliationEncoded.Cd(artifact, new(artifact.FileSha256, Md5.Parse(new string('e', 32)), Sha1.Parse(new string('e', 40)), artifact.FileSize), measured, new(measured, CatalogComposition(ConversionMode.Automatic).Reconciliation["ps1-cd-set"].Recipe,
            CatalogEncoder, MediaFactGraphFixture.Hash('f'), 128, "actual-source-observer", MediaFactGraphFixture.Boot),
            new Dictionary<string, ITempFile> { ["disc.cue"] = descriptor }, Observed.AddTicks(3));
        var claim = await ClaimCatalogAsync(db, conversion: true);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var decision = CatalogDecision(CatalogComposition(ConversionMode.Automatic), canEncode: true);
        var execution = CatalogExecution(db, CatalogComposition(ConversionMode.Automatic), decision);
        var demand = (await execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
        var before = (await CatalogSnapshots(CatalogComposition(ConversionMode.Automatic), Substitute.For<IRomCdDescriptorIdentityReader>()).ReadAsync(demand.Subject)).Value;
        var directive = decision.Decide(claim.Job.Id, demand, before).Value.ShouldBeOfType<MediaReconciliationDirective.Encode>();
        var captured = await execution.CaptureAsync(directive.Capture);
        captured.IsError.ShouldBeFalse();
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            (await execution.PrepareAsync(captured.Value, [targetProof.ArtifactSha256, Cue.Sha256])).IsError.ShouldBeFalse();
            (await execution.StageEncodedAsync(captured.Value, encoded, 3, source.Descriptors)).IsError.ShouldBeFalse();
            await using var outside = database.CreateContext();
            (await outside.Database.SqlQuery<Guid>($"""SELECT "Revision" AS "Value" FROM romd."CatalogCdLayoutBindings" WHERE "CatalogFileId"=2""").SingleAsync()).ShouldBe(revision);
            (await outside.RomFiles.SingleAsync(row => row.Id == source.Outputs.Single().RomId!.Value)).PreferredOutputId.ShouldBe(source.Outputs.Single().Id);
            (await outside.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(2);
            (await new MediaFactReader(outside).ReadAsync([source.Id]))[source.Id].Verifications.Count.ShouldBe(2);
            // GC runs on another backend while the replacement has removed its old owners only in the uncommitted transaction.
            await using (var cleanup = await outside.Database.BeginTransactionAsync())
            {
                (await outside.Database.SqlQuery<bool>($"""SELECT pg_try_advisory_xact_lock(9415,{source.Id}) AS "Value" """).SingleAsync()).ShouldBeFalse();
                (await outside.Database.SqlQuery<bool>($"""SELECT romd.delete_unowned_media_representation({source.Id}) AS "Value" """).SingleAsync()).ShouldBeFalse();
                await cleanup.CommitAsync();
            }
            (await new MediaFactReader(outside).ReadAsync([source.Id]))[source.Id].Verifications.Count.ShouldBe(2);
            (await execution.ReadAttemptOutcomeAsync(claim.Job.Id, captured.Value.Number)).ShouldBeNull();
            if (commit)
                await transaction.CommitAsync();
            else
                await transaction.RollbackAsync();
        }
        await using var observer = database.CreateContext();
        var binding = await observer.Database.SqlQuery<BoundCatalog>($"""SELECT "Id","Revision","RepresentationId" FROM romd."CatalogCdLayoutBindings" WHERE "CatalogFileId"=2""").SingleAsync();
        (await observer.CdArchiveOwners.CountAsync()).ShouldBe(0);
        (await observer.OriginalDiscLayouts.CountAsync()).ShouldBe(foreignOriginalBacking ? 1 : 0);
        if (foreignLayoutId is { } retainedLayout)
        {
            var dependency = await observer.OriginalDiscLayoutFiles.SingleAsync(row => row.OriginalDiscLayoutId == retainedLayout);
            dependency.FileId.ShouldBe(source.FileId);
            dependency.RomId.ShouldBe(source.Outputs.Single().RomId!.Value);
        }
        (await observer.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(commit ? 0 : 2);
        var journal = await execution.ReadAttemptOutcomeAsync(claim.Job.Id, captured.Value.Number);
        if (commit)
        {
            binding.Id.ShouldNotBe(bindingId);
            binding.Revision.ShouldNotBe(revision);
            var graph = (await new MediaFactReader(observer).ReadAsync([binding.RepresentationId]))[binding.RepresentationId];
            graph.Creations.ShouldHaveSingleItem().Id.ShouldBe(encoded.Observation.Id);
            graph.Creations.Single().Sources.Single().RepresentationId.ShouldBe(source.Id);
            graph.Creations.Single().Sources.Single().VerificationId.ShouldBe(native.Id);
            graph.CdBootInspections.Single().Boot.ShouldBe(MediaFactGraphFixture.Boot);
            (await observer.RomFiles.SingleAsync(row => row.Id == source.Outputs.Single().RomId!.Value)).PreferredOutputId.ShouldBe(graph.Outputs.Single().Id);
            journal.ShouldNotBeNull().CreationId.ShouldBe(encoded.Observation.Id);
            var next = (await CatalogSnapshots(CatalogComposition(ConversionMode.Automatic), Substitute.For<IRomCdDescriptorIdentityReader>()).ReadAsync(demand.Subject)).Value;
            CatalogDecision(CatalogComposition(ConversionMode.Automatic), canEncode: false).Decide(claim.Job.Id, demand, next).Value.ShouldBeOfType<MediaReconciliationDirective.Settle>().Code.ShouldBe("Canonical");
            await using var cleanup = await observer.Database.BeginTransactionAsync();
            (await observer.Database.SqlQuery<bool>($"""SELECT romd.delete_unowned_media_representation({source.Id}) AS "Value" """).SingleAsync()).ShouldBe(!sharedCatalogOwner && !foreignOriginalBacking);
            await cleanup.CommitAsync();
            if (sharedCatalogOwner || foreignOriginalBacking)
            {
                var held = (await new MediaFactReader(observer).ReadAsync([source.Id]))[source.Id];
                held.Verifications.Count.ShouldBe(2);
                held.Outputs.Single().RomId.ShouldBe(source.Outputs.Single().RomId);
                if (sharedCatalogOwner)
                    (await observer.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM romd."CurrentCatalogCdLayoutBindings" WHERE "CatalogFileId"=17""").SingleAsync()).ShouldBe(1);
            }
            else
                (await new MediaFactReader(observer).ReadAsync([source.Id])).ShouldBeEmpty();
            (await new MediaFactReader(observer).ReadAsync([graph.Id]))[graph.Id].Creations.Single().Sources.Single().VerificationId.ShouldBe(native.Id);
        }
        else
        {
            binding.Id.ShouldBe(bindingId);
            binding.Revision.ShouldBe(revision);
            binding.RepresentationId.ShouldBe(source.Id);
            (await observer.RomFiles.SingleAsync(row => row.Id == source.Outputs.Single().RomId!.Value)).PreferredOutputId.ShouldBe(source.Outputs.Single().Id);
            (await observer.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM romd."RepresentationCreations" """).SingleAsync()).ShouldBe(0);
            journal.ShouldBeNull();
            await using var cleanup = await observer.Database.BeginTransactionAsync();
            (await observer.Database.SqlQuery<bool>($"""SELECT romd.delete_unowned_media_representation({source.Id}) AS "Value" """).SingleAsync()).ShouldBeFalse();
            await cleanup.CommitAsync();
            (await new MediaFactReader(observer).ReadAsync([source.Id]))[source.Id].Verifications.Count.ShouldBe(2);
        }
    }

    [Theory]
    [InlineData("bin-path")]
    [InlineData("cue-path")]
    [InlineData("cue-md5")]
    public async Task Capture_DirectSqlForeignCatalogMember_RefusesBeforeTheDuplicateInputConstraint(string mutation)
    {
        await RetainAsync(preferred: true, ObservedContainer(canonical: false));
        await using var db = database.CreateContext();
        var claim = await ClaimCatalogAsync(db, conversion: true);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var decision = CatalogDecision(CatalogComposition(ConversionMode.Automatic), canEncode: true);
        var execution = CatalogExecution(db, CatalogComposition(ConversionMode.Automatic), decision);
        var demand = (await execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
        var facts = (await CatalogSnapshots(CatalogComposition(ConversionMode.Automatic), Substitute.For<IRomCdDescriptorIdentityReader>()).ReadAsync(demand.Subject)).Value;
        var captured = await execution.CaptureAsync(decision.Decide(claim.Job.Id, demand, facts).Value.ShouldBeOfType<MediaReconciliationDirective.Encode>().Capture);
        captured.IsError.ShouldBeFalse();
        string role = mutation == "bin-path" ? "Bin" : "Cue", field = mutation == "cue-md5" ? "Md5" : "Path";
        string value = mutation == "cue-md5" ? "\\x" + new string('e', 32) : "foreign." + (role == "Bin" ? "bin" : "cue");
        await using var transaction = await db.Database.BeginTransactionAsync();
        var error = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."MediaReconciliationInputs"
            SELECT (jsonb_populate_record(NULL::romd."MediaReconciliationInputs",to_jsonb(source)||jsonb_build_object({field},{value}))).*
            FROM romd."MediaReconciliationInputs" source WHERE source."JobId"={claim.Job.Id} AND source."Role"={role}
            """));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.ShouldBe("media_reconciliation_source_changed");
        await transaction.RollbackAsync();
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(2);
        (await execution.ReadAttemptOutcomeAsync(claim.Job.Id, captured.Value.Number)).ShouldBeNull();
    }
    private sealed record BoundCatalog(int Id, Guid Revision, int RepresentationId);
}
