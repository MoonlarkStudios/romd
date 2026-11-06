using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Rom;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Romd.Persistence;
using Romd.Domain.Source.Rom;
using Romd.Domain.Hashing;
using Romd.Persistence.MediaFacts;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class MediaFactPublicationPrivilegeTests
{
    [Fact]
    public async Task Publication_ProductionRoles_OnlyWorkerProcedureCanAppendCompleteMeasurements()
    {
        await using var postgres = await RomdPostgreSqlContainer.StartWithRomdRolesAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{PostgreSqlConfiguration.ProvisioningConnectionName}"] = postgres.ProvisionerConnectionString
        }).Build();
        await new PostgreSqlSchemaProvisioner(configuration, NullLogger<PostgreSqlSchemaProvisioner>.Instance).StartAsync(default);
        await ExecuteAsync(postgres.WorkerConnectionString, """
            BEGIN;
            INSERT INTO romd."Files" ("Id","Sha256","Size","SizeOnDisk","IsCompressed","CreatedAt","CreatedByUserId")
                VALUES (1,decode(repeat('aa',32),'hex'),100,100,false,0,'00000000-0000-0000-0000-000000000000');
            INSERT INTO romd."Roms" ("Id","OriginalFilename","ContentSha256","ContentSize","Sha1","Md5","Crc32","CreatedAt","CreatedByUserId")
                VALUES (1,'disc.iso',decode(repeat('bb',32),'hex'),100,decode(repeat('cc',20),'hex'),decode(repeat('dd',16),'hex'),decode('00000000','hex'),0,'00000000-0000-0000-0000-000000000000');
            INSERT INTO romd."Representations" VALUES (1,1,'Rvz');
            INSERT INTO romd."RvzContainerParameters" VALUES (1,196608,0,5,22,131072,false,100);
            INSERT INTO romd."RepresentationOutputs" ("Id","RepresentationId","RomId","ContentSha256","ContentSize","Sha1","Md5","Crc32")
                SELECT 1,1,"Id","ContentSha256","ContentSize","Sha1","Md5","Crc32" FROM romd."Roms" WHERE "Id" = 1;
            UPDATE romd."Roms" SET "PreferredOutputId" = 1 WHERE "Id" = 1;
            COMMIT;
            """);

        foreach (string runtime in postgres.RuntimeConnectionStrings)
        {
            foreach (string sql in new[]
            {
                """INSERT INTO romd."Verifications" SELECT * FROM romd."Verifications" WHERE false""",
                """UPDATE romd."Verifications" SET "VerifierBuild" = 'changed' WHERE false""",
                """DELETE FROM romd."Verifications" WHERE false""",
                """INSERT INTO romd."VerificationOutputs" SELECT * FROM romd."VerificationOutputs" WHERE false""",
                """UPDATE romd."VerificationOutputs" SET "ContentSize" = 0 WHERE false""",
                """DELETE FROM romd."VerificationOutputs" WHERE false""",
                """TRUNCATE romd."Verifications" CASCADE"""
            })
            {
                var denied = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(runtime, sql));
                denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            }
        }
        foreach (string runtime in new[] { postgres.AdminConnectionString, postgres.ConsumerConnectionString })
        {
            var denied = await Should.ThrowAsync<PostgresException>(() => PublishAsync(runtime, Guid.NewGuid(), "reader", Outputs()));
            denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
        foreach (string runtime in new[] { postgres.AdminConnectionString, postgres.ConsumerConnectionString })
            foreach (string table in new[] { "ChdContainerParameters", "ChdMetadataEntries", "RvzContainerParameters" })
                foreach (string sql in new[]
                {
            $"INSERT INTO romd.\"{table}\" SELECT * FROM romd.\"{table}\" WHERE false",
            $"UPDATE romd.\"{table}\" SET \"RepresentationId\" = \"RepresentationId\" WHERE false",
            $"DELETE FROM romd.\"{table}\" WHERE false",
            $"TRUNCATE romd.\"{table}\" CASCADE"
        })
                {
                    var denied = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(runtime, sql));
                    denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
                }
        foreach (string mutation in new[] { "empty", "unknown", "missing", "null", "wrong-type", "bad-hash", "wrong-hash", "wrong-size", "duplicate-output" })
        {
            string payload = MutateOutputs(mutation);
            var rejected = await Should.ThrowAsync<PostgresException>(() => PublishAsync(postgres.WorkerConnectionString,
                Guid.NewGuid(), "invalid-reader", payload));
            rejected.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            (await CountAsync(postgres.WorkerConnectionString)).ShouldBe(0);
        }
        Guid first = Guid.NewGuid();
        (await PublishAsync(postgres.WorkerConnectionString, first, "reader-a", Outputs())).ShouldBe(first);
        await PublishAsync(postgres.WorkerConnectionString, Guid.NewGuid(), "reader-b", Outputs());
        foreach (string runtime in postgres.RuntimeConnectionStrings)
            (await CountAsync(runtime)).ShouldBe(2);

        await ExecuteAsync(postgres.WorkerConnectionString, """
            BEGIN;
            INSERT INTO romd."Files" ("Id","Sha256","Size","SizeOnDisk","IsCompressed","CreatedAt","CreatedByUserId")
                VALUES (2,decode(repeat('ee',32),'hex'),1048576,1048576,false,0,'00000000-0000-0000-0000-000000000000');
            INSERT INTO romd."Roms" ("Id","OriginalFilename","ContentSha256","ContentSize","Sha1","Md5","Crc32","CreatedAt","CreatedByUserId")
                VALUES (2,'dvd.iso',decode(repeat('ff',32),'hex'),4096,decode(repeat('cc',20),'hex'),decode(repeat('dd',16),'hex'),decode('00000000','hex'),0,'00000000-0000-0000-0000-000000000000');
            INSERT INTO romd."Representations" VALUES (2,2,'Chd');
            INSERT INTO romd."ChdContainerParameters" VALUES (2,5,4096,4096,2048,1,0,0,0,0,false,
                decode(repeat('cc',20),'hex'),decode(repeat('ab',20),'hex'),sha256(decode('44564420010000000100','hex')));
            INSERT INTO romd."ChdMetadataEntries" VALUES (2,0,1146504224,1,decode('00','hex'));
            INSERT INTO romd."RepresentationOutputs" ("Id","RepresentationId","RomId","ContentSha256","ContentSize","Sha1","Md5","Crc32")
                SELECT 2,2,"Id","ContentSha256","ContentSize","Sha1","Md5","Crc32" FROM romd."Roms" WHERE "Id" = 2;
            UPDATE romd."Roms" SET "PreferredOutputId" = 2 WHERE "Id" = 2;
            COMMIT;
            """);
        byte[] raw = Enumerable.Repeat((byte)0xcc, 20).ToArray();
        byte[] overall = Enumerable.Repeat((byte)0xab, 20).ToArray();
        foreach (var (badRaw, badOverall) in new (byte[]?, byte[]?)[] { (null, null), (raw, raw), (overall, overall) })
        {
            var rejected = await Should.ThrowAsync<PostgresException>(() => PublishNativeAsync(postgres.WorkerConnectionString,
                "linux-x64", badRaw, badOverall));
            rejected.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            (await CountAsync(postgres.WorkerConnectionString)).ShouldBe(2);
        }
        await PublishNativeAsync(postgres.WorkerConnectionString, "linux-x64", raw, overall);
        await PublishNativeAsync(postgres.WorkerConnectionString, "linux-arm64", raw, overall);
        foreach (string runtime in postgres.RuntimeConnectionStrings)
            (await CountAsync(runtime)).ShouldBe(4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publisher_ProductionWorkerRole_PublishesGraphReadableByEveryRuntime(bool native)
    {
        await using var postgres = await RomdPostgreSqlContainer.StartWithRomdRolesAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{PostgreSqlConfiguration.ProvisioningConnectionName}"] = postgres.ProvisionerConnectionString
        }).Build();
        await new PostgreSqlSchemaProvisioner(configuration, NullLogger<PostgreSqlSchemaProvisioner>.Instance).StartAsync(default);
        await ExecuteAsync(postgres.WorkerConnectionString, """
            INSERT INTO romd."Files" ("Id","Sha256","Size","SizeOnDisk","IsCompressed","CreatedAt","CreatedByUserId")
                VALUES (1,decode(repeat('bb',32),'hex'),1024,1024,false,0,'00000000-0000-0000-0000-000000000000');
            """);
        var logical = new RomLogicalIdentity(4096, Sha256.Parse(new string('a', 64)), Sha1.Parse(new string('a', 40)),
            Md5.Parse(new string('a', 32)), Crc32.FromUInt32(0));
        RomContainerFacts container = native
            ? new RomContainerFacts.Chd(5, 4096, 4096, 2048, 1, [0, 0, 0, 0], false, logical.Sha1, Sha1.Parse(new string('f', 40)), [new(0x44564420, 1, [0])])
            : new RomContainerFacts.Rvz(0x30000, 1, 5, 22, 131072, false, 4096);
        RomRepresentationVerification Measurement(string build) => new(Guid.NewGuid(), Sha256.Parse(new string('b', 64)), 1024,
            native ? new RomDecoderIdentity.Native(new(build == "first" ? "linux-x64" : "linux-arm64", Sha1.Parse(new string('e', 40)), 1))
                : new RomDecoderIdentity.Managed(build), "measured/test", "verifier/test", DateTimeOffset.UnixEpoch.AddTicks(12345),
            [new(RomOutputSelector.Whole.Instance, logical)], container, (container as RomContainerFacts.Chd)?.StoredRawSha1, (container as RomContainerFacts.Chd)?.StoredOverallSha1);
        int representationId;
        await using (var db = Context(postgres.WorkerConnectionString))
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var publisher = new MediaFactPublisher(db);
            int romId = await publisher.CreateLogicalAsync("disc.iso", logical, Guid.Empty, DateTimeOffset.UnixEpoch);
            var published = await publisher.PublishAsync(1, container, RomReconstructionManifest.WholeFile.Instance, Measurement("first"),
                [new MediaOutputOwner(RomOutputSelector.Whole.Instance, romId, true)], []);
            representationId = published.Id;
            (await publisher.AppendVerificationAsync(representationId, Measurement("second"))).ShouldBeTrue();
            await transaction.CommitAsync();
        }
        foreach (string runtime in postgres.RuntimeConnectionStrings)
        {
            await using var db = Context(runtime);
            var facts = (await new MediaFactReader(db).ReadAsync([representationId]))[representationId];
            facts.Verifications.Count.ShouldBe(2);
            facts.Container.Fingerprint.ShouldBe(container.Fingerprint);
            facts.Outputs.Single().Identity.ShouldBe(logical);
        }
    }

    private static RomdDbContext Context(string connectionString)
    {
        var options = new DbContextOptionsBuilder<RomdDbContext>();
        PostgreSqlConfiguration.Configure(options, connectionString);
        return new(options.Options);
    }

    private static string Outputs(int outputId = 1, int size = 100, char hash = 'b') => new JsonArray(new JsonObject
    {
        ["OutputId"] = outputId,
        ["ContentSha256"] = new string(hash, 64),
        ["ContentSize"] = size,
        ["Sha1"] = new string('c', 40),
        ["Md5"] = new string('d', 32),
        ["Crc32"] = "00000000"
    }).ToJsonString();

    private static string MutateOutputs(string mutation)
    {
        var array = JsonNode.Parse(Outputs())!.AsArray();
        var output = array[0]!.AsObject();
        switch (mutation)
        {
            case "empty":
                array.Clear();
                break;
            case "unknown":
                output["Unknown"] = 1;
                break;
            case "missing":
                output.Remove("Sha1");
                break;
            case "null":
                output["Sha1"] = null;
                break;
            case "wrong-type":
                output["ContentSize"] = "100";
                break;
            case "bad-hash":
                output["ContentSha256"] = "invalid";
                break;
            case "wrong-hash":
                output["ContentSha256"] = new string('e', 64);
                break;
            case "wrong-size":
                output["ContentSize"] = 101;
                break;
            case "duplicate-output":
                array.Add(output.DeepClone());
                break;
        }
        return array.ToJsonString();
    }

    private static async Task<Guid> PublishAsync(string connectionString, Guid id, string build, string outputs)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT romd.publish_media_verification(@id,1,decode(repeat('aa',32),'hex'),100,@build,
                NULL,NULL,NULL,'checked-output/test','verifier',621355968000000000,@parameters,NULL,NULL,@outputs)
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("build", build);
        command.Parameters.AddWithValue("parameters", new RomContainerFacts.Rvz(0x30000, 0, 5, 22, 131072, false, 100).Fingerprint!.Value.ToArray());
        command.Parameters.AddWithValue("outputs", NpgsqlDbType.Jsonb, outputs);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private static async Task PublishNativeAsync(string connectionString, string rid, byte[]? raw, byte[]? overall)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT romd.publish_media_verification(@id,2,decode(repeat('ee',32),'hex'),1048576,'native-reader',
                @rid,decode(repeat('ab',20),'hex'),2,'checked-output/test','verifier',621355968000000000,@parameters,@raw,@overall,@outputs)
            """;
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("rid", rid);
        var observed = new RomContainerFacts.Chd(5, 4096, 4096, 2048, 1, [0, 0, 0, 0], false,
            Sha1.Parse(new string('c', 40)), Sha1.Parse(string.Concat(Enumerable.Repeat("ab", 20))), [new RomChdMetadataEntry(0x44564420, 1, [0])]);
        command.Parameters.AddWithValue("parameters", observed.Fingerprint!.Value.ToArray());
        command.Parameters.Add("raw", NpgsqlDbType.Bytea).Value = (object?)raw ?? DBNull.Value;
        command.Parameters.Add("overall", NpgsqlDbType.Bytea).Value = (object?)overall ?? DBNull.Value;
        command.Parameters.AddWithValue("outputs", NpgsqlDbType.Jsonb, Outputs(2, 4096, 'f'));
        await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT count(*) FROM romd."Verifications" """;
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
