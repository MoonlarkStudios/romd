using System.Globalization;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Source.Dat.Commands.ActivateDatVersion;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;
using Romd.Infrastructure.Enrichment;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.MetadataProviders;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed partial class ProviderIdentityDatabaseTests
{
    [Theory]
    [InlineData(1990)]
    [InlineData(1992)]
    public async Task ReadAsync_StagingAndActivatingDat_ScopesEvidenceAndTrustToPublishedFacts(int incomingYear)
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        db.TitleExternalIds.Add(new TitleExternalIdEntity
        {
            TitleId = 1,
            Provider = "confirmed",
            ExternalId = "84",
            IsConfirmed = true
        });
        await db.SaveChangesAsync();
        await AddAutomatic(db, "automatic", "42");
        var evidence = new TitleEnrichmentEvidenceReader(db);
        var store = new ProviderIdentityStore(db, evidence, TimeProvider.System);
        var original = (await store.ReadAsync(1, default)).ShouldNotBeNull();
        original.Facts.Years.ShouldBe([1990]);
        (await store.ReadTrustedAsync(1, "automatic", default)).ShouldBe("42");

        await AddDatVersion(db, 2, DatFileLifecycle.Superseded, 1980);
        await AddDatVersion(db, 3, DatFileLifecycle.PendingActivation, incomingYear);

        var staged = (await store.ReadAsync(1, default)).ShouldNotBeNull();
        staged.Facts.Years.ShouldBe([1990]);
        staged.Fingerprint.ShouldBe(original.Fingerprint);
        (await evidence.ReadAsync(1)).ShouldHaveSingleItem().Year.ShouldBe("1990");
        (await store.ReadTrustedAsync(1, "automatic", default)).ShouldBe("42");

        await ActivateVersion(3);

        var current = (await store.ReadAsync(1, default)).ShouldNotBeNull();
        current.Facts.Years.ShouldBe([incomingYear]);
        (await evidence.ReadAsync(1)).ShouldHaveSingleItem().Year.ShouldBe(incomingYear.ToString(CultureInfo.InvariantCulture));
        bool factsChanged = incomingYear != 1990;
        (current.Fingerprint != original.Fingerprint).ShouldBe(factsChanged);
        (await store.ReadTrustedAsync(1, "automatic", default)).ShouldBe(factsChanged ? null : "42");
        (await store.ReadTrustedAsync(1, "confirmed", default)).ShouldBe("84");
    }

    private async Task ActivateVersion(int versionId)
    {
        await using var writer = _database.CreateContext();
        var activate = new ActivateDatVersionCommandHandler(
            new DatRepository(writer, TimeProvider.System),
            CatalogProjectionTestFactory.Create(writer),
            new LibraryRepository(writer),
            new AdminRealtimeOutbox(writer, TimeProvider.System),
            new EfUnitOfWork(writer),
            TimeProvider.System,
            NullLogger<ActivateDatVersionCommandHandler>.Instance,
            new SourceLifecycleStore(writer));
        var activated = await activate.HandleAsync(ActivateDatVersionCommand.Create(versionId, expectedActiveDatId: 1).Value);
        activated.IsError.ShouldBeFalse(activated.IsError ? activated.FirstError.Description : null);
    }

    [Fact]
    public async Task Provision_PreviousFingerprintIncludedPendingDat_OnlyActiveEvidenceRemainsTrusted()
    {
        await using var database = PostgreSqlTestDatabase.CreateEmpty();
        await using var db = database.CreateContext();
        await db.Database.GetService<IMigrator>().MigrateAsync("20261123000000_DatParentDeclarations");
        await Seed(db);
        await AddAutomatic(db, "installed", "42");
        string installedFingerprint = await Fingerprint(db);
        await AddDatVersion(db, 2, DatFileLifecycle.PendingActivation, 1992);
        await AddAutomatic(db, "mixed", "84");
        (await Fingerprint(db)).ShouldNotBe(installedFingerprint);
        (await Trusted(db, "mixed")).ShouldBeTrue();

        await database.ProvisionAsync();

        (await Fingerprint(db)).ShouldBe(installedFingerprint);
        (await Trusted(db, "installed")).ShouldBeTrue();
        (await Trusted(db, "mixed")).ShouldBeFalse();
    }

    private static async Task AddDatVersion(RomdDbContext db, int versionId, DatFileLifecycle lifecycle, int year,
        int datSourceId = 1, int sourceEntryId = 1)
    {
        db.Files.Add(new FileEntityPersistence
        {
            Id = versionId,
            Sha256 = Sha256.FromBytes(Enumerable.Repeat((byte)versionId, Sha256.ByteLength).ToArray()),
            Size = 1,
            SizeOnDisk = 1
        });
        db.DatFiles.Add(new DatFileEntity
        {
            Id = versionId,
            DatSourceId = datSourceId,
            Name = "Test DAT",
            Description = "Test",
            Type = "NoIntro",
            PlatformId = 1,
            OriginalFilename = "test.dat",
            FileId = versionId,
            Lifecycle = lifecycle.ToString(),
            SupersededAt = lifecycle == DatFileLifecycle.Superseded ? DateTimeOffset.UnixEpoch : null
        });
        db.DatGames.Add(new DatGameEntity
        {
            Id = 100 + versionId,
            DatFileId = versionId,
            SourceEntryId = sourceEntryId,
            Name = "Game",
            Year = year.ToString(CultureInfo.InvariantCulture)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
