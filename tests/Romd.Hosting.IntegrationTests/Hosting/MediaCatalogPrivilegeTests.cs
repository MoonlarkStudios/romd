using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class MediaCatalogPrivilegeTests
{
    [Theory]
    [InlineData("romd_admin", 0U)]
    [InlineData("romd_admin", uint.MaxValue)]
    [InlineData("romd_consumer", 0U)]
    [InlineData("romd_consumer", uint.MaxValue)]
    public async Task Catalog_ProductionApiRole_ReadsBoundedAssertionsWithoutRawBodyAccess(string role, uint crc)
    {
        await using var postgres = await RomdPostgreSqlContainer.StartWithRomdRolesAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{PostgreSqlConfiguration.ProvisioningConnectionName}"] = postgres.ProvisionerConnectionString
        }).Build();
        await new PostgreSqlSchemaProvisioner(configuration, NullLogger<PostgreSqlSchemaProvisioner>.Instance).StartAsync(default);
        var identity = new RomLogicalIdentity(131072, Sha256.Parse(new string('a', 64)), Sha1.Parse(new string('d', 40)),
            Md5.Parse(new string('e', 32)), Crc32.FromUInt32(crc));
        var at = DateTimeOffset.UnixEpoch.AddTicks(1234);
        int romId;
        await using (var worker = Context(postgres.WorkerConnectionString))
        {
            var file = new FileEntityPersistence { Sha256 = identity.Sha256, Size = identity.Size, SizeOnDisk = identity.Size };
            worker.Files.Add(file);
            await worker.SaveChangesAsync();
            await using var publication = await worker.Database.BeginTransactionAsync();
            var proof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), identity.Sha256, identity.Size, identity,
                RomContainerFacts.Identity.Instance, new RomDecoderIdentity.Managed("catalog-role-hasher/v1"), "identity/v1",
                "catalog-role-verifier/v1", at, null, null);
            romId = (await worker.CreatePreferredMeasuredWholeAsync("original.iso", file.Id, RomContainerFacts.Identity.Instance,
                proof, Guid.Empty, at, default)).RomId;
            await publication.CommitAsync();
        }
        string runtime = role == "romd_admin" ? postgres.AdminConnectionString : postgres.ConsumerConnectionString;
        await using var api = Context(runtime);
        var catalog = MediaTestServices.CreateContentCatalog(api);
        var original = (await catalog.FindByRomIdAsync(romId)).ShouldNotBeNull();
        original.Identity.ShouldBe(identity);
        original.Artifacts.Single().Facts.Verifications.Count.ShouldBe(1);
        original.References.ShouldBeEmpty();
        byte[] raw = [1, 0, 255];
        var reference = new RomDiscMediaReferenceRecord(Guid.NewGuid(), RomDiscReferenceSystem.PlayStation2, RomDiscReferenceMedia.Dvd5,
            1, identity.Size, identity.Sha1, identity.Md5, identity.Crc32, raw.Length,
            Sha256.FromSpan(System.Security.Cryptography.SHA256.HashData(raw)), "https://example.test/reference/42",
            "synthetic-reference/v1", "catalog-role-test/v1", at.AddTicks(45));
        await using (var worker = Context(postgres.WorkerConnectionString))
        {
            await using var publication = await worker.Database.BeginTransactionAsync();
            var importer = new RomDiscMediaReferenceImportStore(worker, new Romd.Persistence.Repositories.Reconciliation.MediaReconciliationRequests(worker, TimeProvider.System));
            (await importer.StageAsync(reference, raw)).IsError.ShouldBeFalse();
            // A separate retrieval with one different published hash cannot authorize this ROM.
            var unrelated = new RomDiscMediaReferenceRecord(Guid.NewGuid(), reference.System, reference.Media, 1, identity.Size, identity.Sha1,
                Md5.Parse(new string('f', 32)), identity.Crc32, raw.Length, reference.RawReferenceSha256,
                reference.ReferenceSource, reference.ParserProfile, reference.AcceptedSourceProvenance, at);
            (await importer.StageAsync(unrelated, raw)).IsError.ShouldBeFalse();
            await publication.CommitAsync();
        }
        var referenced = (await catalog.FindByRomIdAsync(romId)).ShouldNotBeNull();
        referenced.References.Single().ShouldBe(reference);
        foreach (string invalid in new[]
        {
            "SELECT count(*) FROM romd.read_disc_reference_assertions(NULL)",
            "SELECT count(*) FROM romd.read_disc_reference_assertions(ARRAY[0])",
            "SELECT count(*) FROM romd.read_disc_reference_assertions(ARRAY[NULL]::integer[])",
            "SELECT count(*) FROM romd.read_disc_reference_assertions(array_fill(1,ARRAY[257]))"
        })
            (await Should.ThrowAsync<PostgresException>(() => api.Database.ExecuteSqlRawAsync(invalid))).SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        (await api.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM romd.read_disc_reference_assertions(ARRAY[]::integer[])").SingleAsync()).ShouldBe(0);
        if (role == "romd_admin" && crc == uint.MaxValue)
        {
            await using var worker = Context(postgres.WorkerConnectionString);
            await using var publication = await worker.Database.BeginTransactionAsync();
            var importer = new RomDiscMediaReferenceImportStore(worker, new Romd.Persistence.Repositories.Reconciliation.MediaReconciliationRequests(worker, TimeProvider.System));
            for (int i = 0; i < 129; i++)
            {
                var extra = new RomDiscMediaReferenceRecord(Guid.NewGuid(), reference.System, reference.Media, 1, identity.Size,
                    identity.Sha1, identity.Md5, identity.Crc32, raw.Length, reference.RawReferenceSha256,
                    reference.ReferenceSource, reference.ParserProfile, reference.AcceptedSourceProvenance, at);
                (await importer.StageAsync(extra, raw)).IsError.ShouldBeFalse();
            }
            await publication.CommitAsync();
            // 130 matching assertions must retain the over-limit signal, never silently become an admissible set of 128.
            (await catalog.FindByRomIdAsync(romId)).ShouldNotBeNull().References.Count.ShouldBe(129);
        }
        foreach (string forbidden in new[]
        {
            """SELECT "RawBody" FROM romd."RomDiscMediaReferences" """,
            """SELECT "Id" FROM romd."RomDiscMediaReferences" """,
            """UPDATE romd."RomDiscMediaReferences" SET "FileCount" = 2"""
        })
            (await Should.ThrowAsync<PostgresException>(() => api.Database.ExecuteSqlRawAsync(forbidden))).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    private static RomdDbContext Context(string connectionString)
    {
        var options = new DbContextOptionsBuilder<RomdDbContext>();
        PostgreSqlConfiguration.Configure(options, connectionString);
        return new(options.Options);
    }
}
