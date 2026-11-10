using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence.MediaFacts;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class MediaContainerFactSchemaTests : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Theory]
    [InlineData("Rvz")]
    [InlineData("Chd")]
    public async Task Verification_MissingCapturedContainerFacts_RejectsPublication(string encoding)
    {
        await using var db = _database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."Files" ("Id","Sha256","Size","SizeOnDisk","IsCompressed","CreatedAt","CreatedByUserId")
                VALUES (1,decode(repeat('aa',32),'hex'),100,100,false,0,'00000000-0000-0000-0000-000000000000');
            INSERT INTO romd."Roms" ("Id","OriginalFilename","ContentSha256","ContentSize","Sha1","Md5","Crc32","CreatedAt","CreatedByUserId")
                VALUES (1,'disc.iso',decode(repeat('bb',32),'hex'),4096,decode(repeat('cc',20),'hex'),decode(repeat('dd',16),'hex'),decode('00000000','hex'),0,'00000000-0000-0000-0000-000000000000');
            INSERT INTO romd."Representations" VALUES (1,1,{encoding});
            INSERT INTO romd."RepresentationOutputs" ("Id","RepresentationId","RomId","ContentSha256","ContentSize","Sha1","Md5","Crc32")
                SELECT 1,1,"Id","ContentSha256","ContentSize","Sha1","Md5","Crc32" FROM romd."Roms";
            UPDATE romd."Roms" SET "PreferredOutputId" = 1;
            INSERT INTO romd."Verifications" ("Id","RepresentationId","ArtifactSha256","ArtifactSize","DecoderBuild","DecoderRid","DecoderUpstreamCommit","DecoderRecipeVersion","CheckedProfile","VerifierBuild","ObservedAtUtcTicks")
                VALUES ('00000000-0000-0000-0000-000000000001',1,decode(repeat('aa',32),'hex'),100,'reader',
                    CASE WHEN {encoding} = 'Chd' THEN 'linux-x64' ELSE NULL END,
                    CASE WHEN {encoding} = 'Chd' THEN decode(repeat('ab',20),'hex') ELSE NULL END,
                    CASE WHEN {encoding} = 'Chd' THEN 2 ELSE NULL END,'checked-output/test','verifier',621355968000000000);
            INSERT INTO romd."VerificationOutputs"
                SELECT '00000000-0000-0000-0000-000000000001',1,"Id","ContentSha256","ContentSize","Sha1","Md5","Crc32" FROM romd."RepresentationOutputs";
            """);
        var error = await Should.ThrowAsync<PostgresException>(() => transaction.CommitAsync());
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Theory]
    [InlineData("Rvz")]
    [InlineData("Chd")]
    public async Task Verification_IndependentParameterFingerprintDisagrees_RejectsPublication(string encoding)
    {
        var error = await Should.ThrowAsync<PostgresException>(() => SeedAsync(encoding, "observed-parameters"));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Theory]
    [InlineData("Rvz")]
    [InlineData("Chd")]
    public async Task ContainerFacts_ExactCapturedGeometryAndIndependentVerification_Publishes(string encoding)
    {
        await SeedAsync(encoding);
        await using var db = _database.CreateContext();
        (await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM romd."Verifications" """).SingleAsync()).ShouldBe(1);
        var retained = (await new MediaFactReader(db).ReadAsync([1]))[1];
        retained.Outputs.Single().Identity.Size.ShouldBe(4096);
        retained.Verifications.Count.ShouldBe(1);
        retained.Container.Encoding.ToString().ShouldBe(encoding);
    }

    [Theory]
    [InlineData("Rvz", "wrong-type")]
    [InlineData("Rvz", "both-types")]
    [InlineData("Rvz", "logical-size")]
    [InlineData("Rvz", "block")]
    [InlineData("Rvz", "managed-integrity")]
    [InlineData("Rvz", "missing-observed-parameters")]
    [InlineData("Chd", "wrong-type")]
    [InlineData("Chd", "both-types")]
    [InlineData("Chd", "logical-size")]
    [InlineData("Chd", "hunk-count")]
    [InlineData("Chd", "unit")]
    [InlineData("Chd", "parent")]
    [InlineData("Chd", "missing-metadata")]
    [InlineData("Chd", "metadata-order")]
    [InlineData("Chd", "metadata-payload")]
    [InlineData("Chd", "metadata-fingerprint")]
    [InlineData("Chd", "missing-integrity")]
    [InlineData("Chd", "missing-observed-parameters")]
    [InlineData("Chd", "raw-integrity")]
    [InlineData("Chd", "overall-integrity")]
    [InlineData("Chd", "raw-output-mismatch")]
    [InlineData("Chd", "metadata-entry-count")]
    [InlineData("Chd", "metadata-payload-size")]
    [InlineData("Chd", "metadata-aggregate-size")]
    [InlineData("Chd", "artifact-too-small")]
    [InlineData("Chd", "metadata-does-not-fit")]
    public async Task ContainerFacts_ContradictoryObservation_RejectsAtomicPublication(string encoding, string mutation)
    {
        var error = await Should.ThrowAsync<PostgresException>(() => SeedAsync(encoding, mutation));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        await using var db = _database.CreateContext();
        (await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM romd."Verifications" """).SingleAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("UPDATE romd.\"ChdContainerParameters\" SET \"Codec0\" = 1")]
    [InlineData("DELETE FROM romd.\"ChdContainerParameters\"")]
    [InlineData("UPDATE romd.\"ChdMetadataEntries\" SET \"Flags\" = 0")]
    [InlineData("DELETE FROM romd.\"ChdMetadataEntries\"")]
    public async Task ContainerFacts_RetainedObservationMutation_Rejects(string sql)
    {
        await SeedAsync("Chd");
        await using var db = _database.CreateContext();
        var error = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task MetadataFingerprint_OrderedBinaryEntries_AgreesWithDomainObservation()
    {
        var captured = new RomContainerFacts.Chd(5, 4096, 4096, 2048, 1, [0, 0, 0, 0], false,
            Sha1.Parse(new string('c', 40)), Sha1.Parse(string.Concat(Enumerable.Repeat("ab", 20))),
            [new RomChdMetadataEntry(uint.MaxValue,255,Enumerable.Range(0,257).Select(value => (byte)value).ToArray()),
             new RomChdMetadataEntry(0x43485432,0,"TRACK:1 TYPE:MODE2_RAW"u8)]);
        await SeedAsync("Chd", captured: captured);
        await using var db = _database.CreateContext();
        var actual = (await new MediaFactReader(db).ReadAsync([1]))[1];
        actual.Container.Fingerprint.ShouldBe(captured.Fingerprint);
    }

    [Theory]
    [InlineData(99, 1)]
    [InlineData(64, 8192)]
    [InlineData(2, 262144)]
    public async Task Metadata_ExactEntryAndAggregateLimits_Publishes(int count, int payloadBytes)
    {
        var entries = Enumerable.Range(0, count).Select(index => new RomChdMetadataEntry((uint)index, 1, new byte[payloadBytes])).ToArray();
        var captured = new RomContainerFacts.Chd(5, 4096, 4096, 2048, 1, [0, 0, 0, 0], false,
            Sha1.Parse(new string('c', 40)), Sha1.Parse(string.Concat(Enumerable.Repeat("ab", 20))), entries);
        await SeedAsync("Chd", captured: captured);
    }

    [Fact]
    public void ContainerMetadataLimit_CoversEverySupportedCdTrack() =>
        RomContainerFacts.MaximumMetadataEntries.ShouldBe(Romd.Media.Cd.ChdCdLimits.MaximumTracks);

    private async Task SeedAsync(string encoding, string? mutation = null, RomContainerFacts.Chd? captured = null)
    {
        await using var db = _database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        long artifactBytes = encoding == "Chd" ? mutation == "artifact-too-small" ? 123 : mutation == "metadata-does-not-fit" ? 140 : 1048576 : 100;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."Files" ("Id","Sha256","Size","SizeOnDisk","IsCompressed","CreatedAt","CreatedByUserId")
                VALUES (1,decode(repeat('aa',32),'hex'),{artifactBytes},{artifactBytes},false,0,'00000000-0000-0000-0000-000000000000');
            INSERT INTO romd."Roms" ("Id","OriginalFilename","ContentSha256","ContentSize","Sha1","Md5","Crc32","CreatedAt","CreatedByUserId")
                VALUES (1,'disc.iso',decode(repeat('bb',32),'hex'),4096,decode(repeat('cc',20),'hex'),decode(repeat('dd',16),'hex'),decode('00000000','hex'),0,'00000000-0000-0000-0000-000000000000');
            INSERT INTO romd."Representations" VALUES (1,1,{encoding});
            INSERT INTO romd."RepresentationOutputs" ("Id","RepresentationId","RomId","ContentSha256","ContentSize","Sha1","Md5","Crc32")
                SELECT 1,1,"Id","ContentSha256","ContentSize","Sha1","Md5","Crc32" FROM romd."Roms";
            UPDATE romd."Roms" SET "PreferredOutputId" = 1;
            """);
        const string rvzSql = """INSERT INTO romd."RvzContainerParameters" VALUES (1,196608,0,5,22,131072,false,4096);""";
        const string chdSql = """
            INSERT INTO romd."ChdContainerParameters" VALUES (1,5,4096,4096,2048,1,0,0,0,0,false,
                decode(repeat('cc',20),'hex'),decode(repeat('ab',20),'hex'),sha256(decode('44564420010000000100','hex')));
            INSERT INTO romd."ChdMetadataEntries" VALUES (1,0,1146504224,1,decode('00','hex'));
            """;
        string parameters = encoding == "Rvz" ? rvzSql : chdSql;
        parameters = mutation switch
        {
            "wrong-type" => encoding == "Rvz" ? chdSql : rvzSql,
            "both-types" => rvzSql + chdSql,
            "logical-size" => parameters.Replace("false,4096)", "false,8192)").Replace("1,5,4096,4096,2048,1", "1,5,8192,4096,2048,2"),
            "block" => parameters.Replace("131072", "100000"),
            "hunk-count" => parameters.Replace("1,5,4096,4096,2048,1", "1,5,4096,4096,2048,2"),
            "unit" => parameters.Replace("4096,4096,2048", "4096,4096,2448"),
            "parent" => parameters.Replace("0,0,0,0,false", "0,0,0,0,true"),
            "missing-metadata" => parameters.Replace("INSERT INTO romd.\"ChdMetadataEntries\" VALUES (1,0,1146504224,1,decode('00','hex'));", ""),
            "metadata-order" => parameters.Replace("VALUES (1,0,1146504224", "VALUES (1,1,1146504224"),
            "metadata-entry-count" => parameters.Replace("VALUES (1,0,1146504224", "VALUES (1,99,1146504224"),
            "metadata-payload-size" => parameters.Replace("1146504224,1,decode('00','hex')", "1146504224,1,decode(repeat('ab',262145),'hex')"),
            "metadata-aggregate-size" => """
                WITH entries AS (SELECT n,decode(repeat('ab',262144),'hex') AS payload FROM generate_series(0,2) n)
                INSERT INTO romd."ChdContainerParameters"
                SELECT 1,5,4096,4096,2048,1,0,0,0,0,false,decode(repeat('cc',20),'hex'),decode(repeat('ab',20),'hex'),
                    sha256(string_agg(decode('4456442001','hex') || int4send(octet_length(payload)) || payload,decode('','hex') ORDER BY n)) FROM entries;
                INSERT INTO romd."ChdMetadataEntries" SELECT 1,n,1146504224,1,decode(repeat('ab',262144),'hex') FROM generate_series(0,2) n;
                """,
            "metadata-payload" => parameters.Replace("1146504224,1,decode('00','hex')", "1146504224,1,decode('ff','hex')"),
            "metadata-fingerprint" => parameters.Replace("sha256(decode('44564420010000000100','hex'))", "sha256(decode('','hex'))"),
            "raw-output-mismatch" => parameters.Replace("repeat('cc',20)", "repeat('cd',20)"),
            _ => parameters
        };
        if (captured is null)
            await db.Database.ExecuteSqlRawAsync(parameters);
        else
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."ChdContainerParameters" VALUES (1,{(int)captured.Version},{captured.LogicalBytes},
                    {(int)captured.HunkBytes},{(int)captured.UnitBytes},{(long)captured.HunkCount},
                    {(long)captured.Codecs[0]},{(long)captured.Codecs[1]},{(long)captured.Codecs[2]},{(long)captured.Codecs[3]},
                    {captured.HasParent},{captured.StoredRawSha1.ToArray()},{captured.StoredOverallSha1.ToArray()},{captured.MetadataSha256.ToArray()});
                """);
            for (int ordinal = 0; ordinal < captured.Metadata.Count; ordinal++)
            {
                var entry = captured.Metadata[ordinal];
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO romd."ChdMetadataEntries" VALUES (1,{ordinal},{(long)entry.Tag},{(int)entry.Flags},{entry.Payload.ToArray()});
                    """);
            }
        }
        bool native = encoding == "Chd";
        byte[]? raw = native && mutation != "missing-integrity"
            ? Enumerable.Repeat(mutation is "raw-integrity" or "raw-output-mismatch" ? (byte)0xcd : (byte)0xcc, 20).ToArray() : null;
        byte[]? overall = native && mutation != "missing-integrity"
            ? Enumerable.Repeat(mutation == "overall-integrity" ? (byte)0xac : (byte)0xab, 20).ToArray() : null;
        if (mutation == "managed-integrity")
            raw = overall = Enumerable.Repeat((byte)1, 20).ToArray();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."Verifications" ("Id","RepresentationId","ArtifactSha256","ArtifactSize","DecoderBuild","DecoderRid","DecoderUpstreamCommit","DecoderRecipeVersion","CheckedProfile","VerifierBuild","ObservedAtUtcTicks","ComputedRawSha1","ComputedOverallSha1","ObservedParametersSha256")
                VALUES ('00000000-0000-0000-0000-000000000001',1,decode(repeat('aa',32),'hex'),{artifactBytes},'reader',
                    CASE WHEN {native} THEN 'linux-x64' ELSE NULL END,
                    CASE WHEN {native} THEN decode(repeat('ab',20),'hex') ELSE NULL END,
                    CASE WHEN {native} THEN 2 ELSE NULL END,'checked-output/test','verifier',621355968000000000,{raw},{overall},
                    CASE WHEN {mutation == "observed-parameters"} THEN decode(repeat('de',32),'hex')
                        WHEN {mutation == "missing-observed-parameters"} THEN NULL ELSE romd.media_parameter_fingerprint(1) END);
            INSERT INTO romd."VerificationOutputs"
                SELECT '00000000-0000-0000-0000-000000000001',1,"Id","ContentSha256","ContentSize","Sha1","Md5","Crc32" FROM romd."RepresentationOutputs";
            """);
        await transaction.CommitAsync();
    }
}
