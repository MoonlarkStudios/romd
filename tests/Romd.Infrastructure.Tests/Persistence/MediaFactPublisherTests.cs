using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Romd.Admin.Application.Common.Persistence;
using Npgsql;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.MediaFacts;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class MediaFactPublisherTests : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private static readonly DateTimeOffset Observed = DateTimeOffset.UnixEpoch.AddTicks(1234567);
    public async Task InitializeAsync()
    {
        await using var db = _database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO romd."Files" ("Id","Sha256","Size","SizeOnDisk","IsCompressed","CreatedAt","CreatedByUserId") VALUES
                (1,decode(repeat('bb',32),'hex'),512,512,false,0,'00000000-0000-0000-0000-000000000000'),
                (2,decode(repeat('cc',32),'hex'),10,10,false,0,'00000000-0000-0000-0000-000000000000'),
                (3,decode(repeat('aa',32),'hex'),100,100,false,0,'00000000-0000-0000-0000-000000000000');
            """);
    }
    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Theory]
    [InlineData("Identity")]
    [InlineData("Rvz")]
    [InlineData("Chd")]
    public async Task Publish_WholeFile_AtomicallyRetainsTypedFactsAndExactMeasurement(string encoding)
    {
        await using var db = _database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var publisher = new MediaFactPublisher(db);
        var identity = Identity(100, 'a');
        var container = Container(encoding);
        int romId = await publisher.CreateLogicalAsync("disc.iso", identity, Guid.Empty, Observed);
        var proof = Proof(container, [new(RomOutputSelector.Whole.Instance, identity)]);
        var published = await publisher.PublishAsync(encoding == "Identity" ? 3 : 1, container, RomReconstructionManifest.WholeFile.Instance,
            proof, [new(RomOutputSelector.Whole.Instance, romId, true)], []);
        await transaction.CommitAsync();
        await using var observer = _database.CreateContext();
        var retained = (await new MediaFactReader(observer).ReadAsync([published.Id]))[published.Id];
        retained.Container.Fingerprint.ShouldBe(container.Fingerprint);
        retained.Outputs.Single().RomId.ShouldBe(romId);
        retained.Verifications.Single().Id.ShouldBe(proof.Id);
        retained.Verifications.Single().ObservedAt.ShouldBe(Observed);
        (await PreferredAsync(observer, romId)).ShouldBe(retained.Outputs.Single().Id);
    }

    [Fact]
    public async Task Publish_CdTracks_RetainsEveryBinAndDescriptorWithoutLegacyJson()
    {
        await using var db = _database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var publisher = new MediaFactPublisher(db);
        var first = Identity(17 * 2352, 'a');
        var second = Identity(13 * 2352, 'd');
        var cue = Identity(10, 'c');
        int firstId = await publisher.CreateLogicalAsync("a.bin", first, Guid.Empty, Observed);
        int secondId = await publisher.CreateLogicalAsync("b.bin", second, Guid.Empty, Observed);
        var manifest = new RomReconstructionManifest.CdTracks(36, [new(1, 0, 17, RomCdTrackKind.Mode2Raw), new(2, 20, 13, RomCdTrackKind.Audio)],
            [new RomCdBinMember("a.bin",Cd(first),Cd(first),[1]),new RomCdBinMember("b.bin",Cd(second),Cd(second),[2]),
             new RomCdStoredMember("disc.cue",Cd(cue),RomCdStoredRole.Cue)]);
        var container = new RomContainerFacts.Chd(5, 36 * 2448, 4 * 2448, 2448, 9, [0, 0, 0, 0], false, ShortHash('e'), ShortHash('f'),
            [new(0x43485432, 1, "TRACK:1 TYPE:MODE2_RAW"u8)]);
        var proof = Proof(container, [new(new RomOutputSelector.Member(0), first), new(new RomOutputSelector.Member(1), second)]);
        var published = await publisher.PublishAsync(1, container, manifest, proof,
            [new(new RomOutputSelector.Member(0), firstId, true), new(new RomOutputSelector.Member(1), secondId, true)],
            [new(2, 2, Hash('c'), 10)]);
        await transaction.CommitAsync();
        await using var observer = _database.CreateContext();
        var retained = (await new MediaFactReader(observer).ReadAsync([published.Id]))[published.Id];
        retained.Manifest.ShouldBeOfType<RomReconstructionManifest.CdTracks>().Members.Count.ShouldBe(3);
        retained.Outputs.Count.ShouldBe(2);
        retained.Verifications.Single().Outputs.Count.ShouldBe(2);
        retained.Descriptors.Single().FileId.ShouldBe(2);
        (await PreferredAsync(observer, firstId)).ShouldBe(retained.Outputs.Single(output => output.RomId == firstId).Id);
        (await PreferredAsync(observer, secondId)).ShouldBe(retained.Outputs.Single(output => output.RomId == secondId).Id);
    }

    [Theory]
    [InlineData("Rvz")]
    [InlineData("Chd")]
    public async Task AppendVerification_NewAndRepeatedBuild_RetainsEachBuildOnce(string encoding)
    {
        var published = await PublishWholeAsync(encoding);
        await using var db = _database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var second = Proof(published.Container, published.Verifications.Single().Outputs, "second");
        var publisher = new MediaFactPublisher(db);
        (await publisher.AppendVerificationAsync(published.Id, second)).ShouldBeTrue();
        (await publisher.AppendVerificationAsync(published.Id, Proof(published.Container, second.Outputs, "second"))).ShouldBeFalse();
        await transaction.CommitAsync();
        await using var observer = _database.CreateContext();
        var retained = (await new MediaFactReader(observer).ReadAsync([published.Id]))[published.Id];
        retained.Verifications.Count.ShouldBe(2);
        retained.Verifications.Select(proof => proof.Id).ShouldContain(second.Id);
        retained.Verifications.Select(proof => proof.Id).ShouldContain(published.Verifications.Single().Id);
    }

    [Fact]
    public async Task Publish_CallerRollsBack_RemovesLogicalOutputProofAndPreferenceTogether()
    {
        await using (var db = _database.CreateContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var publisher = new MediaFactPublisher(db);
            var identity = Identity(100, 'a');
            var container = Container("Rvz");
            int romId = await publisher.CreateLogicalAsync("disc.iso", identity, Guid.Empty, Observed);
            await publisher.PublishAsync(1, container, RomReconstructionManifest.WholeFile.Instance, Proof(container, [new(RomOutputSelector.Whole.Instance, identity)]),
                [new(RomOutputSelector.Whole.Instance, romId, true)], []);
            await transaction.RollbackAsync();
        }
        await using var observer = _database.CreateContext();
        (await observer.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM romd."Roms" """).SingleAsync()).ShouldBe(0);
        (await observer.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM romd."Representations" """).SingleAsync()).ShouldBe(0);
        (await observer.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM romd."Verifications" """).SingleAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Publish_WithoutCallerTransaction_RefusesBeforeWriting()
    {
        await using var db = _database.CreateContext();
        await Should.ThrowAsync<InvalidOperationException>(() => new MediaFactPublisher(db).CreateLogicalAsync("disc.iso", Identity(100, 'a'), Guid.Empty, Observed));
    }

    [Fact]
    public async Task Publish_ArtifactGuardRejects_RollsBackAttemptAndAllowsValidPublicationInSameTransaction()
    {
        await using var db = _database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var publisher = new MediaFactPublisher(db);
        var identity = Identity(100, 'a');
        var container = Container("Rvz");
        int romId = await publisher.CreateLogicalAsync("disc.iso", identity, Guid.Empty, Observed);
        var measured = new RomMeasuredOutput(RomOutputSelector.Whole.Instance, identity);
        var wrong = new RomRepresentationVerification(Guid.NewGuid(), Hash('d'), 512, new RomDecoderIdentity.Managed("bad"),
            "measured/test", "verifier/test", Observed, [measured], container);
        var error = await Should.ThrowAsync<PostgresException>(() => publisher.PublishAsync(1, container, RomReconstructionManifest.WholeFile.Instance,
            wrong, [new(RomOutputSelector.Whole.Instance, romId, true)], []));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        var published = await publisher.PublishAsync(1, container, RomReconstructionManifest.WholeFile.Instance, Proof(container, [measured]),
            [new(RomOutputSelector.Whole.Instance, romId, true)], []);
        await transaction.CommitAsync();
        await using var observer = _database.CreateContext();
        (await observer.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM romd."Representations" """).SingleAsync()).ShouldBe(1);
        (await observer.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM romd."Verifications" """).SingleAsync()).ShouldBe(1);
        (await PreferredAsync(observer, romId)).ShouldBe(published.Outputs.Single().Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_ExistingLogicalOwnerDifferentBytes_CommitRefusesAndRetainsOriginal(bool differentHash)
    {
        var first = await PublishWholeAsync("Rvz");
        await using (var db = _database.CreateContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var identity = Identity(differentHash ? 100 : 101, differentHash ? 'd' : 'a');
            var container = new RomContainerFacts.Rvz(0x30000, 1, 5, 22, 131072, false, identity.Size);
            await new MediaFactPublisher(db).PublishAsync(1, container, RomReconstructionManifest.WholeFile.Instance,
                Proof(container, [new(RomOutputSelector.Whole.Instance, identity)]), [new(RomOutputSelector.Whole.Instance, first.Outputs.Single().RomId, true)], []);
            var error = await Should.ThrowAsync<PostgresException>(() => transaction.CommitAsync());
            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        }
        await using var observer = _database.CreateContext();
        (await observer.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM romd."Representations" """).SingleAsync()).ShouldBe(1);
        (await PreferredAsync(observer, first.Outputs.Single().RomId!.Value)).ShouldBe(first.Outputs.Single().Id);
    }

    [Theory]
    [InlineData("Rvz")]
    [InlineData("Chd")]
    public async Task AppendVerification_ConcurrentSameBuild_SerializesAndRetainsFirstMeasurement(string encoding)
    {
        var published = await PublishWholeAsync(encoding);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        await using var first = _database.CreateContext();
        await using var second = _database.CreateContext();
        await using var firstTransaction = await first.Database.BeginTransactionAsync(ct);
        await using var secondTransaction = await second.Database.BeginTransactionAsync(ct);
        var measurement = Proof(published.Container, published.Verifications.Single().Outputs, "second");
        (await new MediaFactPublisher(first).AppendVerificationAsync(published.Id, measurement, ct)).ShouldBeTrue();
        int pid = await second.OpenBackendProcessIdAsync(ct);
        var pending = new MediaFactPublisher(second).AppendVerificationAsync(published.Id,
            Proof(published.Container, measurement.Outputs, "second"), ct);
        try
        {
            (await _database.WaitsForALockAsync(pid, pending, ct)).ShouldBeTrue();
            await firstTransaction.CommitAsync(ct);
            (await pending).ShouldBeFalse();
            await secondTransaction.CommitAsync(ct);
        }
        finally
        {
            await firstTransaction.DisposeAsync();
            try
            { await pending; }
            catch (Exception) { /* Observe the pending operation during cleanup. */ }
        }
        await using var observer = _database.CreateContext();
        var retained = (await new MediaFactReader(observer).ReadAsync([published.Id], ct))[published.Id];
        retained.Verifications.Count.ShouldBe(2);
        retained.Verifications.Select(proof => proof.Id).ShouldContain(measurement.Id);
    }

    [Fact]
    public async Task AppendVerification_ConcurrentOutputWithdrawal_FullTransactionRetryPreservesBothOutcomes()
    {
        var preferred = await PublishWholeAsync("Rvz");
        RomRepresentationFacts additional;
        await using (var db = _database.CreateContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            additional = await new MediaFactPublisher(db).PublishAsync(1, preferred.Container, preferred.Manifest, Proof(preferred.Container, preferred.Verifications.Single().Outputs),
                [new(RomOutputSelector.Whole.Instance, preferred.Outputs.Single().RomId, false)], []);
            await transaction.CommitAsync();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var pause = new PauseProofPublication();
        await using var appender = new RomdDbContext(_database.CreateOptions(options => options.AddInterceptors(pause)));
        await using var withdrawal = _database.CreateContext();
        var measurement = Proof(additional.Container, additional.Verifications.Single().Outputs, "second");
        async Task<Exception?> AppendAsync(RomdDbContext db)
        {
            await using var transaction = await new EfUnitOfWork(db).BeginTransactionAsync(ct);
            try
            {
                await new MediaFactPublisher(db).AppendVerificationAsync(additional.Id, measurement, ct);
                await transaction.CommitAsync(ct);
                return null;
            }
            catch (Exception error) { await transaction.RollbackAsync(); return error; }
        }
        async Task<Exception?> WithdrawAsync(RomdDbContext db)
        {
            await using var transaction = await new EfUnitOfWork(db).BeginTransactionAsync(ct);
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE romd."RepresentationOutputs" SET "RomId" = NULL WHERE "Id" = {additional.Outputs.Single().Id}""", ct);
                await transaction.CommitAsync(ct);
                return null;
            }
            catch (Exception error) { await transaction.RollbackAsync(); return error; }
        }
        var append = AppendAsync(appender);
        await Task.WhenAny(append, pause.Entered.Task).WaitAsync(ct);
        if (append.IsCompleted)
            (await append).ShouldBeNull();
        pause.Entered.Task.IsCompleted.ShouldBeTrue();
        int pid = await withdrawal.OpenBackendProcessIdAsync(ct);
        var withdraw = WithdrawAsync(withdrawal);
        try
        {
            (await _database.WaitsForALockAsync(pid, withdraw, ct)).ShouldBeTrue();
            pause.Resume.TrySetResult();
            var errors = await Task.WhenAll(append, withdraw).WaitAsync(ct);
            errors.Count(error => error is not null).ShouldBe(1);
            var conflict = errors.Single(error => error is not null)!;
            new EfUnitOfWork(appender).ConflictOf(conflict).ShouldBe(TransactionConflict.Deadlock);
            await using var retry = _database.CreateContext();
            // Replay the entire failed unit of work, rather than retrying one statement in its aborted transaction.
            (await (errors[0] is not null ? AppendAsync(retry) : WithdrawAsync(retry))).ShouldBeNull();
        }
        finally
        {
            pause.Resume.TrySetResult();
            await Task.WhenAll(append, withdraw);
        }
        await using var observer = _database.CreateContext();
        var retained = (await new MediaFactReader(observer).ReadAsync([additional.Id], ct))[additional.Id];
        retained.Outputs.Single().RomId.ShouldBeNull();
        retained.Verifications.Count.ShouldBe(2);
        retained.Verifications.Select(proof => proof.Id).ShouldContain(measurement.Id);
        (await PreferredAsync(observer, preferred.Outputs.Single().RomId!.Value)).ShouldBe(preferred.Outputs.Single().Id);
    }

    private sealed class PauseProofPublication : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SELECT romd.publish_media_verification", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private async Task<RomRepresentationFacts> PublishWholeAsync(string encoding)
    {
        await using var db = _database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var publisher = new MediaFactPublisher(db);
        var identity = Identity(100, 'a');
        var container = Container(encoding);
        int romId = await publisher.CreateLogicalAsync("disc.iso", identity, Guid.Empty, Observed);
        var published = await publisher.PublishAsync(1, container, RomReconstructionManifest.WholeFile.Instance, Proof(container, [new(RomOutputSelector.Whole.Instance, identity)]),
            [new(RomOutputSelector.Whole.Instance, romId, true)], []);
        await transaction.CommitAsync();
        return published;
    }
    private static Task<int> PreferredAsync(RomdDbContext db, int romId) =>
        db.Database.SqlQuery<int>($"""SELECT "PreferredOutputId" AS "Value" FROM romd."Roms" WHERE "Id" = {romId}""").SingleAsync();
    private static Sha256 Hash(char value) => Sha256.Parse(new string(value, 64));
    private static Sha1 ShortHash(char value) => Sha1.Parse(new string(value, 40));
    private static RomLogicalIdentity Identity(long size, char value) => new(size, Hash(value), ShortHash(value), Md5.Parse(new string(value, 32)), Crc32.FromUInt32(0));
    private static RomCdFileIdentity Cd(RomLogicalIdentity identity) => new(identity.Size, identity.Sha256, identity.Sha1, identity.Md5, identity.Crc32);
    private static RomContainerFacts Container(string encoding) => encoding switch
    {
        "Identity" => RomContainerFacts.Identity.Instance,
        "Rvz" => new RomContainerFacts.Rvz(0x30000, 1, 5, 22, 131072, false, 100),
        _ => new RomContainerFacts.Chd(5, 100, 100, 1, 1, [0, 0, 0, 0], false, ShortHash('a'), ShortHash('f'), [])
    };
    private static RomRepresentationVerification Proof(RomContainerFacts container, IReadOnlyList<RomMeasuredOutput> outputs, string build = "first") =>
        new(Guid.NewGuid(), Hash(container is RomContainerFacts.Identity ? 'a' : 'b'), container is RomContainerFacts.Identity ? 100 : 512,
            container is RomContainerFacts.Chd ? new RomDecoderIdentity.Native(new(build == "first" ? "linux-x64" : "linux-arm64", ShortHash('f'), 1)) : new RomDecoderIdentity.Managed(build),
            "measured/test", "verifier/test", Observed, outputs, container, (container as RomContainerFacts.Chd)?.StoredRawSha1, (container as RomContainerFacts.Chd)?.StoredOverallSha1);
}
