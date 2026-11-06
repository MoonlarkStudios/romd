using Romd.PostgreSql.TestSupport;
using Romd.Admin.Application.Source.Rom;
using Romd.Application.Common.Content;
using Romd.Persistence.MediaFacts;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Romd.Admin.Application.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Readiness;
using Romd.Persistence;
using Romd.Persistence.Enrichment;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;
using static Romd.Hosting.IntegrationTests.Hosting.HangfirePostgreSqlTestSupport;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Runs deploy/postgres/init/10-romd-roles.sh in a real PostgreSQL 18 and proves the grant
///     model behaviourally: the worker provisions both schemas as romd_provisioner with romd_owner
///     owning every object, runtime roles get DML only and none of them can rewrite a published
///     schema version or the migration history, the consumer can read nothing but the published
///     Hangfire schema version, holds the exact application allow-list, and an admin
///     enqueue is executed by a worker under the runtime credentials.
/// </summary>
public sealed class PostgreSqlRolePrivilegeTests
{
    private const string InsufficientPrivilege = "42501";

    [Fact]
    public async Task RoleBootstrap_ProvisionerOwnsSchema_RuntimeRolesAreDmlOnly_AdminEnqueueRunsOnWorker()
    {
        await using RomdPostgreSqlContainer postgres = await RomdPostgreSqlContainer.StartWithRomdRolesAsync();

        await ProvisionAsync(postgres.ProvisionerConnectionString);

        await AssertHangfireObjectsOwnedByRomdOwnerAsync(postgres.BootstrapConnectionString);
        foreach (string runtime in postgres.RuntimeConnectionStrings)
        {
            await AssertCannotCreateAlterOrDropAsync(runtime);
        }

        await AssertConsumerReadsPublishedVersionOnlyAsync(postgres);
        await AssertReadinessProbeSeesPublishedVersionAsync(postgres.AdminConnectionString);
        await AssertReadinessProbeSeesPublishedVersionAsync(postgres.ConsumerConnectionString);
        await AssertAdminEnqueueIsExecutedByWorkerAsync(postgres);

        await ProvisionApplicationSchemaAsync(postgres.ProvisionerConnectionString);
        await AssertApplicationObjectsOwnedByRomdOwnerAsync(postgres.BootstrapConnectionString);
        await AssertApplicationSchemaVersionReadableByEveryRuntimeRoleAsync(postgres);
        await AssertRomdSchemaDefaultsKeepConsumerReadOnlyAsync(postgres);
        await AssertWorkerAndAdminHaveApplicationDmlAsync(postgres);
        await AssertConsumerHoldsExactApplicationAllowListAsync(postgres.ConsumerConnectionString);
        await AssertMetadataProviderSettingsAreManagementOnlyAsync(postgres);
        await AssertDiscReferenceAdmissionIsWorkerOnlyAsync(postgres);
        await AssertMeasurementPublicationIsWorkerOnlyAsync(postgres);
        await AssertWorkerAndAdminCanAssessEnrichmentAsync(postgres);
    }

