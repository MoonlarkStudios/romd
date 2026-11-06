using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Npgsql;
using Romd.Persistence;
using Romd.Persistence.MediaFacts;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class MediaFactSchemaTests : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    public async Task Verification_TwoManagedBuilds_RetainsBothMeasurements()
    {
        await SeedWholeAsync("Rvz");
        await VerifyAsync("reader-a");
        await VerifyAsync("reader-b");
        await using var db = _database.CreateContext();
        (await VerificationCountAsync(db)).ShouldBe(2);
        (await VerificationOutputCountAsync(db)).ShouldBe(2);
        var retained = (await new MediaFactReader(db).ReadAsync([1]))[1];
        retained.Verifications.Count.ShouldBe(2);
        retained.Verifications.Select(proof => proof.Decoder.Key).Distinct().Count().ShouldBe(2);
    }

    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    public async Task Verification_NativeBuild_StoresFactsWithoutRuntimeQualificationPolicy(string rid)
    {
        await SeedWholeAsync("Chd");
        await VerifyAsync("native-reader", rid);
        await using var db = _database.CreateContext();
        (await VerificationCountAsync(db)).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verification_RepeatedDecoderBuild_RejectsDuplicate(bool native)
    {
        await SeedWholeAsync(native ? "Chd" : "Rvz");
        await VerifyAsync("reader", native ? "linux-x64" : null);
        var error = await Should.ThrowAsync<PostgresException>(() => VerifyAsync(native ? "different-display-label" : "reader", native ? "linux-x64" : null));
        error.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Theory]
    [InlineData("artifact-hash")]
    [InlineData("artifact-size")]
    [InlineData("output-hash")]
    [InlineData("output-size")]
    [InlineData("missing-output")]
    [InlineData("native-with-managed-encoding")]
    [InlineData("chd-without-native-build")]
    [InlineData("partial-native-build")]
    [InlineData("blank-profile")]
    [InlineData("control-profile")]
    public async Task Verification_ContradictoryOrIncompleteMeasurement_Rejects(string mutation)
    {
        await SeedWholeAsync(mutation == "chd-without-native-build" ? "Chd" : "Rvz");
        var error = await Should.ThrowAsync<PostgresException>(() => VerifyAsync("reader", mutation: mutation));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        await using var db = _database.CreateContext();
        (await VerificationCountAsync(db)).ShouldBe(0);
    }

    [Theory]
    [InlineData("UPDATE romd.\"Roms\" SET \"ContentSize\" = 101")]
    [InlineData("UPDATE romd.\"Representations\" SET \"FileId\" = 2")]
    [InlineData("UPDATE romd.\"RepresentationOutputs\" SET \"ContentSize\" = 101")]
    [InlineData("UPDATE romd.\"Verifications\" SET \"VerifierBuild\" = 'changed'")]
    [InlineData("UPDATE romd.\"VerificationOutputs\" SET \"ContentSize\" = 101")]
    [InlineData("DELETE FROM romd.\"Verifications\"")]
    [InlineData("DELETE FROM romd.\"VerificationOutputs\"")]
    [InlineData("UPDATE romd.\"Files\" SET \"Size\" = 101 WHERE \"Id\" = 1")]
    public async Task VerifiedFacts_AlterationWhileRetained_Rejects(string sql)
    {
        await SeedWholeAsync("Rvz");
        await VerifyAsync("reader");
        await using var db = _database.CreateContext();
        var error = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Representation_LogicalWithdrawal_RetainsProofUntilArtifactOwnerIsDeleted()
    {
        await SeedWholeAsync("Rvz");
        await VerifyAsync("reader");
        await using var db = _database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM romd."Roms";""");
        (await VerificationCountAsync(db)).ShouldBe(1);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM romd."Representations";""");
        (await VerificationCountAsync(db)).ShouldBe(0);
        (await VerificationOutputCountAsync(db)).ShouldBe(0);
        (await db.Files.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task PreferredOutput_OtherLogicalOwner_RejectsAtCommit()
    {
        await SeedWholeAsync("Rvz");
        await using var db = _database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO romd."Roms" ("Id", "OriginalFilename", "ContentSha256", "ContentSize", "Sha1", "Md5", "Crc32", "PreferredOutputId", "CreatedAt", "CreatedByUserId")
            VALUES (2, 'other.iso', decode(repeat('dd',32),'hex'),100, decode(repeat('ee',20),'hex'),decode(repeat('ff',16),'hex'),decode('00000000','hex'),1,0,'00000000-0000-0000-0000-000000000000');
            """);
        var error = await Should.ThrowAsync<PostgresException>(() => transaction.CommitAsync());
        error.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task CdTracks_CompletePublication_RetainsManifestAndOnlyDecodedOutputs()
    {
        await SeedCdAsync();
        await using var db = _database.CreateContext();
        (await VerificationCountAsync(db)).ShouldBe(1);
        (await VerificationOutputCountAsync(db)).ShouldBe(1);
        (await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM romd."ReconstructionMembers" """).SingleAsync()).ShouldBe(2);
        var retained = (await new MediaFactReader(db).ReadAsync([1]))[1];
        retained.Manifest.ShouldBeOfType<Romd.Domain.Source.Rom.RomReconstructionManifest.CdTracks>();
        retained.Outputs.Single().Selector.ShouldBeOfType<Romd.Domain.Source.Rom.RomOutputSelector.Member>();
        retained.Descriptors.Single().FileId.ShouldBe(2);
    }

    [Theory]
    [InlineData("track-placement")]
    [InlineData("missing-track-owner")]
    [InlineData("member-size")]
    [InlineData("missing-cue")]
    [InlineData("descriptor-identity")]
    [InlineData("unsafe-path")]
    [InlineData("descriptor-output")]
    [InlineData("missing-proof")]
    [InlineData("null-sidecar-role")]
    public async Task CdTracks_IncompleteOrContradictoryGraph_RejectsAtPublication(string mutation)
    {
        var error = await Should.ThrowAsync<PostgresException>(() => SeedCdAsync(mutation));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Theory]
    [InlineData("UPDATE romd.\"ReconstructionManifests\" SET \"LogicalFrames\" = 40")]
    [InlineData("UPDATE romd.\"ReconstructionTracks\" SET \"Frames\" = 1 WHERE \"Number\" = 1")]
    [InlineData("UPDATE romd.\"ReconstructionMembers\" SET \"Path\" = 'changed.bin' WHERE \"Ordinal\" = 0")]
    [InlineData("DELETE FROM romd.\"ReconstructionMemberTracks\" WHERE \"TrackNumber\" = 2")]
    [InlineData("INSERT INTO romd.\"ReconstructionTracks\" VALUES (1,3,36,4,'Audio')")]
    [InlineData("UPDATE romd.\"Files\" SET \"Size\" = 11 WHERE \"Id\" = 2")]
    public async Task CdTracks_PublishedLayoutMutation_Rejects(string sql)
    {
        await SeedCdAsync();
        await using var db = _database.CreateContext();
        var error = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Output_ContradictsItsLogicalRom_RejectsAtCommit()
    {
        await SeedWholeAsync("Rvz");
        await using var db = _database.CreateContext();
        var error = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            """UPDATE romd."RepresentationOutputs" SET "ContentSize" = 101;"""));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Verification_ConcurrentArtifactMutation_SerializesAndPreservesConsistentFacts(bool verificationFirst)
    {
        await SeedWholeAsync("Rvz");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        await using var verifier = _database.CreateContext();
        await using var verifierTransaction = await verifier.Database.BeginTransactionAsync(ct);
        await using var writer = _database.CreateContext();
        await using var writerTransaction = await writer.Database.BeginTransactionAsync(ct);
        Task pending;
        if (verificationFirst)
        {
            await VerifyCoreAsync(verifier, "reader", ct: ct);
            int pid = await writer.OpenBackendProcessIdAsync(ct);
            pending = writer.Database.ExecuteSqlRawAsync("""UPDATE romd."Files" SET "Size" = 101 WHERE "Id" = 1;""", ct);
            try
            {
                (await _database.WaitsForALockAsync(pid, pending, ct)).ShouldBeTrue();
                await verifierTransaction.CommitAsync(ct);
                var error = await Should.ThrowAsync<PostgresException>(() => pending);
                error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            }
            finally
            {
                await verifierTransaction.DisposeAsync();
                try
                { await pending; }
                catch (Exception) { /* Observe the pending writer during failure cleanup. */ }
            }
        }
        else
        {
            await writer.Database.ExecuteSqlRawAsync("""UPDATE romd."Files" SET "Size" = 101 WHERE "Id" = 1;""", ct);
            int pid = await verifier.OpenBackendProcessIdAsync(ct);
            pending = VerifyCoreAsync(verifier, "reader", ct: ct);
            try
            {
                (await _database.WaitsForALockAsync(pid, pending, ct)).ShouldBeTrue();
                await writerTransaction.CommitAsync(ct);
                var error = await Should.ThrowAsync<PostgresException>(() => pending);
                error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            }
            finally
            {
                await writerTransaction.DisposeAsync();
                try
                { await pending; }
                catch (Exception) { /* Observe the pending verifier during failure cleanup. */ }
            }
        }
        await using var observer = _database.CreateContext();
        (await VerificationCountAsync(observer)).ShouldBe(verificationFirst ? 1 : 0);
        (await observer.Files.SingleAsync()).Size.ShouldBe(verificationFirst ? 100 : 101);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CdPublication_ConcurrentDescriptorMutation_SerializesExactRetainedBytes(bool verificationFirst)
    {
        await using (var setup = _database.CreateContext())
            await setup.Database.ExecuteSqlRawAsync("""
                INSERT INTO romd."Files" ("Id","Sha256","Size","SizeOnDisk","IsCompressed","CreatedAt","CreatedByUserId")
                    VALUES (2,decode(repeat('cc',32),'hex'),10,10,false,0,'00000000-0000-0000-0000-000000000000');
                """);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        await using var verifier = _database.CreateContext();
        await using var verifierTransaction = await verifier.Database.BeginTransactionAsync(ct);
        await using var writer = _database.CreateContext();
        await using var writerTransaction = await writer.Database.BeginTransactionAsync(ct);
        Task pending;
        if (verificationFirst)
        {
            await SeedCdCoreAsync(verifier, existingDescriptor: true);
            int pid = await writer.OpenBackendProcessIdAsync(ct);
            pending = writer.Database.ExecuteSqlRawAsync("""UPDATE romd."Files" SET "Size" = 11 WHERE "Id" = 2;""", ct);
            try
            {
                (await _database.WaitsForALockAsync(pid, pending, ct)).ShouldBeTrue();
                await verifierTransaction.CommitAsync(ct);
                var error = await Should.ThrowAsync<PostgresException>(() => pending);
                error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            }
            finally
            {
                await verifierTransaction.DisposeAsync();
                try
                { await pending; }
                catch (Exception) { /* Observe the pending writer during cleanup. */ }
            }
        }
        else
        {
            await writer.Database.ExecuteSqlRawAsync("""UPDATE romd."Files" SET "Size" = 11 WHERE "Id" = 2;""", ct);
            int pid = await verifier.OpenBackendProcessIdAsync(ct);
            pending = PublishAsync();
            async Task PublishAsync()
            {
                await SeedCdCoreAsync(verifier, existingDescriptor: true);
                await verifierTransaction.CommitAsync(ct);
            }
            try
            {
                (await _database.WaitsForALockAsync(pid, pending, ct)).ShouldBeTrue();
                await writerTransaction.CommitAsync(ct);
                var error = await Should.ThrowAsync<PostgresException>(() => pending);
                error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            }
            finally
            {
                await writerTransaction.DisposeAsync();
                try
                { await pending; }
                catch (Exception) { /* Observe the pending publication during cleanup. */ }
            }
        }
        await using var observer = _database.CreateContext();
        (await VerificationCountAsync(observer)).ShouldBe(verificationFirst ? 1 : 0);
        (await observer.Files.SingleAsync(row => row.Id == 2)).Size.ShouldBe(verificationFirst ? 10 : 11);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OutputReattachment_ConcurrentLogicalMutation_SerializesVerifiedIdentity(bool attachmentFirst)
    {
        await SeedWholeAsync("Rvz");
        await VerifyAsync("reader");
        await using (var setup = _database.CreateContext())
        {
            await using var setupTransaction = await setup.Database.BeginTransactionAsync();
            await setup.Database.ExecuteSqlRawAsync("""
                INSERT INTO romd."Files" ("Id","Sha256","Size","SizeOnDisk","IsCompressed","CreatedAt","CreatedByUserId")
                    VALUES (2,decode(repeat('bb',32),'hex'),100,100,false,0,'00000000-0000-0000-0000-000000000000');
                INSERT INTO romd."Representations" VALUES (2,2,'Identity');
                INSERT INTO romd."RepresentationOutputs" ("Id","RepresentationId","RomId","ContentSha256","ContentSize","Sha1","Md5","Crc32")
                    SELECT 2,2,"Id","ContentSha256","ContentSize","Sha1","Md5","Crc32" FROM romd."Roms";
                UPDATE romd."Roms" SET "PreferredOutputId" = 2;
                UPDATE romd."RepresentationOutputs" SET "RomId" = NULL WHERE "Id" = 1;
                """);
            await setupTransaction.CommitAsync();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        await using var attachment = _database.CreateContext();
        await using var attachmentTransaction = await attachment.Database.BeginTransactionAsync(ct);
        await using var writer = _database.CreateContext();
        await using var writerTransaction = await writer.Database.BeginTransactionAsync(ct);
        const string attachSql = """
            UPDATE romd."RepresentationOutputs" SET "RomId" = 1 WHERE "Id" = 1;
            SET CONSTRAINTS ALL IMMEDIATE;
            """;
        const string writeSql = """
            UPDATE romd."Roms" SET "ContentSize" = 101 WHERE "Id" = 1;
            UPDATE romd."RepresentationOutputs" SET "ContentSize" = 101 WHERE "Id" = 2;
            UPDATE romd."Files" SET "Size" = 101 WHERE "Id" = 2;
            SET CONSTRAINTS ALL IMMEDIATE;
            """;
        Task pending;
        if (attachmentFirst)
        {
            await attachment.Database.ExecuteSqlRawAsync(attachSql, ct);
            int pid = await writer.OpenBackendProcessIdAsync(ct);
            pending = writer.Database.ExecuteSqlRawAsync(writeSql, ct);
            try
            {
                (await _database.WaitsForALockAsync(pid, pending, ct)).ShouldBeTrue();
                await attachmentTransaction.CommitAsync(ct);
                var error = await Should.ThrowAsync<PostgresException>(() => pending);
                error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            }
            finally
            {
                await attachmentTransaction.DisposeAsync();
                try
                { await pending; }
                catch (Exception) { /* Observe pending mutation during cleanup. */ }
            }
        }
        else
        {
            await writer.Database.ExecuteSqlRawAsync(writeSql, ct);
            int pid = await attachment.OpenBackendProcessIdAsync(ct);
            pending = attachment.Database.ExecuteSqlRawAsync(attachSql, ct);
            try
            {
                (await _database.WaitsForALockAsync(pid, pending, ct)).ShouldBeTrue();
                await writerTransaction.CommitAsync(ct);
                var error = await Should.ThrowAsync<PostgresException>(() => pending);
                error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            }
            finally
            {
                await writerTransaction.DisposeAsync();
                try
                { await pending; }
                catch (Exception) { /* Observe pending attachment during cleanup. */ }
            }
        }
        await using var observer = _database.CreateContext();
        (await VerificationCountAsync(observer)).ShouldBe(1);
        (await observer.Database.SqlQueryRaw<long>("""SELECT "ContentSize" AS "Value" FROM romd."Roms" WHERE "Id" = 1""").SingleAsync())
            .ShouldBe(attachmentFirst ? 100 : 101);
        (await observer.Database.SqlQueryRaw<int?>("""SELECT "RomId" AS "Value" FROM romd."RepresentationOutputs" WHERE "Id" = 1""").SingleAsync())
            .ShouldBe(attachmentFirst ? 1 : null);
    }

    [Theory]
    [InlineData("é", "É")]
    [InlineData("σ", "ς")]
    [InlineData("𐐀", "𐐨")]
    [InlineData("ı", "I")]
    [InlineData("ſ", "S")]
    [InlineData("ß", "ẞ")]
    [InlineData("ß", "ss")]
    public async Task MemberPaths_UnicodeCasing_MatchesPortableOrdinalComparison(string left, string right)
    {
        await using var db = _database.CreateContext();
        bool equal = await db.Database.SqlQuery<bool>($"""
            SELECT romd.media_member_path_key({left}) = romd.media_member_path_key({right}) AS "Value"
            """).SingleAsync();
        equal.ShouldBe(StringComparer.OrdinalIgnoreCase.Equals(left, right));
    }

    [Fact]
    public async Task MemberPaths_UnicodeCaseCollision_RejectsAtInsertion()
    {
        var error = await Should.ThrowAsync<PostgresException>(() => SeedCdAsync("unicode-path-collision"));
        error.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Representation_SameArtifactDifferentOriginalLayouts_RetainsIndependentOwnershipAndProof()
    {
        await SeedCdAsync();
        await using var db = _database.CreateContext();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await SeedAlternateCdCoreAsync(db);
            await VerifyCoreAsync(db, "same-decoder-new-layout", "linux-x64", representationId: 2);
            await transaction.CommitAsync();
        }
        var both = await new MediaFactReader(db).ReadAsync([1, 2]);
        both.Count.ShouldBe(2);
        both[1].FileId.ShouldBe(both[2].FileId);
        both[1].Descriptors.Single().FileId.ShouldBe(2);
        both[2].Descriptors.Single().FileId.ShouldBe(3);
        both[1].Verifications.Single().Id.ShouldNotBe(both[2].Verifications.Single().Id);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE romd."Roms" SET "PreferredOutputId" = 2 WHERE "Id" = 1;
                DELETE FROM romd."Representations" WHERE "Id" = 1;
                """);
            await transaction.CommitAsync();
        }
        var remaining = (await new MediaFactReader(db).ReadAsync([1, 2])).Single().Value;
        remaining.Id.ShouldBe(2);
        remaining.Verifications.Count.ShouldBe(1);
        var pinned = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""DELETE FROM romd."Files" WHERE "Id" = 1;"""));
        pinned.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
        pinned.ConstraintName.ShouldBe("Representations_FileId_fkey");
    }

    [Fact]
    public async Task Verification_SharedArtifactConflictingMeasuredHeader_Rejects()
    {
        await SeedCdAsync();
        await using var db = _database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await SeedAlternateCdCoreAsync(db, conflictingHeader: true);
        var error = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await VerifyCoreAsync(db, "conflicting-header", "linux-x64", representationId: 2);
            await transaction.CommitAsync();
        });
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Theory]
    [InlineData(System.Data.IsolationLevel.RepeatableRead)]
    [InlineData(System.Data.IsolationLevel.Serializable)]
    public async Task Verification_OutsidePublicationIsolation_Refuses(System.Data.IsolationLevel isolation)
    {
        await SeedWholeAsync("Rvz");
        await using var db = _database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync(isolation);
        var error = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await VerifyCoreAsync(db, "reader");
            await transaction.CommitAsync();
        });
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Verification_SharedWholeArtifactDifferentLogicalBytes_Rejects()
    {
        await SeedWholeAsync("Rvz");
        await VerifyAsync("reader-a");
        await using var db = _database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO romd."Roms" ("Id","OriginalFilename","ContentSha256","ContentSize","Sha1","Md5","Crc32","CreatedAt","CreatedByUserId")
                VALUES (2,'other.iso',decode(repeat('ee',32),'hex'),100,decode(repeat('ee',20),'hex'),decode(repeat('ee',16),'hex'),decode('00000000','hex'),0,'00000000-0000-0000-0000-000000000000');
            INSERT INTO romd."Representations" VALUES (2,1,'Rvz');
            INSERT INTO romd."RvzContainerParameters" SELECT 2,"Version","DiscType","Compression","CompressionLevel","BlockBytes","HasWiiPartitions","LogicalBytes"
                FROM romd."RvzContainerParameters" WHERE "RepresentationId" = 1;
            INSERT INTO romd."RepresentationOutputs" ("Id","RepresentationId","RomId","ContentSha256","ContentSize","Sha1","Md5","Crc32")
                SELECT 2,2,"Id","ContentSha256","ContentSize","Sha1","Md5","Crc32" FROM romd."Roms" WHERE "Id" = 2;
            UPDATE romd."Roms" SET "PreferredOutputId" = 2 WHERE "Id" = 2;
            """);
        var error = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await VerifyCoreAsync(db, "reader-b", representationId: 2);
            await transaction.CommitAsync();
        });
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verification_ConcurrentSharedArtifactHeaderDisagreement_SerializesBeforeAcceptingFacts(bool secondFirst)
    {
        await SeedWholeAsync("Rvz");
        await using (var setup = _database.CreateContext())
        {
            await using var setupTransaction = await setup.Database.BeginTransactionAsync();
            await setup.Database.ExecuteSqlRawAsync("""
                INSERT INTO romd."Representations" VALUES (2,1,'Rvz');
                INSERT INTO romd."RvzContainerParameters" SELECT 2,"Version","DiscType",3,7,32768,"HasWiiPartitions","LogicalBytes"
                    FROM romd."RvzContainerParameters" WHERE "RepresentationId" = 1;
                INSERT INTO romd."RepresentationOutputs" ("Id","RepresentationId","RomId","ContentSha256","ContentSize","Sha1","Md5","Crc32")
                    SELECT 2,2,NULL,"ContentSha256","ContentSize","Sha1","Md5","Crc32" FROM romd."RepresentationOutputs" WHERE "Id" = 1;
                """);
            await setupTransaction.CommitAsync();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        await using var left = _database.CreateContext();
        await using var leftTransaction = await left.Database.BeginTransactionAsync(ct);
        await using var right = _database.CreateContext();
        await using var rightTransaction = await right.Database.BeginTransactionAsync(ct);
        var first = secondFirst ? right : left;
        var firstTransaction = secondFirst ? rightTransaction : leftTransaction;
        var second = secondFirst ? left : right;
        var secondTransaction = secondFirst ? leftTransaction : rightTransaction;
        int firstId = secondFirst ? 2 : 1;
        int secondId = secondFirst ? 1 : 2;
        await VerifyCoreAsync(first, "reader-first", ct: ct, representationId: firstId);
        int pid = await second.OpenBackendProcessIdAsync(ct);
        var pending = PublishSecondAsync();
        async Task PublishSecondAsync()
        {
            await VerifyCoreAsync(second, "reader-second", ct: ct, representationId: secondId);
            await secondTransaction.CommitAsync(ct);
        }
        try
        {
            (await _database.WaitsForALockAsync(pid, pending, ct)).ShouldBeTrue();
            await firstTransaction.CommitAsync(ct);
            var error = await Should.ThrowAsync<PostgresException>(() => pending);
            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        }
        finally
        {
            await firstTransaction.DisposeAsync();
            try
            { await pending; }
            catch (Exception) { /* Observe pending publication during cleanup. */ }
        }
        await using var observer = _database.CreateContext();
        (await VerificationCountAsync(observer)).ShouldBe(1);
        (await observer.Database.SqlQueryRaw<int>("""SELECT "RepresentationId" AS "Value" FROM romd."Verifications" """).SingleAsync(ct)).ShouldBe(firstId);
    }

    private static async Task SeedAlternateCdCoreAsync(RomdDbContext db, bool conflictingHeader = false)
    {
        string sql = """
                INSERT INTO romd."Files" ("Id","Sha256","Size","SizeOnDisk","IsCompressed","CreatedAt","CreatedByUserId")
                    VALUES (3,decode(repeat('de',32),'hex'),11,11,false,0,'00000000-0000-0000-0000-000000000000');
                INSERT INTO romd."Representations" VALUES (2,1,'Chd');
                INSERT INTO romd."ChdContainerParameters"
                    SELECT 2,"Version","LogicalBytes","HunkBytes","UnitBytes","HunkCount","Codec0","Codec1","Codec2","Codec3","HasParent","StoredRawSha1","StoredOverallSha1","MetadataSha256"
                    FROM romd."ChdContainerParameters" WHERE "RepresentationId" = 1;
                INSERT INTO romd."ChdMetadataEntries" SELECT 2,"Ordinal","Tag","Flags","Payload" FROM romd."ChdMetadataEntries" WHERE "RepresentationId" = 1;
                INSERT INTO romd."ReconstructionManifests" VALUES (2,36);
                INSERT INTO romd."ReconstructionTracks" SELECT 2,"Number","FirstFrame","Frames","Kind" FROM romd."ReconstructionTracks" WHERE "RepresentationId" = 1;
                INSERT INTO romd."ReconstructionMembers"
                    SELECT 2,0,'alternate.bin',"ContentSha256","ContentSize","Sha1","Md5","Crc32",NULL,NULL
                        FROM romd."ReconstructionMembers" WHERE "RepresentationId" = 1 AND "Ordinal" = 0;
                INSERT INTO romd."ReconstructionMembers" VALUES
                    (2,1,'alternate.cue',decode(repeat('de',32),'hex'),11,decode(repeat('ef',20),'hex'),decode(repeat('bc',16),'hex'),decode('00000000','hex'),3,'Cue');
                INSERT INTO romd."ReconstructionMemberTracks" VALUES (2,0,1),(2,0,2);
                INSERT INTO romd."RepresentationOutputs" ("Id","RepresentationId","MemberOrdinal","RomId","ContentSha256","ContentSize","Sha1","Md5","Crc32")
                    SELECT 2,2,0,"Id","ContentSha256","ContentSize","Sha1","Md5","Crc32" FROM romd."Roms" WHERE "Id" = 1;
                """;
        if (conflictingHeader)
            sql = sql.Replace("\"Codec0\",\"Codec1\"", "2053925218,\"Codec1\"");
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    [Fact]
    public async Task FactReader_ConcurrentRetentionCleanup_ReadsOneCompleteSnapshot()
    {
        await SeedCdAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var pause = new PauseFactOutputs();
        await using var readerContext = new RomdDbContext(_database.CreateOptions(options => options.AddInterceptors(pause)));
        var read = new MediaFactReader(readerContext).ReadAsync([1], ct);
        try
        {
            await Task.WhenAny(read, pause.Entered.Task).WaitAsync(ct);
            if (read.IsCompleted)
                await read;
            pause.Entered.Task.IsCompleted.ShouldBeTrue();
            await using var cleanup = _database.CreateContext();
            await cleanup.Database.ExecuteSqlRawAsync("""
                DELETE FROM romd."Roms" WHERE "Id" = 1;
                DELETE FROM romd."Representations" WHERE "Id" = 1;
                DELETE FROM romd."Files" WHERE "Id" IN (1,2);
                """, ct);
            pause.Resume.TrySetResult();
            var retained = (await read)[1];
            retained.Outputs.Count.ShouldBe(1);
            retained.Descriptors.Count.ShouldBe(1);
            retained.Verifications.Count.ShouldBe(1);
            (await new MediaFactReader(cleanup).ReadAsync([1], ct)).ShouldBeEmpty();
        }
        finally
        {
            pause.Resume.TrySetResult();
            try
            { await read; }
            catch (Exception) { /* Observe pending materialization during cleanup. */ }
        }
    }

    [Fact]
    public async Task FactReader_UnprotectedAmbientReadCommitted_RefusesBeforeMaterializing()
    {
        await SeedCdAsync();
        await using var db = _database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        await Should.ThrowAsync<InvalidOperationException>(() => new MediaFactReader(db).ReadAsync([1]));
    }

    [Theory]
    [InlineData(System.Data.IsolationLevel.RepeatableRead)]
    [InlineData(System.Data.IsolationLevel.Serializable)]
    public async Task FactReader_AmbientSnapshot_ReadsCompleteGraph(System.Data.IsolationLevel isolation)
    {
        await SeedCdAsync();
        await using var db = _database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync(isolation);
        (await new MediaFactReader(db).ReadAsync([1]))[1].Verifications.Count.ShouldBe(1);
        await transaction.CommitAsync();
    }

    private sealed class PauseFactOutputs : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _paused;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM romd.\"RepresentationOutputs\"", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _paused, 1) == 0)
            {
                Entered.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private async Task SeedCdAsync(string? mutation = null)
    {
        await using var db = _database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await SeedCdCoreAsync(db, mutation);
        await transaction.CommitAsync();
    }

    private static async Task SeedCdCoreAsync(RomdDbContext db, string? mutation = null, bool existingDescriptor = false)
    {
        string seedSql = """
            INSERT INTO romd."Files" ("Id","Sha256","Size","SizeOnDisk","IsCompressed","CreatedAt","CreatedByUserId") VALUES
                (1,decode(repeat('aa',32),'hex'),512,512,false,0,'00000000-0000-0000-0000-000000000000'),
                (2,decode(repeat('cc',32),'hex'),10,10,false,0,'00000000-0000-0000-0000-000000000000');
            INSERT INTO romd."Roms" ("Id","OriginalFilename","ContentSha256","ContentSize","Sha1","Md5","Crc32","CreatedAt","CreatedByUserId")
                VALUES (1,'disc.bin',decode(repeat('bb',32),'hex'),70560,decode(repeat('dd',20),'hex'),decode(repeat('ee',16),'hex'),decode('00000000','hex'),0,'00000000-0000-0000-0000-000000000000');
            INSERT INTO romd."Representations" VALUES (1,1,'Chd');
            INSERT INTO romd."ReconstructionManifests" VALUES (1,36);
            INSERT INTO romd."ReconstructionTracks" VALUES (1,1,0,17,'Mode2Raw'),(1,2,20,13,'Audio');
            INSERT INTO romd."ReconstructionMembers" VALUES
                (1,0,'disc.bin',decode(repeat('bb',32),'hex'),70560,decode(repeat('dd',20),'hex'),decode(repeat('ee',16),'hex'),decode('00000000','hex'),NULL,NULL),
                (1,1,'disc.cue',decode(repeat('cc',32),'hex'),10,decode(repeat('ff',20),'hex'),decode(repeat('ab',16),'hex'),decode('00000000','hex'),2,'Cue');
            INSERT INTO romd."ReconstructionMemberTracks" VALUES (1,0,1),(1,0,2);
            INSERT INTO romd."RepresentationOutputs" ("Id","RepresentationId","MemberOrdinal","RomId","ContentSha256","ContentSize","Sha1","Md5","Crc32")
                SELECT 1,1,0,"Id","ContentSha256","ContentSize","Sha1","Md5","Crc32" FROM romd."Roms" WHERE "Id" = 1;
            UPDATE romd."Roms" SET "PreferredOutputId" = 1 WHERE "Id" = 1;
            """;
        seedSql = mutation switch
        {
            "unicode-path-collision" => seedSql.Replace("'disc.bin'", "'élan.bin'").Replace("'disc.cue'", "'ÉLAN.BIN'"),
            "track-placement" => seedSql.Replace("(1,2,20,13,'Audio')", "(1,2,17,13,'Audio')"),
            "missing-track-owner" => seedSql.Replace("VALUES (1,0,1),(1,0,2)", "VALUES (1,0,1)"),
            "member-size" => seedSql.Replace("70560,decode(repeat('dd',20)", "70559,decode(repeat('dd',20)"),
            "missing-cue" => seedSql.Replace("2,'Cue')", "2,'Sidecar')"),
            "descriptor-identity" => seedSql.Replace("2,'Cue')", "1,'Cue')"),
            "unsafe-path" => seedSql.Replace("'disc.bin'", "'../disc.bin'"),
            "descriptor-output" => seedSql.Replace("SELECT 1,1,0,", "SELECT 1,1,1,"),
            _ => seedSql
        };
        if (existingDescriptor)
            seedSql = seedSql.Replace(",\n    (2,decode(repeat('cc',32),'hex'),10,10,false,0,'00000000-0000-0000-0000-000000000000');",
                ";");
        await db.Database.ExecuteSqlRawAsync(seedSql);
        if (mutation == "null-sidecar-role")
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO romd."ReconstructionMembers"
                SELECT "RepresentationId",2,'disc.txt',"ContentSha256","ContentSize","Sha1","Md5","Crc32","StoredFileId",NULL
                FROM romd."ReconstructionMembers" WHERE "Ordinal" = 1;
                """);
        await CaptureParametersAsync(db, "Chd", cd: true);
        if (mutation != "missing-proof")
            await VerifyCoreAsync(db, "native-reader", "linux-x64");
    }

    private async Task SeedWholeAsync(string encoding)
    {
        await using var db = _database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."Files" ("Id","Sha256","Size","SizeOnDisk","IsCompressed","CreatedAt","CreatedByUserId")
            VALUES (1, decode(repeat('aa',32),'hex'),{(encoding == "Chd" ? 512 : 100)},{(encoding == "Chd" ? 512 : 100)},false,0,'00000000-0000-0000-0000-000000000000');
            INSERT INTO romd."Roms" ("Id", "OriginalFilename", "ContentSha256", "ContentSize", "Sha1", "Md5", "Crc32", "CreatedAt", "CreatedByUserId")
            VALUES (1, 'disc.iso', decode(repeat('bb',32),'hex'),100, decode(repeat('cc',20),'hex'),decode(repeat('dd',16),'hex'),decode('00000000','hex'),0,'00000000-0000-0000-0000-000000000000');
            INSERT INTO romd."Representations" ("Id", "FileId", "Encoding") VALUES (1,1,{encoding});
            INSERT INTO romd."RepresentationOutputs" ("Id","RepresentationId","RomId","ContentSha256","ContentSize","Sha1","Md5","Crc32")
            SELECT 1,1,"Id","ContentSha256","ContentSize","Sha1","Md5","Crc32" FROM romd."Roms" WHERE "Id" = 1;
            UPDATE romd."Roms" SET "PreferredOutputId" = 1 WHERE "Id" = 1;
            """);
        await CaptureParametersAsync(db, encoding);
        await transaction.CommitAsync();
    }

    private async Task VerifyAsync(string build, string? rid = null, string? mutation = null)
    {
        await using var db = _database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await VerifyCoreAsync(db, build, rid, mutation);
        await transaction.CommitAsync();
    }

    private static async Task VerifyCoreAsync(RomdDbContext db, string build, string? rid = null, string? mutation = null, CancellationToken ct = default, int representationId = 1)
    {
        Guid id = Guid.NewGuid();
        string encoding = await db.Database.SqlQuery<string>($"""SELECT "Encoding" AS "Value" FROM romd."Representations" WHERE "Id" = {representationId}""").SingleAsync(ct);
        long artifactSize = encoding == "Chd" ? 512 : 100;
        string? observedRid = mutation is "native-with-managed-encoding" or "partial-native-build" ? "linux-x64" : rid;
        byte[]? pin = observedRid is null || mutation == "partial-native-build" ? null : Enumerable.Repeat((byte)1, 20).ToArray();
        int? recipe = pin is null ? null : 2;
        byte[] artifactHash = Enumerable.Repeat(mutation == "artifact-hash" ? (byte)0xab : (byte)0xaa, 32).ToArray();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."Verifications" ("Id","RepresentationId","ArtifactSha256","ArtifactSize","DecoderBuild","DecoderRid","DecoderUpstreamCommit","DecoderRecipeVersion","CheckedProfile","VerifierBuild","ObservedAtUtcTicks","ComputedRawSha1","ComputedOverallSha1","ObservedParametersSha256")
            VALUES ({id},{representationId},{artifactHash},{artifactSize + (mutation == "artifact-size" ? 1 : 0)},{build},{observedRid},{pin},{recipe},{(mutation == "blank-profile" ? "" : mutation == "control-profile" ? "observed\n" : "checked-output/test")},'verifier',621355968000000000,
                CASE WHEN {observedRid != null} THEN decode(repeat('cc',20),'hex') ELSE NULL END,
                CASE WHEN {observedRid != null} THEN decode(repeat('ab',20),'hex') ELSE NULL END,
                romd.media_parameter_fingerprint({representationId}));
            """, ct);
        if (mutation != "missing-output")
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."VerificationOutputs" ("VerificationId","RepresentationId","OutputId","ContentSha256","ContentSize","Sha1","Md5","Crc32")
                SELECT {id},{representationId},"Id",CASE WHEN {mutation == "output-hash"} THEN decode(repeat('be',32),'hex') ELSE "ContentSha256" END,
                    CASE WHEN {mutation == "output-size"} THEN 101 ELSE "ContentSize" END,"Sha1","Md5","Crc32" FROM romd."RepresentationOutputs" WHERE "RepresentationId" = {representationId};
                """, ct);
    }

    private static async Task CaptureParametersAsync(RomdDbContext db, string encoding, bool cd = false)
    {
        if (encoding == "Rvz")
            await db.Database.ExecuteSqlRawAsync("""INSERT INTO romd."RvzContainerParameters" VALUES (1,196608,0,5,22,131072,false,100);""");
        if (encoding != "Chd")
            return;
        // Exact ordered metadata bytes are separate from the reconstructed per-BIN identities.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."ChdContainerParameters" VALUES (1,5,{(cd ? 36 * 2448 : 100)},{(cd ? 4 * 2448 : 100)},
                {(cd ? 2448 : 1)},{(cd ? 9 : 1)},0,0,0,0,false,decode(repeat('cc',20),'hex'),decode(repeat('ab',20),'hex'),
                sha256(decode('44564420010000000100','hex')));
            INSERT INTO romd."ChdMetadataEntries" VALUES (1,0,1146504224,1,decode('00','hex'));
            """);
    }

    private static Task<int> VerificationCountAsync(RomdDbContext db) => db.Database.SqlQueryRaw<int>(
        """SELECT count(*)::int AS "Value" FROM romd."Verifications" """).SingleAsync();
    private static Task<int> VerificationOutputCountAsync(RomdDbContext db) => db.Database.SqlQueryRaw<int>(
        """SELECT count(*)::int AS "Value" FROM romd."VerificationOutputs" """).SingleAsync();
}
