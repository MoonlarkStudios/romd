using Microsoft.EntityFrameworkCore;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class RomContentPublicationReaderTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadLockedPublication_StagedPreferenceAndObservation_StayAtomicAndPrivateUntilCallerCompletes(bool commit)
    {
        byte[] bytes = Enumerable.Range(0, 512).Select(index => (byte)(index % 251)).ToArray();
        var identity = MediaFactFixtures.Identity(bytes);
        await using var db = _database.CreateContext();
        PublishedWholeMediaFixture original;
        await using (var seed = await db.Database.BeginTransactionAsync())
        {
            var file = new FileEntityPersistence { Sha256 = identity.Sha256, Size = identity.Size, SizeOnDisk = identity.Size };
            db.Files.Add(file);
            await db.SaveChangesAsync();
            var proof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), file.Sha256, file.Size, identity, RomContainerFacts.Identity.Instance,
                new RomDecoderIdentity.Managed("fixture-original"), "identity/v1", "fixture-verifier", DateTimeOffset.UnixEpoch, null, null);
            original = await db.CreatePreferredMeasuredWholeAsync("disc.iso", file.Id, RomContainerFacts.Identity.Instance, proof,
                Guid.Empty, DateTimeOffset.UnixEpoch, CancellationToken.None);
            await seed.CommitAsync();
        }
        RomRepresentationFacts target;
        var inspection = MediaFactFixtures.ObserveNintendo(Guid.NewGuid(), identity, bytes.AsSpan(0, 32), new(0, 0, 0),
            "raw-fixture/v1", "fixture-inspector", DateTimeOffset.UnixEpoch.AddTicks(17));
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var file = new FileEntityPersistence { Sha256 = Sha256.Parse(new string('f', 64)), Size = 128, SizeOnDisk = 128 };
            db.Files.Add(file);
            await db.SaveChangesAsync();
            var container = MediaFactFixtures.Rvz(identity.Size, 1, 5, 19, 131072, false);
            var proof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), file.Sha256, file.Size, identity, container,
                new RomDecoderIdentity.Managed("fixture-rvz"), "whole-output/v1", "fixture-verifier", DateTimeOffset.UnixEpoch, null, null);
            target = await db.StageMeasuredWholeAsync(file.Id, container, proof, new(RomOutputSelector.Whole.Instance, original.RomId, true), CancellationToken.None);
            var receipt = new Romd.Admin.Application.Source.Rom.RomRetainedMeasurement(target.Id, target.Outputs.Single().Id, target.FileId, proof.Id);
            (await MediaTestServices.CreateObservationStore(db, TimeProvider.System).StageAsync(original.RomId, receipt, inspection)).IsError.ShouldBeFalse();
            var catalog = MediaTestServices.CreateContentCatalog(db);
            var publication = (await catalog.ReadLockedPublicationAsync([original.RomId], CancellationToken.None))[original.RomId];
            publication.RepresentationId.ShouldBe(target.Id);
            publication.OutputId.ShouldBe(target.Outputs.Single().Id);
            publication.Preferred.Facts.Verifications.Single().Id.ShouldBe(proof.Id);
            publication.Inspections.Single().SameObservation(inspection).ShouldBeTrue();
            (await MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(original.RomId)).ShouldNotBeNull().RepresentationId.ShouldBe(original.Graph.Id);
            await using var outside = _database.CreateContext();
            var visible = (await MediaTestServices.CreateContentCatalog(outside).FindByRomIdAsync(original.RomId)).ShouldNotBeNull();
            visible.RepresentationId.ShouldBe(original.Graph.Id);
            visible.Inspections.ShouldBeEmpty();
            if (commit)
                await transaction.CommitAsync();
            else
                await transaction.RollbackAsync();
        }
        await using var reader = _database.CreateContext();
        var after = (await MediaTestServices.CreateContentCatalog(reader).FindByRomIdAsync(original.RomId)).ShouldNotBeNull();
        after.RepresentationId.ShouldBe(commit ? target.Id : original.Graph.Id);
        after.Inspections.Count.ShouldBe(commit ? 1 : 0);
        (await reader.Verifications.CountAsync()).ShouldBe(commit ? 2 : 1);
        (await reader.Files.CountAsync()).ShouldBe(commit ? 2 : 1);
    }

    [Fact]
    public async Task ReadLockedPublication_StagedBootAndReference_AreReadTogetherWithoutPublishingThem()
    {
        byte[] bytes = new byte[131072];
        var identity = MediaFactFixtures.Identity(bytes);
        await using var db = _database.CreateContext();
        PublishedWholeMediaFixture original;
        await using (var seed = await db.Database.BeginTransactionAsync())
        {
            var file = new FileEntityPersistence { Sha256 = identity.Sha256, Size = identity.Size, SizeOnDisk = identity.Size };
            db.Files.Add(file);
            await db.SaveChangesAsync();
            var proof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), file.Sha256, file.Size, identity, RomContainerFacts.Identity.Instance,
                new RomDecoderIdentity.Managed("fixture-original"), "identity/v1", "fixture-verifier", DateTimeOffset.UnixEpoch, null, null);
            original = await db.CreatePreferredMeasuredWholeAsync("disc.iso", file.Id, RomContainerFacts.Identity.Instance, proof,
                Guid.Empty, DateTimeOffset.UnixEpoch, CancellationToken.None);
            await seed.CommitAsync();
        }
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var boot = MediaFactFixtures.Ps2Boot(identity.Size, "fixture-boot");
            var observation = MediaFactFixtures.ObservePs2(Guid.NewGuid(), identity, bytes.AsSpan(0, 32), boot,
                "raw-boot/v1", boot.InspectorBuild, DateTimeOffset.UnixEpoch.AddTicks(31));
            (await MediaTestServices.CreateObservationStore(db, TimeProvider.System).StageAsync(original.RomId,
                original.Measurement(original.Graph.Verifications.Single().Id), observation)).IsError.ShouldBeFalse();
            byte[] body = [1];
            var reference = new RomDiscMediaReferenceRecord(Guid.NewGuid(), RomDiscReferenceSystem.PlayStation2, RomDiscReferenceMedia.Dvd5, 1,
                identity.Size, identity.Sha1, identity.Md5, identity.Crc32, body.Length, MediaFactFixtures.Identity(body).Sha256,
                "synthetic-reference", "fixture-parser/v1", "trusted-fixture/v1", DateTimeOffset.UnixEpoch);
            (await new RomDiscMediaReferenceImportStore(db, new Romd.Persistence.Repositories.Reconciliation.MediaReconciliationRequests(db, TimeProvider.System)).StageAsync(reference, body)).IsError.ShouldBeFalse();
            var catalog = MediaTestServices.CreateContentCatalog(db);
            var staged = (await catalog.ReadLockedPublicationAsync([original.RomId], CancellationToken.None))[original.RomId];
            staged.Inspections.Single().Layout.ShouldBe(boot);
            staged.References.Single().Id.ShouldBe(reference.Id);
            var committed = (await catalog.FindByRomIdAsync(original.RomId)).ShouldNotBeNull();
            committed.Inspections.ShouldBeEmpty();
            committed.References.ShouldBeEmpty();
            await transaction.RollbackAsync();
        }
        (await db.RomDiscMediaReferences.CountAsync()).ShouldBe(0);
        (await MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(original.RomId)).ShouldNotBeNull().Inspections.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadLockedPublication_WithoutCallerTransaction_Refuses()
    {
        await using var db = _database.CreateContext();
        await Should.ThrowAsync<InvalidOperationException>(() => MediaTestServices.CreateContentCatalog(db).ReadLockedPublicationAsync([1], CancellationToken.None));
    }

    public void Dispose() => _database.Dispose();
}