    [Fact]
    public async Task DiscReference_WorkerRetainsExactRawBody_ReferenceFactsRemainImmutable()
    {
        await using RomdPostgreSqlContainer postgres = await RomdPostgreSqlContainer.StartWithRomdRolesAsync();
        await ProvisionApplicationSchemaAsync(postgres.ProvisionerConnectionString);
        Convert.ToString(await ScalarAsync(postgres.WorkerConnectionString, "SELECT current_user")).ShouldBe("romd_worker");

        // Exact synthetic raw custody only; no external-reference or media qualification.
        const string referenceId = "11111111-2222-3333-4444-555555555555";
        const string referenceTable = "romd.\"RomDiscMediaReferences\"";
        await ExecuteAsync(postgres.WorkerConnectionString, """
            INSERT INTO romd."RomDiscMediaReferences"
                ("Id", "System", "Media", "FileCount", "LogicalBytes", "Sha1", "Md5", "Crc32",
                 "RawReferenceBytes", "RawReferenceSha256", "ReferenceSource", "ParserProfile",
                 "AcceptedSourceProvenance", "RetrievedAtUtcTicks", "RawBody")
            VALUES ('11111111-2222-3333-4444-555555555555', 'PlayStation2', 'Dvd5', 1, 131072,
                decode(repeat('01',20),'hex'), decode(repeat('02',16),'hex'), 4294967295,
                3, sha256(decode('0100ff','hex')), 'https://example.test/reference/42',
                'synthetic-reference/v1', 'role-test/v1', 621355968000001234, decode('0100ff','hex'))
            """);
        string referenceBefore = Convert.ToString(await ScalarAsync(postgres.WorkerConnectionString,
            $"SELECT to_jsonb(reference)::text FROM {referenceTable} reference WHERE \"Id\" = '{referenceId}'")).ShouldNotBeNull();
        Convert.ToInt32(await ScalarAsync(postgres.WorkerConnectionString, """
            SELECT count(*) FROM romd."RomDiscMediaReferences"
            WHERE "Id" = '11111111-2222-3333-4444-555555555555' AND "Crc32" = 4294967295
                AND "LogicalBytes" = 131072 AND "RawReferenceBytes" = 3
                AND "RawBody" = decode('0100ff','hex') AND "RawReferenceSha256" = sha256("RawBody")
            """)).ShouldBe(1);
        foreach ((string runtime, string role) in new[]
                 {
                     (postgres.WorkerConnectionString, "romd_worker"),
                     (postgres.AdminConnectionString, "romd_admin"),
                     (postgres.ConsumerConnectionString, "romd_consumer")
                 })
        {
            Convert.ToString(await ScalarAsync(runtime, "SELECT current_user")).ShouldBe(role);
            foreach (string privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER" })
                Convert.ToBoolean(await ScalarAsync(runtime,
                        $"SELECT has_table_privilege(current_user, '{referenceTable}', '{privilege}')"))
                    .ShouldBe(role == "romd_worker" && (privilege is "SELECT" or "INSERT"), $"{role} {privilege}");
            Convert.ToBoolean(await ScalarAsync(runtime, """
                SELECT count(*) = 15 AND bool_and(
                    has_column_privilege(current_user, 'romd."RomDiscMediaReferences"', attribute.attname, 'SELECT') = (current_user = 'romd_worker')
                    AND has_column_privilege(current_user, 'romd."RomDiscMediaReferences"', attribute.attname, 'INSERT') = (current_user = 'romd_worker')
                    AND has_column_privilege(current_user, 'romd."RomDiscMediaReferences"', attribute.attname, 'UPDATE')
                        = (current_user = 'romd_worker' AND attribute.attname = 'Id'))
                FROM pg_attribute attribute WHERE attribute.attrelid = 'romd."RomDiscMediaReferences"'::regclass
                    AND attribute.attnum > 0 AND NOT attribute.attisdropped
                """)).ShouldBeTrue($"{role} exact column privileges");
            if (role == "romd_worker")
                continue;
            await AssertDeniedAsync(runtime, $"SELECT \"Id\" FROM {referenceTable} WHERE \"Id\" = '{referenceId}' FOR SHARE");
            await AssertDeniedAsync(runtime, $"UPDATE {referenceTable} SET \"Id\" = \"Id\" WHERE \"Id\" = '{referenceId}'");
        }
        var immutable = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(postgres.WorkerConnectionString,
            $"UPDATE {referenceTable} SET \"Id\" = \"Id\" WHERE \"Id\" = '{referenceId}'"));
        immutable.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        immutable.MessageText.ShouldBe("Retained disc references are immutable");
        await AssertDeniedAsync(postgres.WorkerConnectionString, $"UPDATE {referenceTable} SET \"FileCount\" = 2 WHERE \"Id\" = '{referenceId}'");
        await AssertDeniedAsync(postgres.WorkerConnectionString, $"DELETE FROM {referenceTable} WHERE \"Id\" = '{referenceId}'");
        await AssertDeniedAsync(postgres.WorkerConnectionString, $"ALTER TABLE {referenceTable} DISABLE TRIGGER rom_disc_reference_guard");
        Convert.ToString(await ScalarAsync(postgres.WorkerConnectionString,
            $"SELECT to_jsonb(reference)::text FROM {referenceTable} reference WHERE \"Id\" = '{referenceId}'")).ShouldBe(referenceBefore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogicalObservation_WorkerPublishesImmutableFacts_SourceCollectionRetainsThemUntilRomDeletion(bool chd)
    {
        await using RomdPostgreSqlContainer postgres = await RomdPostgreSqlContainer.StartWithRomdRolesAsync();
        await ProvisionApplicationSchemaAsync(postgres.ProvisionerConnectionString);
        var identity = new RomLogicalIdentity(131072, Sha256.Parse(new string('a', 64)), Sha1.Parse(new string('d', 40)),
            Md5.Parse(new string('e', 32)), Crc32.FromUInt32(0));
        var observedAt = DateTimeOffset.UnixEpoch.AddTicks(1234);
        var overall = Sha1.Parse(new string('f', 40));
        var container = MediaFactFixtures.Chd(identity.Size, 16384, 2048, [0x7a6c6962, 0x6c7a6d61, 0x68756666, 0x7a737464],
            identity.Sha1, overall, [new(0x44564420, 1, new byte[] { 0 })]);
        var originalProof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), identity.Sha256, identity.Size, identity,
            RomContainerFacts.Identity.Instance, new RomDecoderIdentity.Managed("role-fixture-hasher/v1"), "identity/v1",
            "role-fixture-verifier/v1", observedAt, null, null);
        var chdProof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), Sha256.Parse(new string('b', 64)), 4096, identity,
            container, new RomDecoderIdentity.Native(new("linux-arm64", overall, 1)), "chd-logical/v1",
            "role-fixture-verifier/v1", observedAt, identity.Sha1, overall);
        int romId, sourceRepresentationId, sourceOutputId, keeperOutputId;
        Guid sourceProofId;
        var boot = MediaFactFixtures.Ps2Boot(identity.Size, "role-fixture-boot/v1");
        var observation = MediaFactFixtures.ObservePs2(Guid.NewGuid(), identity, new byte[32], boot,
            "logical-inspection/v1", boot.InspectorBuild, observedAt.AddTicks(43));
        await using (var worker = RuntimeContext(postgres.WorkerConnectionString))
        {
            var original = new FileEntityPersistence { Sha256 = identity.Sha256, Size = identity.Size, SizeOnDisk = identity.Size };
            var artifact = new FileEntityPersistence { Sha256 = chdProof.ArtifactSha256, Size = 4096, SizeOnDisk = 4096 };
            worker.Files.AddRange(original, artifact);
            await worker.SaveChangesAsync();
            await using var transaction = await worker.Database.BeginTransactionAsync();
            var published = await worker.CreatePreferredMeasuredWholeAsync("role-probe.iso", original.Id,
                RomContainerFacts.Identity.Instance, originalProof, Guid.NewGuid(), observedAt, CancellationToken.None);
            romId = published.RomId;
            var encoded = await worker.StageMeasuredWholeAsync(artifact.Id, container, chdProof,
                new(RomOutputSelector.Whole.Instance, romId, false), CancellationToken.None);
            var source = chd ? encoded : published.Graph;
            var keeper = chd ? published.Graph : encoded;
            sourceRepresentationId = source.Id;
            sourceOutputId = source.Outputs.Single().Id;
            sourceProofId = source.Verifications.Single().Id;
            keeperOutputId = keeper.Outputs.Single().Id;
            (await MediaTestServices.CreateObservationStore(worker, TimeProvider.System).StageAsync(romId,
                new(source.Id, sourceOutputId, source.FileId, sourceProofId), observation)).IsError.ShouldBeFalse();
            Convert.ToInt32(await ScalarAsync(postgres.WorkerConnectionString,
                """SELECT count(*) FROM romd."LogicalInspections" """)).ShouldBe(0);
            await transaction.CommitAsync();
        }
        foreach (string table in new[] { "LogicalInspections", "Ps2BootObservations" })
        {
            string qualified = $"romd.\"{table}\"";
            foreach (string runtime in postgres.RuntimeConnectionStrings)
            {
                Convert.ToInt32(await ScalarAsync(runtime, $"SELECT count(*) FROM {qualified}")).ShouldBe(1);
                await AssertDeniedAsync(runtime, $"TRUNCATE {qualified}");
            }
            foreach (string runtime in new[] { postgres.AdminConnectionString, postgres.ConsumerConnectionString })
            {
                await AssertDeniedAsync(runtime, $"INSERT INTO {qualified} SELECT * FROM {qualified}");
                await AssertDeniedAsync(runtime, $"DELETE FROM {qualified}");
            }
        }
        var immutable = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(postgres.WorkerConnectionString,
            """UPDATE romd."Ps2BootObservations" SET "EntryPoint" = "EntryPoint" + 4"""));
        immutable.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        await using (var worker = RuntimeContext(postgres.WorkerConnectionString))
        await using (var transaction = await worker.Database.BeginTransactionAsync())
        {
            var repository = MediaTestServices.CreateRomRepository(worker);
            await repository.LockRepresentationSetAsync(romId);
            await repository.SetPreferredRepresentationStagedAsync(romId, keeperOutputId);
            await repository.ReleaseRepresentationsStagedAsync(romId, [sourceOutputId]);
            await transaction.CommitAsync();
        }
        Convert.ToBoolean(await ScalarAsync(postgres.WorkerConnectionString,
            $"SELECT romd.delete_unowned_media_representation({sourceRepresentationId})")).ShouldBeTrue();
        await using (var observer = RuntimeContext(postgres.WorkerConnectionString))
        {
            var content = (await MediaTestServices.CreateContentCatalog(observer).FindByRomIdAsync(romId)).ShouldNotBeNull();
            var retained = content.Inspections.ShouldHaveSingleItem();
            retained.SameObservation(observation).ShouldBeTrue();
            retained.Identity.Crc32.IsEmpty.ShouldBeFalse();
            retained.Layout.ShouldBe(boot);
            (await observer.Verifications.AnyAsync(row => row.Id == sourceProofId)).ShouldBeFalse();
        }
        await using (var admin = RuntimeContext(postgres.AdminConnectionString))
            await MediaTestServices.CreateRomRepository(admin).DeleteAsync(romId);
        Convert.ToInt32(await ScalarAsync(postgres.WorkerConnectionString,
            """SELECT count(*) FROM romd."LogicalInspections" """)).ShouldBe(0);
        Convert.ToInt32(await ScalarAsync(postgres.WorkerConnectionString,
            """SELECT count(*) FROM romd."Ps2BootObservations" """)).ShouldBe(0);
        await using var final = RuntimeContext(postgres.WorkerConnectionString);
        (await final.Files.CountAsync()).ShouldBe(2); // Graph cleanup does not delete CAS ledger rows.
    }

    [Fact]
    public async Task ReconciliationExecutionTables_WorkerOwnsDml_ApiRolesCannotManufactureCustodyOrOutcomes()
    {
        await using var postgres = await RomdPostgreSqlContainer.StartWithRomdRolesAsync();
        await ProvisionApplicationSchemaAsync(postgres.ProvisionerConnectionString);
        await AssertApplicationSchemaVersionReadableByEveryRuntimeRoleAsync(postgres);
        string[] tables = ["MediaReconciliationAttempts","MediaReconciliationInputs","MediaReconciliationPins",
            "MediaReconciliationFailures","MediaReconciliationOutcomes","MediaReconciliationSelectionItems"];
        string[] privileges = ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER"];
        foreach (string name in tables)
        {
            string table = $"romd.\"{name}\"";
            foreach ((string connection, string role) in new[]
            {
                (postgres.WorkerConnectionString,"romd_worker"),(postgres.AdminConnectionString,"romd_admin"),
                (postgres.ConsumerConnectionString,"romd_consumer")
            })
            {
                Convert.ToString(await ScalarAsync(connection, "SELECT current_user")).ShouldBe(role);
                foreach (string privilege in privileges)
                    Convert.ToBoolean(await ScalarAsync(connection, $"SELECT has_table_privilege(current_user, '{table}', '{privilege}')"))
                        .ShouldBe(privilege == "SELECT" || role == "romd_worker" && privilege is "INSERT" or "UPDATE" or "DELETE", $"{role} {name} {privilege}");
                if (role != "romd_worker")
                {
                    await AssertDeniedAsync(connection, $"INSERT INTO {table} (\"JobId\") SELECT NULL::uuid WHERE false".Replace("(\"JobId\")", name == "MediaReconciliationSelectionItems" ? "(\"SelectionJobId\")" : "(\"JobId\")", StringComparison.Ordinal));
                    await AssertDeniedAsync(connection, $"DELETE FROM {table} WHERE false");
                }
                await AssertDeniedAsync(connection, $"TRUNCATE {table}");
                await AssertDeniedAsync(connection, $"ALTER TABLE {table} ADD COLUMN forbidden integer");
                await AssertDeniedAsync(connection, $"DROP TABLE {table}");
            }
            Convert.ToInt32(await ScalarAsync(postgres.BootstrapConnectionString, $"""
                SELECT count(*) FROM pg_class relation JOIN pg_namespace n ON n.oid = relation.relnamespace
                CROSS JOIN LATERAL aclexplode(COALESCE(relation.relacl,acldefault('r',relation.relowner))) acl
                WHERE n.nspname = 'romd' AND relation.relname = '{name}' AND acl.grantee = 0
                """)).ShouldBe(0);
            Convert.ToInt32(await ScalarAsync(postgres.WorkerConnectionString, $"SELECT count(*) FROM {table}")).ShouldBe(0);
        }
    }

    [Fact]
    public async Task CatalogCdLayoutBindings_WorkerPublishesImmutableBindings_ApiRolesOnlyRead()
    {
        await using var postgres = await RomdPostgreSqlContainer.StartWithRomdRolesAsync();
        await ProvisionApplicationSchemaAsync(postgres.ProvisionerConnectionString);
        await AssertApplicationSchemaVersionReadableByEveryRuntimeRoleAsync(postgres);
        const string table = "romd.\"CatalogCdLayoutBindings\"";
        foreach ((string connection, string role) in new[]
        {
            (postgres.WorkerConnectionString, "romd_worker"), (postgres.AdminConnectionString, "romd_admin"),
            (postgres.ConsumerConnectionString, "romd_consumer")
        })
        {
            foreach (string privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER" })
                Convert.ToBoolean(await ScalarAsync(connection, $"SELECT has_table_privilege(current_user, '{table}', '{privilege}')"))
                    .ShouldBe(privilege == "SELECT" || role == "romd_worker" && privilege is "INSERT" or "DELETE", $"{role} {privilege}");
            foreach (string privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER" })
                Convert.ToBoolean(await ScalarAsync(connection, $"""SELECT has_table_privilege(current_user,'romd."CurrentCatalogCdLayoutBindings"','{privilege}')"""))
                    .ShouldBe(privilege == "SELECT", $"{role} current-layout view {privilege}");
            await AssertDeniedAsync(connection, $"UPDATE {table} SET \"GeneratorProfile\" = 'forged' WHERE false");
            await AssertDeniedAsync(connection, $"TRUNCATE {table}");
            await AssertDeniedAsync(connection, $"ALTER TABLE {table} ADD COLUMN forbidden integer");
            await AssertDeniedAsync(connection, $"DROP TABLE {table}");
            if (role != "romd_worker")
            {
                await AssertDeniedAsync(connection, $"INSERT INTO {table} (\"Id\") SELECT 0 WHERE false");
                await AssertDeniedAsync(connection, $"DELETE FROM {table} WHERE false");
            }
            Convert.ToInt32(await ScalarAsync(connection, $"SELECT count(*) FROM {table}")).ShouldBe(0);
        }
    }

    [Fact]
    public async Task CatalogCdLayoutBindings_RuntimeRoles_PublishReadAndCascadeWithinTheirAuthorizedBoundaries()
    {
        await using var postgres = await RomdPostgreSqlContainer.StartWithRomdRolesAsync();
        await ProvisionApplicationSchemaAsync(postgres.ProvisionerConnectionString);
        var bin = MediaFactFixtures.Identity(new byte[4 * 2352]);
        var cue = MediaFactFixtures.Identity("generated descriptor"u8);
        var expected = MediaFactFixtures.Identity("original descriptor bytes"u8);
        var artifactHash = Sha256.Parse(new string('b', 64));
        var observed = DateTimeOffset.UnixEpoch.AddDays(1);
        await using (var bootstrap = RuntimeContext(postgres.BootstrapConnectionString))
        {
            bootstrap.Platforms.Add(new()
            {
                Id = 1,
                Name = "Fixture",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
                BuiltInVersion = 1,
                BaseName = "Fixture",
                BaseCompactLabel = "FX",
                ShortName = "fixture",
                CanonicalKey = "fixture"
            });
            bootstrap.Titles.Add(new() { Id = 1, PlatformId = 1, Name = "Fixture", NormalizedName = "fixture", EnrichmentStatus = "Completed" });
            bootstrap.CatalogReleases.Add(new() { Id = 1, PlatformId = 1, CatalogTitleId = 1, Fingerprint = "sha1:fixture", Name = "Fixture" });
            bootstrap.CatalogReleaseFiles.AddRange(
                new() { Id = 1, CatalogReleaseId = 1, FileFingerprint = "bin", Name = "track.bin", Size = bin.Size, Sha1 = bin.Sha1, Md5 = bin.Md5, Crc = bin.Crc32 },
                new() { Id = 2, CatalogReleaseId = 1, FileFingerprint = "cue", Name = "disc.cue", Size = expected.Size, Sha1 = expected.Sha1, Md5 = expected.Md5, Crc = expected.Crc32 });
            bootstrap.Files.AddRange(new() { Id = 1, Sha256 = artifactHash, Size = 512, SizeOnDisk = 512 },
                new() { Id = 2, Sha256 = cue.Sha256, Size = cue.Size, SizeOnDisk = cue.Size });
            await bootstrap.SaveChangesAsync();
        }
        int representationId;
        await using (var worker = RuntimeContext(postgres.WorkerConnectionString))
        await using (var transaction = await worker.Database.BeginTransactionAsync())
        {
            var store = new CatalogCdLayoutStore(worker);
            var requirement = new CatalogDescriptorRequirement(2, 1, "sha1:fixture", "cue", "disc.cue", expected.Size, expected.Sha1, expected.Md5, expected.Crc32);
            (await store.LockScopeAsync(requirement, CancellationToken.None)).ShouldBeTrue();
            var publisher = new MediaFactPublisher(worker);
            int rom = await publisher.CreateLogicalAsync("track.bin", bin, Guid.Empty, observed);
            var container = MediaFactFixtures.Chd(4 * 2448, 4 * 2448, 2448, [0, 0, 0, 0], Sha1.Parse(new string('c', 40)),
                Sha1.Parse(new string('d', 40)), [new(0x43485432, 1, "TRACK:1 TYPE:MODE2_RAW"u8)]);
            RomCdFileIdentity Cd(RomLogicalIdentity identity) => new(identity.Size, identity.Sha256, identity.Sha1, identity.Md5, identity.Crc32);
            var manifest = new RomReconstructionManifest.CdTracks(4, [new(1, 0, 4, RomCdTrackKind.Mode2Raw)],
                [new RomCdBinMember("track.bin", Cd(bin), Cd(bin), [1]), new RomCdStoredMember("disc.cue", Cd(cue), RomCdStoredRole.Cue)]);
            var decoder = new RomChdDecoderBuild("linux-x64", Sha1.Parse(new string('a', 40)), 2);
            var proof = new RomRepresentationVerification(Guid.NewGuid(), artifactHash, 512, new RomDecoderIdentity.Native(decoder),
                "fixture-inspection", "fixture-verifier", observed, [new(new RomOutputSelector.Member(0), bin)], container,
                container.StoredRawSha1, container.StoredOverallSha1);
            var graph = await publisher.PublishAsync(1, container, manifest, proof, [new(new RomOutputSelector.Member(0), rom, true)], [new(1, 2, cue.Sha256, cue.Size)]);
            representationId = graph.Id;
            var binding = await store.PublishAsync(new(Guid.NewGuid(), requirement, graph.Id, 1, "fixture-generator/v1", observed), CancellationToken.None);
            binding.IsError.ShouldBeFalse();
            await transaction.CommitAsync();
        }
        await using (var consumer = RuntimeContext(postgres.ConsumerConnectionString))
        await using (var transaction = await consumer.Database.BeginTransactionAsync())
        {
            var binding = (await new DerivedDescriptorReader(consumer).ReadForReleaseAsync(1, CancellationToken.None)).Single();
            binding.Identity.ShouldBe(cue);
            binding.RepresentationId.ShouldBe(representationId);
            await transaction.CommitAsync();
        }
        await using (var admin = RuntimeContext(postgres.AdminConnectionString))
        await using (var transaction = await admin.Database.BeginTransactionAsync())
        {
            await CatalogTopologyFence.AcquireAsync(admin, CancellationToken.None);
            await admin.CatalogReleaseFiles.Where(file => file.Id == 2).ExecuteDeleteAsync();
            await transaction.CommitAsync();
        }
        Convert.ToInt32(await ScalarAsync(postgres.WorkerConnectionString, """SELECT count(*) FROM romd."CatalogCdLayoutBindings" """)).ShouldBe(0);
        Convert.ToInt32(await ScalarAsync(postgres.WorkerConnectionString, """SELECT count(*) FROM romd."Roms" """)).ShouldBe(1);
        Convert.ToInt32(await ScalarAsync(postgres.WorkerConnectionString, """SELECT count(*) FROM romd."Verifications" """)).ShouldBe(1);
    }

    private static async Task ProvisionApplicationSchemaAsync(string provisionerConnectionString)
    {
        var provisioner = new PostgreSqlSchemaProvisioner(
            ConnectionConfiguration(
                PostgreSqlConfiguration.ProvisioningConnectionName,
                provisionerConnectionString),
            NullLogger<PostgreSqlSchemaProvisioner>.Instance);
        await provisioner.StartAsync(CancellationToken.None);
    }

    private static async Task AssertApplicationObjectsOwnedByRomdOwnerAsync(string bootstrapConnectionString)
    {
        await using var connection = new NpgsqlConnection(bootstrapConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.relname, pg_get_userbyid(c.relowner)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'romd' AND c.relkind IN ('r', 'S', 'i')
            """;
        var owners = new Dictionary<string, string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                owners[reader.GetString(0)] = reader.GetString(1);
            }
        }

        owners.Keys.ShouldContain("Titles");
        owners.Keys.ShouldContain("__EFMigrationsHistory");
        owners.Keys.ShouldContain(PostgreSqlConfiguration.SchemaVersionTable);
        owners.Values.Distinct().ShouldBe(["romd_owner"]);
    }

    private static async Task AssertApplicationSchemaVersionReadableByEveryRuntimeRoleAsync(
        RomdPostgreSqlContainer postgres)
    {
        foreach (string runtime in postgres.RuntimeConnectionStrings)
        {
            Convert.ToInt32(await ScalarAsync(runtime, "SELECT version FROM romd.romd_schema WHERE singleton = TRUE"))
                .ShouldBe(PostgreSqlConfiguration.ExpectedSchemaVersion);
            Convert.ToInt32(await ScalarAsync(runtime, @"SELECT count(*) FROM romd.""__EFMigrationsHistory"""))
                .ShouldBe(typeof(RomdDbContext).Assembly.GetTypes().Count(type =>
                    type.IsSubclassOf(typeof(Microsoft.EntityFrameworkCore.Migrations.Migration))));
            await AssertDeniedAsync(runtime, "UPDATE romd.romd_schema SET version = 99");
            await AssertDeniedAsync(
                runtime,
                "INSERT INTO romd.romd_schema (singleton, version, published_at_utc) VALUES (FALSE, 99, now())");
            await AssertDeniedAsync(runtime, @"DELETE FROM romd.""__EFMigrationsHistory""");
        }

        var builder = new DbContextOptionsBuilder<RomdDbContext>();
        PostgreSqlConfiguration.Configure(builder, postgres.ConsumerConnectionString);
        await using var context = new RomdDbContext(builder.Options);
        (await new RomdSchemaProbe(context).GetPublishedVersionAsync(CancellationToken.None))
            .ShouldBe(PostgreSqlConfiguration.ExpectedSchemaVersion);
    }

    private static async Task AssertWorkerAndAdminHaveApplicationDmlAsync(RomdPostgreSqlContainer postgres)
    {
        foreach (string runtime in new[] { postgres.WorkerConnectionString, postgres.AdminConnectionString })
        {
            await ExecuteAsync(runtime, """
                INSERT INTO romd."Regions" ("CanonicalKey", "Name", "SortOrder", "BaseSortOrder", "Retired", "IsAutoCreated", "CreatedAt", "CreatedByUserId")
                VALUES ('dat-privilege-probe', 'Privilege probe', 999, 0, FALSE, FALSE, 0, '00000000-0000-0000-0000-000000000000')
                """);
            await ExecuteAsync(
                runtime,
                @"UPDATE romd.""Regions"" SET ""SortOrder"" = 998 WHERE ""Name"" = 'Privilege probe'");
            await ExecuteAsync(runtime, @"DELETE FROM romd.""Regions"" WHERE ""Name"" = 'Privilege probe'");
            await AssertDeniedAsync(runtime, @"TRUNCATE romd.""Regions""");
        }
    }

    /// <summary>
    ///     The consumer allow-list is exact: read catalog data, write its own settings, play sessions,
    ///     and OpenIddict grants, and touch only the credential columns of a user row. Anything else,
    ///     including the columns next to the granted ones, is a privilege error rather than a no-op.
    /// </summary>
    private static async Task AssertConsumerHoldsExactApplicationAllowListAsync(string consumerConnectionString)
    {
        const string noUser = "'00000000-0000-0000-0000-000000000000'";
        Convert.ToInt32(await ScalarAsync(consumerConnectionString, @"SELECT count(*) FROM romd.""Titles"""))
            .ShouldBe(0);
        await ExecuteAsync(consumerConnectionString, @"DELETE FROM romd.""OpenIddictTokens"" WHERE ""Id"" = 'none'");
        await ExecuteAsync(
            consumerConnectionString,
            @"DELETE FROM romd.""OpenIddictAuthorizations"" WHERE ""Id"" = 'none'");
        await ExecuteAsync(
            consumerConnectionString,
            $@"UPDATE romd.""ConsumerUserSettings"" SET ""UpdatedAt"" = 0 WHERE ""UserId"" = {noUser}");
        Convert.ToBoolean(await ScalarAsync(
                consumerConnectionString,
                """
                SELECT has_table_privilege(current_user, 'romd."PlaySessions"', 'INSERT')
                   AND has_table_privilege(current_user, 'romd."PlaySessions"', 'UPDATE')
                   AND has_table_privilege(current_user, 'romd."PlaySessions"', 'DELETE')
                """))
            .ShouldBeTrue();
        await ExecuteAsync(
            consumerConnectionString,
            $@"UPDATE romd.""PlaySessions"" SET ""UpdatedAt"" = 0 WHERE ""UserId"" = {noUser}");
        await ExecuteAsync(
            consumerConnectionString,
            $@"DELETE FROM romd.""PlaySessions"" WHERE ""UserId"" = {noUser}");
        await ExecuteAsync(
            consumerConnectionString,
            $@"UPDATE romd.""AspNetUsers"" SET ""SecurityStamp"" = 'probe' WHERE ""Id"" = {noUser}");

        await ExecuteAsync(consumerConnectionString,
            $@"UPDATE romd.""AspNetUsers"" SET ""LastSignedInAt"" = 0 WHERE ""Id"" = {noUser}");

        Convert.ToBoolean(await ScalarAsync(consumerConnectionString,
                """
                SELECT has_table_privilege(current_user, 'romd."VfsConnections"', 'SELECT')
                   AND has_table_privilege(current_user, 'romd."VfsConnections"', 'INSERT')
                   AND has_table_privilege(current_user, 'romd."VfsConnections"', 'UPDATE')
                   AND NOT has_table_privilege(current_user, 'romd."VfsConnections"', 'DELETE')
                """))
            .ShouldBeTrue();

        string[] denied =
        [
            @"SELECT * FROM romd.""AccountLinks""",
            @"SELECT * FROM romd.""AdminAuditEvents""",
            """
            INSERT INTO romd."Regions" ("CanonicalKey", "Name", "SortOrder", "BaseSortOrder", "Retired", "IsAutoCreated", "CreatedAt", "CreatedByUserId")
            VALUES ('dat-x', 'x', 1, 0, FALSE, FALSE, 0, '00000000-0000-0000-0000-000000000000')
            """,
            @"UPDATE romd.""Titles"" SET ""Name"" = 'probe'",
            @"DELETE FROM romd.""ConsumerUserSettings""",
            @"DELETE FROM romd.""AspNetUsers""",
            $@"UPDATE romd.""AspNetUsers"" SET ""UserName"" = 'probe' WHERE ""Id"" = {noUser}",
            $@"UPDATE romd.""AspNetUsers"" SET ""Email"" = 'probe' WHERE ""Id"" = {noUser}",
            @"INSERT INTO romd.""OpenIddictApplications"" (""Id"", ""ConcurrencyToken"") VALUES ('x', 'x')",
            @"DELETE FROM romd.""Jobs"""
        ];
        foreach (string statement in denied)
        {
            await AssertDeniedAsync(consumerConnectionString, statement);
        }
    }

    private static async Task AssertMetadataProviderSettingsAreManagementOnlyAsync(RomdPostgreSqlContainer postgres)
    {
        foreach (string runtime in new[] { postgres.AdminConnectionString, postgres.WorkerConnectionString })
        {
            Convert.ToInt32(await ScalarAsync(runtime,
                """SELECT count(*) FROM romd."MetadataProviderSettings" WHERE "ProviderId" = 'igdb'"""))
                .ShouldBe(1);
            await ExecuteAsync(runtime,
                """UPDATE romd."MetadataProviderSettings" SET "Enabled" = FALSE WHERE "ProviderId" = 'igdb'""");
        }

        string[] denied =
        [
            @"SELECT * FROM romd.""MetadataProviderSettings""",
            """INSERT INTO romd."MetadataProviderSettings" ("ProviderId") VALUES ('forbidden')""",
            """UPDATE romd."MetadataProviderSettings" SET "Enabled" = TRUE WHERE "ProviderId" = 'igdb'""",
            """DELETE FROM romd."MetadataProviderSettings" WHERE "ProviderId" = 'igdb'"""
        ];
        foreach (string statement in denied)
        {
            await AssertDeniedAsync(postgres.ConsumerConnectionString, statement);
        }
    }

    private static async Task AssertDiscReferenceAdmissionIsWorkerOnlyAsync(RomdPostgreSqlContainer postgres)
    {
        // Synthetic raw custody tests the database roles; it does not authenticate a reference source.
        const string insert = """
            INSERT INTO romd."RomDiscMediaReferences"
                ("Id", "System", "Media", "FileCount", "LogicalBytes", "Sha1", "Md5", "Crc32",
                 "RawReferenceBytes", "RawReferenceSha256", "ReferenceSource", "ParserProfile",
                 "AcceptedSourceProvenance", "RetrievedAtUtcTicks", "RawBody")
            VALUES ('11111111-2222-3333-4444-555555555555', 'PlayStation2', 'Dvd5', 1, 131072,
                decode(repeat('01',20),'hex'), decode(repeat('02',16),'hex'), 4294967295,
                3, sha256(decode('0100ff','hex')), 'https://example.test/reference/42',
                'synthetic-reference/v1', 'role-test/v1', 621355968000001234, decode('0100ff','hex'))
            """;
        foreach (string runtime in new[] { postgres.AdminConnectionString, postgres.ConsumerConnectionString })
        {
            await AssertDeniedAsync(runtime, """SELECT * FROM romd."RomDiscMediaReferences" """);
            await AssertDeniedAsync(runtime, insert);
            await AssertDeniedAsync(runtime, """UPDATE romd."RomDiscMediaReferences" SET "FileCount" = 2""");
            await AssertDeniedAsync(runtime, """DELETE FROM romd."RomDiscMediaReferences" """);
        }

        await ExecuteAsync(postgres.WorkerConnectionString, insert);
        Convert.ToInt32(await ScalarAsync(postgres.WorkerConnectionString,
            """SELECT count(*) FROM romd."RomDiscMediaReferences" """)).ShouldBe(1);
        Convert.ToString(await ScalarAsync(postgres.WorkerConnectionString, """
            SELECT "Id" FROM romd."RomDiscMediaReferences"
            WHERE "Id" = '11111111-2222-3333-4444-555555555555' FOR SHARE
            """)).ShouldBe("11111111-2222-3333-4444-555555555555");
        await AssertDeniedAsync(postgres.WorkerConnectionString,
            """UPDATE romd."RomDiscMediaReferences" SET "FileCount" = 2""");
        await AssertDeniedAsync(postgres.WorkerConnectionString, """DELETE FROM romd."RomDiscMediaReferences" """);
        await AssertDeniedAsync(postgres.WorkerConnectionString, """TRUNCATE romd."RomDiscMediaReferences" """);
    }

    /// <summary>
    ///     The facts function relies on PostgreSQL's default PUBLIC EXECUTE plus runtime table grants. Both
    ///     management runtimes must still read a real title through the mapped eight-argument signature.
    /// </summary>
    private static async Task AssertWorkerAndAdminCanAssessEnrichmentAsync(RomdPostgreSqlContainer postgres)
    {
        int titleId;
        await using (var admin = RuntimeContext(postgres.AdminConnectionString))
        {
            var platform = new PlatformEntity { Name = "Privilege probe", ShortName = "privilege-probe" };
            admin.Platforms.Add(platform);
            await admin.SaveChangesAsync();
            var title = new TitleEntity
            {
                PlatformId = platform.Id,
                Name = "Privilege probe",
                NormalizedName = "privilege probe",
                EnrichmentStatus = "None",
                CreatedAt = DateTimeOffset.UtcNow
            };
            admin.Titles.Add(title);
            await admin.SaveChangesAsync();
            titleId = title.Id;
        }

        foreach (string runtime in new[] { postgres.WorkerConnectionString, postgres.AdminConnectionString })
        {
            await using var context = RuntimeContext(runtime);
            var assessments = new EnrichmentAssessmentQuery(context, context.Titles.Where(x => x.Id == titleId),
                providers: new EnrichmentProviderAvailability(["igdb"], ["igdb"], ["steamgriddb"]));
            var requirements = await assessments.Requirements().ToListAsync();
            requirements.ShouldContain(x => x.Kind == EnrichmentRequirementKind.Identity && x.Subject == "igdb" && x.Cause == "Discover");
            requirements.ShouldContain(x => x.Kind == EnrichmentRequirementKind.Operation && x.SatisfiedBy == "Idle");
            (await assessments.SummariesForCounts().ToListAsync()).ShouldHaveSingleItem().TitleId.ShouldBe(titleId);
        }

        await using (var admin = RuntimeContext(postgres.AdminConnectionString))
        {
            await admin.Titles.Where(x => x.Id == titleId).ExecuteDeleteAsync();
            await admin.Platforms.Where(x => x.Name == "Privilege probe").ExecuteDeleteAsync();
        }
    }

    private static RomdDbContext RuntimeContext(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<RomdDbContext>();
        PostgreSqlConfiguration.Configure(builder, connectionString);
        return new RomdDbContext(builder.Options);
    }

    private static async Task ProvisionAsync(string provisionerConnectionString)
    {
        var provisioner = new HangfireSchemaProvisioner(
            ConnectionConfiguration(
                HangfirePostgreSqlConfiguration.ProvisioningConnectionName,
                provisionerConnectionString),
            NullLogger<HangfireSchemaProvisioner>.Instance);

        await provisioner.StartAsync(CancellationToken.None);
    }

    private static async Task AssertHangfireObjectsOwnedByRomdOwnerAsync(string bootstrapConnectionString)
    {
        await using var connection = new NpgsqlConnection(bootstrapConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.relname, pg_get_userbyid(c.relowner)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'hangfire' AND c.relkind IN ('r', 'S')
            UNION ALL
            SELECT 'schema:hangfire', pg_get_userbyid(nspowner)
            FROM pg_namespace
            WHERE nspname = 'hangfire'
            """;
        var owners = new Dictionary<string, string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                owners[reader.GetString(0)] = reader.GetString(1);
            }
        }

        owners.Keys.ShouldContain("schema:hangfire");
        owners.Keys.ShouldContain("job");
        owners.Keys.ShouldContain("romd_schema");
        owners.Values.Distinct().ShouldBe(["romd_owner"]);
    }

    private static async Task AssertCannotCreateAlterOrDropAsync(string runtimeConnectionString)
    {
        string[] statements =
        [
            "CREATE TABLE hangfire.privilege_probe (id integer)",
            "ALTER TABLE hangfire.job ADD COLUMN privilege_probe integer",
            "DROP TABLE hangfire.romd_schema",
            "CREATE SCHEMA privilege_probe",
            "CREATE TABLE romd.privilege_probe (id integer)",
            "CREATE TABLE public.privilege_probe (id integer)"
        ];
        foreach (string statement in statements)
        {
            await AssertDeniedAsync(runtimeConnectionString, statement);
        }
    }

    private static async Task AssertMeasurementPublicationIsWorkerOnlyAsync(RomdPostgreSqlContainer postgres)
    {
        const string publish = "romd.publish_media_verification(uuid,integer,bytea,bigint,text,text,bytea,integer,text,text,bigint,bytea,bytea,bytea,jsonb)";
        foreach (string runtime in postgres.RuntimeConnectionStrings)
        {
            foreach (string table in new[] { "Verifications", "VerificationOutputs" })
            {
                string qualified = $"romd.\"{table}\"";
                Convert.ToInt32(await ScalarAsync(runtime, $"SELECT count(*) FROM {qualified}")).ShouldBe(0);
                await AssertDeniedAsync(runtime, $"INSERT INTO {qualified} SELECT * FROM {qualified}");
                await AssertDeniedAsync(runtime, $"DELETE FROM {qualified} WHERE false");
                await AssertDeniedAsync(runtime, $"TRUNCATE {qualified}");
            }
            string role = Convert.ToString(await ScalarAsync(runtime, "SELECT current_user"))!;
            Convert.ToBoolean(await ScalarAsync(runtime, $"SELECT has_function_privilege(current_user, '{publish}', 'EXECUTE')"))
                .ShouldBe(role == "romd_worker");
        }
    }

    private static async Task AssertConsumerReadsPublishedVersionOnlyAsync(RomdPostgreSqlContainer postgres)
    {
        Convert.ToInt32(await ScalarAsync(postgres.ConsumerConnectionString, "SELECT version FROM hangfire.romd_schema"))
            .ShouldBe(HangfirePostgreSqlConfiguration.ExpectedSchemaVersion);
        foreach (string runtime in postgres.RuntimeConnectionStrings)
        {
            await AssertDeniedAsync(runtime, "UPDATE hangfire.romd_schema SET version = 99");
        }
        await AssertDeniedAsync(postgres.ConsumerConnectionString, "SELECT count(*) FROM hangfire.job");
    }

    private static async Task AssertRomdSchemaDefaultsKeepConsumerReadOnlyAsync(RomdPostgreSqlContainer postgres)
    {
        await ExecuteAsync(postgres.ProvisionerConnectionString, "CREATE TABLE romd.privilege_probe (id integer PRIMARY KEY)");
        await ExecuteAsync(postgres.WorkerConnectionString, "INSERT INTO romd.privilege_probe (id) VALUES (1)");
        await ExecuteAsync(postgres.AdminConnectionString, "UPDATE romd.privilege_probe SET id = 2");
        Convert.ToInt32(await ScalarAsync(postgres.ConsumerConnectionString, "SELECT count(*) FROM romd.privilege_probe"))
            .ShouldBe(1);
        await AssertDeniedAsync(postgres.ConsumerConnectionString, "INSERT INTO romd.privilege_probe (id) VALUES (3)");
        await AssertDeniedAsync(postgres.ConsumerConnectionString, "DELETE FROM romd.privilege_probe");
    }

    private static async Task AssertReadinessProbeSeesPublishedVersionAsync(string runtimeConnectionString)
    {
        var probe = new PostgreSqlHangfireSchemaProbe(
            ConnectionConfiguration(HangfirePostgreSqlConfiguration.RuntimeConnectionName, runtimeConnectionString));

        (await probe.GetPublishedVersionAsync(CancellationToken.None))
            .ShouldBe(HangfirePostgreSqlConfiguration.ExpectedSchemaVersion);
    }

    private static async Task AssertAdminEnqueueIsExecutedByWorkerAsync(RomdPostgreSqlContainer postgres)
    {
        PostgreSqlStorage workerStorage = CreateRuntimeStorage(postgres.WorkerConnectionString);
        using var server = new BackgroundJobServer(
            new BackgroundJobServerOptions
            {
                WorkerCount = 1,
                Queues = ["default"],
                ServerName = "role-privilege-worker",
                // Other integration tests replace JobActivator.Current with DI-bound activators
                // whose providers are disposed by the time this test runs.
                Activator = new JobActivator()
            },
            workerStorage);
        string jobId = new BackgroundJobClient(CreateRuntimeStorage(postgres.AdminConnectionString))
            .Enqueue(() => RolePrivilegeJobs.RunUnderWorkerRole());

        await WaitForLatestStateAsync(workerStorage, jobId, "Succeeded");
    }

    private static IConfiguration ConnectionConfiguration(string connectionName, string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{connectionName}"] = connectionString
            })
            .Build();

    private static async Task AssertDeniedAsync(string connectionString, string sql)
    {
        PostgresException denied = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connectionString, sql));
        denied.SqlState.ShouldBe(InsufficientPrivilege, sql);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}

public static class RolePrivilegeJobs
{
    [AutomaticRetry(Attempts = 0)]
    public static void RunUnderWorkerRole()
    {
    }
}
