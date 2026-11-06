using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Npgsql;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Storage;
using Romd.Infrastructure.Storage;
using Romd.Persistence;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ArtifactChecksumLedgerTests : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"romd-artifact-checksums-{Guid.NewGuid():N}");
    private readonly byte[] _bytes = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 7)).ToArray();
    private ServiceProvider _services = null!;
    private IContentAddressableStore _cas = null!;
    private Sha256 Hash => Sha256.FromSpan(SHA256.HashData(_bytes));
    private Md5 Md5 => Romd.Domain.Hashing.Md5.FromSpan(MD5.HashData(_bytes));
    private Sha1 Sha1 => Romd.Domain.Hashing.Sha1.FromSpan(SHA1.HashData(_bytes));

    public Task InitializeAsync()
    {
        _services = new ServiceCollection().AddContentAddressableStorage(options => options.RootPath = _root)
            .BuildServiceProvider();
        _cas = _services.GetRequiredService<IContentAddressableStore>();
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishStaged_NewArtifactOrCasOrphan_RecordsHashesOfDeliveredBytes(bool casOrphan)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        if (casOrphan)
            await _cas.StoreAsync(new MemoryStream(_bytes), ct: ct);
        await using var db = _database.CreateContext();
        var service = CreateService(db);
        await using var source = new TempFile(_bytes);
        await using var staged = await service.StageAsync(source, ct);
        staged.ArtifactMd5.ShouldBe(Md5);
        staged.ArtifactSha1.ShouldBe(Sha1);
        (await db.Files.CountAsync(ct)).ShouldBe(0);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var result = await service.PublishAndFlushAsync(db, staged, ct);
        await transaction.CommitAsync(ct);

        result.WasNew.ShouldBeTrue();
        result.WasDeduplicated.ShouldBe(casOrphan);
        result.File.ArtifactMd5.ShouldBe(Md5);
        result.File.ArtifactSha1.ShouldBe(Sha1);
        result.File.IsCompressed.ShouldBeTrue();
        result.File.SizeOnDisk.ShouldBeLessThan(_bytes.Length);
        await using var observer = _database.CreateContext();
        var row = await observer.Files.SingleAsync(ct);
        row.ToDomain().ArtifactMd5.ShouldBe(Md5);
        row.ToDomain().ArtifactSha1.ShouldBe(Sha1);
        await using var delivered = await _cas.RetrieveAsync(StorageKey.FromHash(Hash), ct);
        (await MD5.HashDataAsync(delivered!, ct)).ShouldBe(Md5.ToArray());
        await using var deliveredAgain = await _cas.RetrieveAsync(StorageKey.FromHash(Hash), ct);
        (await SHA1.HashDataAsync(deliveredAgain!, ct)).ShouldBe(Sha1.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishStaged_RealRemeasureOfLegacyArtifact_FillsOnlyInCommittedTransaction(bool commit)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        int id = await SeedLegacyAsync(ct);
        await using var db = _database.CreateContext();
        var service = CreateService(db);
        await using var source = new TempFile(_bytes);
        await using var staged = await service.StageAsync(source, ct);
        staged.ArtifactMd5.ShouldBe(Md5);
        staged.ArtifactSha1.ShouldBe(Sha1);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var published = await service.PublishAndFlushAsync(db, staged, ct);
        published.File.Id.ShouldBe(id);
        published.WasNew.ShouldBeFalse();
        published.File.ArtifactMd5.ShouldBe(Md5);
        published.File.ArtifactSha1.ShouldBe(Sha1);
        if (commit)
            await transaction.CommitAsync(ct);
        else
            await transaction.RollbackAsync(ct);

        await using var observer = _database.CreateContext();
        var row = await observer.Files.SingleAsync(ct);
        row.Id.ShouldBe(id);
        row.ArtifactMd5.ShouldBe(commit ? Md5 : null);
        row.ArtifactSha1.ShouldBe(commit ? Sha1 : null);
        (await _cas.VerifyAsync(StorageKey.FromHash(Hash), ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task RecordArtifactChecksumsAsync_OutsidePublicationTransaction_RejectsAndKeepsUnknownFacts()
    {
        int id = await SeedLegacyAsync(CancellationToken.None);
        await using var db = _database.CreateContext();

        var error = await Should.ThrowAsync<InvalidOperationException>(() => new FileRepository(db)
            .RecordArtifactChecksumsAsync(id, Hash, _bytes.Length, Md5, Sha1, CancellationToken.None));

        error.Message.ShouldContain("publication transaction");
        var row = await db.Files.SingleAsync();
        row.ArtifactMd5.ShouldBeNull();
        row.ArtifactSha1.ShouldBeNull();
    }

    [Fact]
    public async Task RecordArtifactChecksumsAsync_PendingTrackedChange_DoesNotFlushCallerOwnedMutation()
    {
        int id = await SeedLegacyAsync(CancellationToken.None);
        await using var db = _database.CreateContext();
        var tracked = await db.Files.AsTracking().SingleAsync(file => file.Id == id);
        long originalDiskSize = tracked.SizeOnDisk;
        tracked.SizeOnDisk++;
        await using var transaction = await db.Database.BeginTransactionAsync();
        await new FileMutationLock(db).AcquireAsync(Hash);

        var measured = await new FileRepository(db).RecordArtifactChecksumsAsync(id, Hash, _bytes.Length, Md5, Sha1, default);

        measured.ArtifactMd5.ShouldBe(Md5);
        measured.ArtifactSha1.ShouldBe(Sha1);
        measured.SizeOnDisk.ShouldBe(originalDiskSize);
        var fresh = await db.Files.AsNoTracking().SingleAsync(file => file.Id == id);
        fresh.SizeOnDisk.ShouldBe(originalDiskSize);
        tracked.ArtifactMd5.ShouldBe(Md5);
        tracked.ArtifactSha1.ShouldBe(Sha1);
        db.Entry(tracked).Property(file => file.SizeOnDisk).IsModified.ShouldBeTrue();
        db.Entry(tracked).Property(file => file.ArtifactMd5).IsModified.ShouldBeFalse();
        db.Entry(tracked).Property(file => file.ArtifactSha1).IsModified.ShouldBeFalse();
        await db.SaveChangesAsync(); // The publication owner chooses when the pending mutation is flushed.
        (await db.Files.AsNoTracking().SingleAsync()).SizeOnDisk.ShouldBe(originalDiskSize + 1);
        await transaction.RollbackAsync();
        await using var observer = _database.CreateContext();
        var rolledBack = await observer.Files.SingleAsync();
        rolledBack.SizeOnDisk.ShouldBe(originalDiskSize);
        rolledBack.ArtifactMd5.ShouldBeNull();
        rolledBack.ArtifactSha1.ShouldBeNull();
    }

    [Fact]
    public async Task RecordArtifactChecksumsAsync_StaleTrackedUnknownFacts_RejectsConflictUsingFreshLedger()
    {
        int id = await SeedLegacyAsync(CancellationToken.None);
        await using var db = _database.CreateContext();
        var stale = await db.Files.AsTracking().SingleAsync(file => file.Id == id);
        stale.ArtifactMd5.ShouldBeNull();
        await using (var writer = _database.CreateContext())
        {
            await using var writerTransaction = await writer.Database.BeginTransactionAsync();
            await new FileMutationLock(writer).AcquireAsync(Hash);
            await writer.Database.ExecuteSqlInterpolatedAsync($"UPDATE romd.\"Files\" SET \"ArtifactMd5\" = {Md5.ToArray()}, \"ArtifactSha1\" = {Sha1.ToArray()} WHERE \"Id\" = {id}");
            await writerTransaction.CommitAsync();
        }
        await using var transaction = await db.Database.BeginTransactionAsync();
        await new FileMutationLock(db).AcquireAsync(Hash);

        await Should.ThrowAsync<InvalidDataException>(() => new FileRepository(db).RecordArtifactChecksumsAsync(
            id, Hash, _bytes.Length, Romd.Domain.Hashing.Md5.Parse(new string('b', 32)), Sha1, default));

        await transaction.RollbackAsync();
        await using var observer = _database.CreateContext();
        var retained = await observer.Files.SingleAsync();
        retained.ArtifactMd5.ShouldBe(Md5);
        retained.ArtifactSha1.ShouldBe(Sha1);
    }

    [Theory]
    [InlineData("ArtifactMd5", 15)]
    [InlineData("ArtifactMd5", 17)]
    [InlineData("ArtifactSha1", 19)]
    [InlineData("ArtifactSha1", 21)]
    public async Task Sql_UpdateUnknownChecksumWithWrongLength_RejectsAndLeavesNull(string column, int length)
    {
        int id = await SeedLegacyAsync(CancellationToken.None);
        await using var db = _database.CreateContext();
        string sql = column switch
        {
            "ArtifactMd5" => "UPDATE romd.\"Files\" SET \"ArtifactMd5\" = @checksum WHERE \"Id\" = @file_id",
            "ArtifactSha1" => "UPDATE romd.\"Files\" SET \"ArtifactSha1\" = @checksum WHERE \"Id\" = @file_id",
            _ => throw new ArgumentOutOfRangeException(nameof(column))
        };

        var error = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql,
            new NpgsqlParameter("checksum", new byte[length]), new NpgsqlParameter("file_id", id)));

        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        var row = await db.Files.SingleAsync();
        row.ArtifactMd5.ShouldBeNull();
        row.ArtifactSha1.ShouldBeNull();
    }

    [Theory]
    [InlineData("\"ArtifactMd5\" = NULL")]
    [InlineData("\"ArtifactSha1\" = NULL")]
    [InlineData("\"ArtifactMd5\" = decode(repeat('ab', 16), 'hex')")]
    [InlineData("\"ArtifactSha1\" = decode(repeat('ab', 20), 'hex')")]
    [InlineData("\"Sha256\" = decode(repeat('ab', 32), 'hex')")]
    [InlineData("\"Size\" = \"Size\" + 1")]
    public async Task Sql_MeasuredGenericArtifactHashOrIdentityTamper_RejectsAndPreservesFacts(string mutation)
    {
        await using var db = _database.CreateContext();
        var file = await new FileRepository(db).CreateAndCommitAsync(db, FileEntity.CreateNew(Hash, _bytes.Length, _bytes.Length,
            false, now: DateTimeOffset.UnixEpoch, Md5, Sha1), CancellationToken.None);

        string sql = "UPDATE romd.\"Files\" SET " + mutation + " WHERE \"Id\" = @file_id";
        var error = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql,
            new NpgsqlParameter("file_id", file.Id)));

        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        var row = await db.Files.SingleAsync();
        row.Sha256.ShouldBe(Hash);
        row.Size.ShouldBe(_bytes.Length);
        row.ArtifactMd5.ShouldBe(Md5);
        row.ArtifactSha1.ShouldBe(Sha1);
        (await db.RomRepresentations.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sql_FillLegacyNullHashes_CommitsOrRollsBackWithoutInventingOtherFacts(bool commit)
    {
        int id = await SeedLegacyAsync(CancellationToken.None);
        await using var db = _database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();

        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE romd.\"Files\" SET \"ArtifactMd5\" = {Md5.ToArray()}, \"ArtifactSha1\" = {Sha1.ToArray()} WHERE \"Id\" = {id}");
        if (commit)
            await transaction.CommitAsync();
        else
            await transaction.RollbackAsync();

        await using var observer = _database.CreateContext();
        var row = await observer.Files.SingleAsync();
        row.Sha256.ShouldBe(Hash);
        row.Size.ShouldBe(_bytes.Length);
        row.ArtifactMd5.ShouldBe(commit ? Md5 : null);
        row.ArtifactSha1.ShouldBe(commit ? Sha1 : null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sql_FillUnknownChecksumAndChangeItsByteIdentity_Rejects(bool changeSize)
    {
        int id = await SeedLegacyAsync(CancellationToken.None);
        await using var db = _database.CreateContext();
        string sql = changeSize
            ? "UPDATE romd.\"Files\" SET \"ArtifactMd5\" = @checksum, \"Size\" = \"Size\" + 1 WHERE \"Id\" = @file_id"
            : "UPDATE romd.\"Files\" SET \"ArtifactMd5\" = @checksum, \"Sha256\" = decode(repeat('ab', 32), 'hex') WHERE \"Id\" = @file_id";

        var error = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql,
            new NpgsqlParameter("checksum", Md5.ToArray()), new NpgsqlParameter("file_id", id)));

        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        var row = await db.Files.SingleAsync();
        row.Sha256.ShouldBe(Hash);
        row.Size.ShouldBe(_bytes.Length);
        row.ArtifactMd5.ShouldBeNull();
        row.ArtifactSha1.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishStaged_PartiallyMeasuredArtifact_PreservesKnownHashAndFillsMissingOne(bool md5Known)
    {
        var stored = await _cas.StoreAsync(new MemoryStream(_bytes));
        await using var db = _database.CreateContext();
        var file = await new FileRepository(db).CreateAndCommitAsync(db, FileEntity.CreateNew(Hash, stored.Size,
            stored.CompressedSize, stored.IsCompressed, now: DateTimeOffset.UnixEpoch, md5Known ? Md5 : null, md5Known ? null : Sha1), CancellationToken.None);
        var service = CreateService(db);
        await using var source = new TempFile(_bytes);
        await using var staged = await service.StageAsync(source);
        await using var transaction = await db.Database.BeginTransactionAsync();

        var result = await service.PublishAndFlushAsync(db, staged);
        await transaction.CommitAsync();

        result.File.Id.ShouldBe(file.Id);
        result.File.ArtifactMd5.ShouldBe(Md5);
        result.File.ArtifactSha1.ShouldBe(Sha1);
        await using var observer = _database.CreateContext();
        var row = await observer.Files.SingleAsync();
        row.ArtifactMd5.ShouldBe(Md5);
        row.ArtifactSha1.ShouldBe(Sha1);
    }

    private async Task<int> SeedLegacyAsync(CancellationToken ct)
    {
        var stored = await _cas.StoreAsync(new MemoryStream(_bytes), ct: ct);
        await using var db = _database.CreateContext();
        var file = await new FileRepository(db).CreateAndCommitAsync(db, FileEntity.CreateNew(stored.Key.Hash, stored.Size,
            stored.CompressedSize, stored.IsCompressed, now: DateTimeOffset.UnixEpoch), ct);
        file.ArtifactMd5.ShouldBeNull();
        file.ArtifactSha1.ShouldBeNull();
        return file.Id;
    }

    private FileStorageService CreateService(RomdDbContext db) => new(_cas, new FileRepository(db),
        new EfUnitOfWork(db), new FileMutationLock(db), Substitute.For<IAdminEventOutbox>(),
        NullLogger<FileStorageService>.Instance, TimeProvider.System);

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _database.DisposeAsync();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class TempFile(byte[] bytes) : ITempFile
    {
        public bool TryBeginChildUse() => true;
        public bool TryEndChildUse() => true;
        public bool Quarantine() => true;
        public string Path => "unused";
        public Sha256 FileSha256 => Sha256.FromSpan(SHA256.HashData(bytes));
        public long FileSize => bytes.Length;
        public Stream OpenRead() => new MemoryStream(bytes);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
