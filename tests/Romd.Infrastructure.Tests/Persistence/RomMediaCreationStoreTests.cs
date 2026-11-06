using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class RomMediaCreationStoreTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private static readonly DateTimeOffset Observed = DateTimeOffset.UnixEpoch.AddTicks(1234567);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageAsync_CallerCompletes_AtomicallyPublishesCreationPreferenceAndSourceRelease(bool commit)
    {
        var seed = await SeedOriginalPreferredAndOwnedRvzAsync();
        await using (var db = _database.CreateContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            (await new RomMediaCreationStore(db).StageAsync(seed.RomId, Receipt(seed.Target), seed.Comparison, null)).IsError.ShouldBeFalse();
            var repository = MediaTestServices.CreateRomRepository(db);
            await repository.SetPreferredRepresentationStagedAsync(seed.RomId, seed.Target.Outputs.Single().Id);
            await repository.ReleaseRepresentationsStagedAsync(seed.RomId, [seed.Source.Outputs.Single().Id]);
            db.Database.CurrentTransaction.ShouldBeSameAs(transaction);
            await using var outside = _database.CreateContext();
            (await ReadAsync(outside, seed.Target.Id)).Creations.ShouldBeEmpty();
            (await outside.RomFiles.SingleAsync()).PreferredOutputId.ShouldBe(seed.Source.Outputs.Single().Id);
            if (commit)
                await transaction.CommitAsync();
            else
                await transaction.RollbackAsync();
        }
        await using var reader = _database.CreateContext();
        var retained = await ReadAsync(reader, seed.Target.Id);
        retained.Creations.Count.ShouldBe(commit ? 1 : 0);
        retained.Verifications.Single().Id.ShouldBe(seed.Target.Verifications.Single().Id);
        (await reader.RomFiles.SingleAsync()).PreferredOutputId.ShouldBe((commit ? seed.Target : seed.Source).Outputs.Single().Id);
        (await reader.RepresentationOutputs.SingleAsync(row => row.Id == seed.Source.Outputs.Single().Id)).RomId.ShouldBe(commit ? null : seed.RomId);
        if (commit)
            AssertCreation(retained.Creations.Single(), seed);
        (await reader.Verifications.CountAsync()).ShouldBe(2);
        (await reader.Files.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task StageAsync_ExactReplayAfterSourceCollection_RetainsOriginalWitnessAndRejectsChangedCreation()
    {
        var seed = await SeedOriginalPreferredAndOwnedRvzAsync();
        await using var db = _database.CreateContext();
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted))
        {
            var store = new RomMediaCreationStore(db);
            (await store.StageAsync(seed.RomId, Receipt(seed.Target), seed.Comparison, null)).IsError.ShouldBeFalse();
            (await store.StageAsync(seed.RomId, Receipt(seed.Target), seed.Comparison, null)).IsError.ShouldBeFalse();
            var repository = MediaTestServices.CreateRomRepository(db);
            await repository.SetPreferredRepresentationStagedAsync(seed.RomId, seed.Target.Outputs.Single().Id);
            await repository.ReleaseRepresentationsStagedAsync(seed.RomId, [seed.Source.Outputs.Single().Id]);
            await transaction.CommitAsync();
        }
        (await new MediaRepresentationCleanup(db).CollectAsync(256)).ShouldBe(1);
        (await new MediaFactReader(db).ReadAsync([seed.Source.Id])).ShouldBeEmpty();
        var first = (await ReadAsync(db, seed.Target.Id)).Creations.Single();
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted))
        {
            (await new RomMediaCreationStore(db).StageAsync(seed.RomId, Receipt(seed.Target), seed.Comparison, null)).IsError.ShouldBeFalse();
            var changed = new RomRepresentationCreation(first.Id, first.VerificationId, first.Recipe, "other-encoder", first.ComparedAt,
                first.ParametersSha256, first.NativeEncoder, first.Sources);
            await Should.ThrowAsync<InvalidDataException>(() => new MediaCreationFactWriter(db).StageCreationAsync(seed.Target.Id, changed, default));
            await transaction.CommitAsync();
        }
        var after = (await ReadAsync(db, seed.Target.Id)).Creations.Single();
        AssertCreation(after, seed);
        after.Sources.ShouldBe(first.Sources);
        (await db.Verifications.SingleAsync()).Id.ShouldBe(seed.Target.Verifications.Single().Id);
        (await db.Files.CountAsync()).ShouldBe(2);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("file")]
    [InlineData("output")]
    [InlineData("representation")]
    [InlineData("proof")]
    [InlineData("released-target")]
    [InlineData("source-artifact")]
    [InlineData("sha256-collision")]
    public async Task StageAsync_ForeignOrWithdrawnBindings_ReturnsConflictWithoutInventingCreation(string change)
    {
        var seed = await SeedOriginalPreferredAndOwnedRvzAsync();
        await using var db = _database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var receipt = Receipt(seed.Target);
        receipt = change switch
        {
            "file" => receipt with { FileId = seed.Source.FileId },
            "output" => receipt with { OutputId = seed.Source.Outputs.Single().Id },
            "representation" => receipt with { RepresentationId = seed.Source.Id },
            "proof" => receipt with { VerificationId = seed.Source.Verifications.Single().Id },
            _ => receipt
        };
        if (change == "released-target")
            await MediaTestServices.CreateRomRepository(db).ReleaseRepresentationsStagedAsync(seed.RomId, [seed.Target.Outputs.Single().Id]);
        var comparison = seed.Comparison;
        if (change is "source-artifact" or "sha256-collision")
        {
            var logical = seed.Target.Outputs.Single().Identity;
            if (change == "sha256-collision")
                logical = new(logical.Size, Sha256.Parse(new string('e', 64)), logical.Sha1, logical.Md5, logical.Crc32);
            // Deliberately claim an encoded source measurement with a foreign artifact or colliding legacy hashes.
            // Neither packet may create provenance for a graph whose complete retained identity disagrees.
            var source = ComparisonProof(seed.Source, logical, change == "source-artifact" ? Sha256.Parse(new string('d', 64)) : seed.Source.ArtifactSha256, RomRepresentationEncoding.Rvz);
            var target = ComparisonProof(seed.Target, logical, seed.Target.ArtifactSha256, RomRepresentationEncoding.Rvz);
            RomSourceEquivalence.TryCreate(source, target, "fixture-recipe/v1", "fixture-encoder", Observed, out comparison).ShouldBeTrue();
        }
        var result = await new RomMediaCreationStore(db).StageAsync(change == "owner" ? seed.RomId + 100 : seed.RomId, receipt, comparison, null);
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Rom.CreationConflict");
        (await ReadLockedAsync(db, seed.Target.Id)).Creations.ShouldBeEmpty();
        await transaction.RollbackAsync();
        (await db.Verifications.CountAsync()).ShouldBe(2);
        (await db.RepresentationOutputs.CountAsync(row => row.RomId == seed.RomId)).ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageAsync_ConcurrentSourceRelease_SerializesOwnershipAndPreservesIndependentProofs(bool creationFirst)
    {
        var seed = await SeedOriginalPreferredAndOwnedRvzAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var creating = _database.CreateContext();
        await using var creationTransaction = await creating.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using var releasing = _database.CreateContext();
        await using var releaseTransaction = await releasing.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var store = new RomMediaCreationStore(creating);
        if (creationFirst)
        {
            (await store.StageAsync(seed.RomId, Receipt(seed.Target), seed.Comparison, null, ct)).IsError.ShouldBeFalse();
            var release = ReleaseAsync();
            await WaitForBlockedMutationAsync(releasing, ct);
            release.IsCompleted.ShouldBeFalse();
            await creationTransaction.CommitAsync(ct);
            await release;
            await releaseTransaction.CommitAsync(ct);
        }
        else
        {
            await ReleaseAsync();
            var creation = store.StageAsync(seed.RomId, Receipt(seed.Target), seed.Comparison, null, ct);
            await WaitForBlockedMutationAsync(creating, ct);
            creation.IsCompleted.ShouldBeFalse();
            await releaseTransaction.CommitAsync(ct);
            var result = await creation;
            result.IsError.ShouldBeTrue();
            result.FirstError.Code.ShouldBe("Rom.CreationConflict");
            await creationTransaction.CommitAsync(ct);
        }
        await using var reader = _database.CreateContext();
        var graph = await ReadAsync(reader, seed.Target.Id);
        graph.Creations.Count.ShouldBe(creationFirst ? 1 : 0);
        if (creationFirst)
            AssertCreation(graph.Creations.Single(), seed);
        (await reader.RepresentationOutputs.SingleAsync(row => row.Id == seed.Source.Outputs.Single().Id)).RomId.ShouldBeNull();
        (await reader.Verifications.CountAsync()).ShouldBe(2);
        (await new MediaRepresentationCleanup(reader).CollectAsync(256, ct)).ShouldBe(1);
        (await reader.Verifications.SingleAsync(ct)).Id.ShouldBe(seed.Target.Verifications.Single().Id);
        (await reader.Files.CountAsync(ct)).ShouldBe(2);

        async Task ReleaseAsync()
        {
            var repository = MediaTestServices.CreateRomRepository(releasing);
            await repository.LockRepresentationSetAsync(seed.RomId, ct);
            await repository.SetPreferredRepresentationStagedAsync(seed.RomId, seed.Target.Outputs.Single().Id, ct);
            await repository.ReleaseRepresentationsStagedAsync(seed.RomId, [seed.Source.Outputs.Single().Id], ct);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task StageAsync_UnsupportedTransaction_RefusesWithoutCreation(IsolationLevel? isolation)
    {
        var seed = await SeedOriginalPreferredAndOwnedRvzAsync();
        await using var db = _database.CreateContext();
        await using var transaction = isolation is { } level ? await db.Database.BeginTransactionAsync(level) : null;
        await Should.ThrowAsync<InvalidOperationException>(() => new RomMediaCreationStore(db).StageAsync(seed.RomId, Receipt(seed.Target), seed.Comparison, null));
        if (transaction is not null)
            await transaction.RollbackAsync();
        (await ReadAsync(db, seed.Target.Id)).Creations.ShouldBeEmpty();
    }

    [Fact]
    public async Task StageAsync_PreCancelledToken_PrecedesTransactionAndLookup()
    {
        var seed = await SeedOriginalPreferredAndOwnedRvzAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await using var db = _database.CreateContext();
        await Should.ThrowAsync<OperationCanceledException>(() => new RomMediaCreationStore(db).StageAsync(seed.RomId, Receipt(seed.Target), seed.Comparison, null, cancelled.Token));
        (await ReadAsync(db, seed.Target.Id)).Creations.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("target-proof")]
    [InlineData("parameters")]
    [InlineData("source-proof")]
    [InlineData("source-output")]
    [InlineData("source-file")]
    [InlineData("source-artifact")]
    [InlineData("missing-source")]
    public async Task DirectSql_ForeignOrIncompleteCreation_RejectsAndRollsBackAllNewProvenance(string change)
    {
        var seed = await SeedOriginalPreferredAndOwnedRvzAsync();
        await using var db = _database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        Guid creationId = Guid.NewGuid();
        var error = await Should.ThrowAsync<PostgresException>(async () =>
        {
            Guid targetProof = (change == "target-proof" ? seed.Source : seed.Target).Verifications.Single().Id;
            var parameters = change == "parameters" ? Sha256.Parse(new string('e', 64)) : seed.Target.Container.Fingerprint!.Value;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."RepresentationCreations" ("Id","RepresentationId","VerificationId","RecipeId","EncoderBuild","ComparedAtUtcTicks","ParametersSha256")
                VALUES ({creationId},{seed.Target.Id},{targetProof},'fixture-recipe/v1','fixture-encoder',{Observed.UtcTicks},{parameters.ToArray()})
                """);
            if (change != "missing-source")
            {
                Guid sourceProof = (change == "source-proof" ? seed.Target : seed.Source).Verifications.Single().Id;
                int sourceOutput = (change == "source-output" ? seed.Target : seed.Source).Outputs.Single().Id;
                int sourceFile = (change == "source-file" ? seed.Target : seed.Source).FileId;
                var sourceArtifact = (change == "source-artifact" ? seed.Target : seed.Source).ArtifactSha256;
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO romd."CreationSources" VALUES ({creationId},{seed.Target.Outputs.Single().Id},{seed.Source.Id},{sourceOutput},
                        {sourceProof},{sourceFile},{sourceArtifact.ToArray()},{seed.Source.ArtifactSize})
                    """);
            }
            await transaction.CommitAsync();
        });
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        // A deferred constraint rejects COMMIT and PostgreSQL has already rolled back that transaction.
        if (change != "missing-source")
            await transaction.RollbackAsync();
        await using var reader = _database.CreateContext();
        (await ReadAsync(reader, seed.Target.Id)).Creations.ShouldBeEmpty();
        (await reader.Verifications.CountAsync()).ShouldBe(2);
        (await reader.RepresentationOutputs.CountAsync(row => row.RomId == seed.RomId)).ShouldBe(2);
    }

    [Theory]
    [InlineData("UPDATE romd.\"RepresentationCreations\" SET \"EncoderBuild\" = 'forged'")]
    [InlineData("DELETE FROM romd.\"RepresentationCreations\"")]
    [InlineData("UPDATE romd.\"CreationSources\" SET \"SourceArtifactSize\" = \"SourceArtifactSize\" + 1")]
    [InlineData("DELETE FROM romd.\"CreationSources\"")]
    public async Task DirectSql_RetainedCreationOrSourceMutation_PreservesExactWitness(string sql)
    {
        var seed = await SeedOriginalPreferredAndOwnedRvzAsync();
        await using var db = _database.CreateContext();
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted))
        {
            (await new RomMediaCreationStore(db).StageAsync(seed.RomId, Receipt(seed.Target), seed.Comparison, null)).IsError.ShouldBeFalse();
            await transaction.CommitAsync();
        }
        var error = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        AssertCreation((await ReadAsync(db, seed.Target.Id)).Creations.Single(), seed);
    }

    private async Task<Seed> SeedOriginalPreferredAndOwnedRvzAsync()
    {
        await using var db = _database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var logical = MediaFactFixtures.Identity(Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray());
        var originalFile = new FileEntityPersistence { Sha256 = logical.Sha256, Size = logical.Size, SizeOnDisk = logical.Size };
        var rvzFile = new FileEntityPersistence { Sha256 = Sha256.Parse(new string('b', 64)), Size = 512, SizeOnDisk = 512 };
        db.Files.AddRange(originalFile, rvzFile);
        await db.SaveChangesAsync();
        var sourceProof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), logical.Sha256, logical.Size, logical, RomContainerFacts.Identity.Instance,
            new RomDecoderIdentity.Managed("fixture-original/v1"), "identity/v1", "fixture-verifier", Observed, null, null);
        var source = await db.CreatePreferredMeasuredWholeAsync("disc.iso", originalFile.Id, RomContainerFacts.Identity.Instance, sourceProof,
            Guid.Empty, Observed, CancellationToken.None);
        var container = MediaFactFixtures.Rvz(logical.Size, 1, 5, 19, 131072, false);
        var targetProof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), rvzFile.Sha256, rvzFile.Size, logical, container,
            new RomDecoderIdentity.Managed("fixture-rvz/v2"), "whole-output/v1", "fixture-verifier", Observed.AddTicks(7), null, null);
        var target = await db.StageMeasuredWholeAsync(rvzFile.Id, container, targetProof,
            new(RomOutputSelector.Whole.Instance, source.RomId, false), CancellationToken.None);
        RomSourceEquivalence.TryCreate(ComparisonProof(source.Graph, logical, source.Graph.ArtifactSha256, RomRepresentationEncoding.Identity),
            ComparisonProof(target, logical, target.ArtifactSha256, RomRepresentationEncoding.Rvz), "fixture-recipe/v1", "fixture-encoder", Observed.AddTicks(13), out var comparison).ShouldBeTrue();
        await transaction.CommitAsync();
        return new(source.RomId, source.Graph, target, comparison);
    }

    private static RomVerification ComparisonProof(RomRepresentationFacts graph, RomLogicalIdentity identity, Sha256 artifact, RomRepresentationEncoding encoding) =>
        RomVerification.Rehydrate(graph.Verifications.Single().Id, artifact, graph.ArtifactSize,
            encoding,
            identity.Sha256, identity.Size, identity.Sha1, identity.Md5, identity.Crc32, "whole-output/v1", "fixture-decoder", "fixture-verifier", Observed);

    private static RomRetainedMeasurement Receipt(RomRepresentationFacts graph) => new(graph.Id, graph.Outputs.Single().Id, graph.FileId, graph.Verifications.Single().Id);
    private static async Task<RomRepresentationFacts> ReadAsync(RomdDbContext db, int id) => (await new MediaFactReader(db).ReadAsync([id]))[id];
    private static async Task<RomRepresentationFacts> ReadLockedAsync(RomdDbContext db, int id) => (await new MediaFactReader(db).ReadLockedPublicationAsync([id], default))[id];
    private static void AssertCreation(RomRepresentationCreation creation, Seed seed)
    {
        creation.Id.ShouldBe(seed.Comparison.Id);
        creation.VerificationId.ShouldBe(seed.Target.Verifications.Single().Id);
        creation.Recipe.Value.ShouldBe("fixture-recipe/v1");
        creation.EncoderBuild.ShouldBe("fixture-encoder");
        creation.ComparedAt.ShouldBe(Observed.AddTicks(13));
        creation.ParametersSha256.ShouldBe(seed.Target.Container.Fingerprint!.Value);
        creation.NativeEncoder.ShouldBeNull();
        var witness = creation.Sources.Single();
        witness.TargetOutputId.ShouldBe(seed.Target.Outputs.Single().Id);
        witness.RepresentationId.ShouldBe(seed.Source.Id);
        witness.OutputId.ShouldBe(seed.Source.Outputs.Single().Id);
        witness.VerificationId.ShouldBe(seed.Source.Verifications.Single().Id);
        witness.FileId.ShouldBe(seed.Source.FileId);
        witness.ArtifactSha256.ShouldBe(seed.Source.ArtifactSha256);
        witness.ArtifactSize.ShouldBe(seed.Source.ArtifactSize);
    }
    private async Task WaitForBlockedMutationAsync(RomdDbContext context, CancellationToken ct)
    {
        int pid = ((NpgsqlConnection)context.Database.GetDbConnection()).ProcessID;
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid = $1 AND NOT granted)", connection);
        command.Parameters.AddWithValue(pid);
        while (!(bool)(await command.ExecuteScalarAsync(ct))!)
            await Task.Delay(20, ct);
    }
    private sealed record Seed(int RomId, RomRepresentationFacts Source, RomRepresentationFacts Target, RomSourceEquivalence Comparison);
    public void Dispose() => _database.Dispose();
}
