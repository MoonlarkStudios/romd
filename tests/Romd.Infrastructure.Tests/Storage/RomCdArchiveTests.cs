using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Npgsql;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Media;
using Romd.Media.Cd;
using Romd.Media.Chd;
using Romd.Media.TestSupport;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;
using Romd.Storage.FileSystem;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed partial class RomCdArchiveTests : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly string _directory = Directory.CreateTempSubdirectory("romd-cd-archive-").FullName;
    private readonly List<ServiceProvider> _providers = [];
    private static ChdNativeCandidate Native => ChdNativeCandidate.FromBuildRecord(NativeTestBuilds.LibchdrDirectory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishAndRead_CompleteMeasuredOutputs_SharePhysicalCustodyUntilLastRepresentationCollected(bool singleBin)
    {
        var fixture = await FixtureAsync(singleBin);
        await using var db = _database.CreateContext();
        await using var cas = Cas();
        var work = new EfUnitOfWork(db);
        var gate = new PublicationGate(db, fail: false);
        var storage = Storage(cas, db, work);
        var publisher = new RomCdArchivePublisher(storage, work, new MediaFactPublisher(db), new JobItemRepository(db));
        var publishedFirst = await publisher.PublishAsync(fixture.Measured, fixture.Chd, fixture.Stored, gate);
        int first = publishedFirst.Id;
        publishedFirst.Outputs.ShouldAllBe(output => output.Id > 0 && output.RomId > 0);
        publishedFirst.Verifications.Single().Id.ShouldBe(fixture.Measured.Measurement.Id);
        var independent = await FixtureAsync(singleBin);
        int second = (await publisher.PublishAsync(independent.Measured, independent.Chd, independent.Stored, gate)).Id;
        first.ShouldNotBe(second);
        (await db.Files.CountAsync()).ShouldBe(3); // Raw CHD and exact CUE/sidecar; no materialized BINs.
        (await db.RomFiles.CountAsync()).ShouldBe(fixture.Measured.Measurement.Outputs.Count);
        (await db.RepresentationOutputs.CountAsync()).ShouldBe(fixture.Measured.Measurement.Outputs.Count * 2);
        var graphs = await new MediaFactReader(db).ReadAsync([first, second]);
        graphs.Values.SelectMany(graph => graph.Verifications).Select(proof => proof.Id).Distinct().Count().ShouldBe(2);
        foreach (var retained in graphs.Values)
        {
            fixture.Measured.MatchesRetained(retained).ShouldBeTrue();
            retained.Outputs.ShouldAllBe(output => output.RomId != null);
            retained.Verifications.Single().Decoder.ShouldBe(fixture.Measured.Measurement.Decoder);
            foreach (var output in retained.Outputs)
            {
                var content = Content(retained, output);
                await using var stack = new ReaderStack(content, cas);
                await using var stream = (await stack.Reader.OpenVerifiedByRomIdAsync(content.RomId)).ShouldNotBeNull();
                using var copied = new MemoryStream();
                await stream.CopyToAsync(copied);
                copied.ToArray().ShouldBe(fixture.Originals[fixture.Manifest.Members[((RomOutputSelector.Member)output.Selector).Ordinal].Path]);
            }
            foreach (var descriptor in retained.Descriptors)
            {
                var original = fixture.Manifest.Members[descriptor.Ordinal];
                await using var stream = (await cas.RetrieveAsync(StorageKey.FromHash(descriptor.Sha256))).ShouldNotBeNull();
                using var copied = new MemoryStream();
                await stream.CopyToAsync(copied);
                copied.ToArray().ShouldBe(fixture.Originals[original.Path]);
            }
        }
        var ledger = new FileRepository(db);
        var files = await db.Files.ToArrayAsync();
        foreach (var file in files)
        {
            (await ledger.IsReferencedAsync(file.Id, default)).ShouldBeTrue();
            (await storage.DeleteIfUnreferencedAsync(file.Id, default)).ShouldBeFalse();
        }
        (await ledger.GetUnreferencedFileIdsOlderThanAsync(DateTimeOffset.UtcNow.AddDays(1), default)).ShouldBeEmpty();
        (await ledger.GetStorageStatsAsync(default)).Breakdown.Single(category => category.Category == StorageCategories.RomUnmatched).FileCount.ShouldBe(3);
        await db.RepresentationOutputs.Where(row => row.RepresentationId == first).ExecuteUpdateAsync(set => set.SetProperty(row => row.RomId, (int?)null));
        var cleanup = new MediaRepresentationCleanup(db);
        (await cleanup.CollectAsync(256)).ShouldBe(1);
        foreach (var file in files)
            (await storage.DeleteIfUnreferencedAsync(file.Id, default)).ShouldBeFalse();
        await db.RomFiles.ExecuteDeleteAsync();
        (await cleanup.CollectAsync(256)).ShouldBe(1);
        foreach (var file in files)
            (await storage.DeleteIfUnreferencedAsync(file.Id, default)).ShouldBeTrue();
        (await db.Files.CountAsync()).ShouldBe(0);
        Directory.EnumerateFiles(_directory, "*.bin", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task Publish_CompletionFailureAfterFactsFlushed_RollsBackFactsPreferencesAndLedgerTogether()
    {
        var fixture = await FixtureAsync(false);
        await using var db = _database.CreateContext();
        await using var cas = Cas();
        var work = new EfUnitOfWork(db);
        var gate = new PublicationGate(db, fail: true);
        var publisher = new RomCdArchivePublisher(Storage(cas, db, work), work, new MediaFactPublisher(db), new JobItemRepository(db));
        var error = await Should.ThrowAsync<IOException>(() => publisher.PublishAsync(fixture.Measured, fixture.Chd, fixture.Stored, gate));
        error.Message.ShouldBe("Injected completion failure after facts and preferences flushed.");
        gate.Completed.ShouldBeTrue();
        (await db.RomRepresentations.CountAsync()).ShouldBe(0);
        (await db.RepresentationOutputs.CountAsync()).ShouldBe(0);
        (await db.Files.CountAsync()).ShouldBe(0);
        (await db.RomFiles.CountAsync()).ShouldBe(0);
        var storage = Storage(cas, db, work);
        foreach (var hash in fixture.Manifest.Members.OfType<RomCdStoredMember>().Select(member => member.Identity.Sha256).Append(fixture.Chd.FileSha256))
            (await storage.DeleteOrphanAsync(hash, default)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_FileBatchIdentityFlushInterrupted_RollsBackLogicalOwnersAndEveryLedgerRow(bool cancel)
    {
        var fixture = await FixtureAsync(false);
        await using var db = _database.CreateContext();
        await using var cas = Cas();
        using var cancellation = new CancellationTokenSource();
        var work = new FileIdentityFlushInterruption(new EfUnitOfWork(db), db, cancel, cancellation);
        var gate = new PublicationGate(db, fail: false);
        var publisher = new RomCdArchivePublisher(Storage(cas, db, new EfUnitOfWork(db)), work, new MediaFactPublisher(db), new JobItemRepository(db));

        if (cancel)
            (await Should.ThrowAsync<OperationCanceledException>(() => publisher.PublishAsync(fixture.Measured, fixture.Chd,
                fixture.Stored, gate, cancellation.Token))).CancellationToken.ShouldBe(cancellation.Token);
        else
            await Should.ThrowAsync<IOException>(() => publisher.PublishAsync(fixture.Measured, fixture.Chd, fixture.Stored, gate, cancellation.Token));

        work.GeneratedFileIds.Count.ShouldBe(fixture.Stored.Count + 1);
        gate.Completed.ShouldBeFalse();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await db.SaveChangesAsync();
        (await db.Files.CountAsync()).ShouldBe(0);
        (await db.RomFiles.CountAsync()).ShouldBe(0);
        (await db.RomRepresentations.CountAsync()).ShouldBe(0);
        (await db.Verifications.CountAsync()).ShouldBe(0);
        var storage = Storage(cas, db, new EfUnitOfWork(db));
        foreach (var hash in fixture.Stored.Values.Select(file => file.FileSha256).Append(fixture.Chd.FileSha256).Distinct())
            (await storage.DeleteOrphanAsync(hash, default)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("UPDATE romd.\"ChdContainerParameters\" SET \"UnitBytes\" = 2048")]
    [InlineData("UPDATE romd.\"ReconstructionMembers\" SET \"Path\" = 'changed.bin' WHERE \"Ordinal\" = 0")]
    [InlineData("DELETE FROM romd.\"ReconstructionMembers\" WHERE \"Ordinal\" = 0")]
    [InlineData("DELETE FROM romd.\"VerificationOutputs\"")]
    public async Task Database_PublishedFactsMutation_RejectsAndKeepsCompleteArchive(string sql)
    {
        var fixture = await FixtureAsync(true);
        await using var db = _database.CreateContext();
        await using var cas = Cas();
        var work = new EfUnitOfWork(db);
        var gate = new PublicationGate(db, fail: false);
        var published = await new RomCdArchivePublisher(Storage(cas, db, work), work, new MediaFactPublisher(db), new JobItemRepository(db))
            .PublishAsync(fixture.Measured, fixture.Chd, fixture.Stored, gate);
        int id = published.Id;
        await using (var transaction = await work.BeginTransactionAsync())
        {
            var error = await Should.ThrowAsync<PostgresException>(async () => { await db.Database.ExecuteSqlRawAsync(sql); await transaction.CommitAsync(); });
            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        }
        var retained = (await new MediaFactReader(db).ReadAsync([id]))[id];
        fixture.Measured.MatchesRetained(retained).ShouldBeTrue();
        retained.Verifications.Count.ShouldBe(1);
        retained.Descriptors.Count.ShouldBe(2);
    }

    private sealed class PublicationGate(RomdDbContext db, bool fail) : IRomCdArchivePublicationGate
    {
        private readonly Dictionary<string, int> owners = new(StringComparer.Ordinal);
        public bool Completed { get; private set; }
        public async Task<RomCdArchivePublicationAuthority> PrepareAsync(RomMeasuredRepresentation measured, CancellationToken ct)
        {
            db.Database.CurrentTransaction.ShouldNotBeNull();
            // Actual identities are allocated inside publication, in stable content order; no reserved sequence IDs escape staging.
            foreach (var member in ((RomReconstructionManifest.CdTracks)measured.Manifest).Members.OfType<RomCdBinMember>()
                .OrderBy(member => member.Identity.Sha256.ToString(), StringComparer.Ordinal).ThenBy(member => member.Path, StringComparer.Ordinal))
            {
                if (owners.TryGetValue(member.Path, out int id) && await db.RomFiles.AnyAsync(row => row.Id == id, ct))
                    continue;
                var identity = member.Identity;
                var rom = new RomFileEntity
                {
                    OriginalFilename = member.Path,
                    ContentSize = identity.Size,
                    ContentSha256 = identity.Sha256,
                    Sha1 = identity.Sha1,
                    Md5 = identity.Md5,
                    Crc32 = identity.Crc32,
                    CreatedAt = DateTimeOffset.UnixEpoch
                };
                db.RomFiles.Add(rom);
                await db.SaveChangesAsync(ct);
                owners[member.Path] = rom.Id;
            }
            return new RomCdArchivePublicationAuthority.Publish(owners, retained: null);
        }
        public async Task<JobItem?> CompleteAsync(RomRepresentationFacts retained, CancellationToken ct)
        {
            (await db.RepresentationOutputs.CountAsync(row => row.RepresentationId == retained.Id, ct)).ShouldBe(owners.Count);
            foreach (var output in retained.Outputs)
                await db.RomFiles.Where(row => row.Id == output.RomId).ExecuteUpdateAsync(set => set.SetProperty(row => row.PreferredOutputId, output.Id), ct);
            await db.SaveChangesAsync(ct);
            Completed = true;
            if (fail)
                throw new IOException("Injected completion failure after facts and preferences flushed.");
            return null;
        }
    }

    private IContentAddressableStore Cas()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddContentAddressableStorage(options =>
        { options.RootPath = Path.Combine(_directory, "cas"); options.WritePolicy = ContentWritePolicy.Raw; options.MaxContentSize = 1 << 20; });
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider.GetRequiredService<IContentAddressableStore>();
    }
    private static FileStorageService Storage(IContentAddressableStore cas, RomdDbContext db, EfUnitOfWork work) =>
        new(cas, new FileRepository(db), work, new FileMutationLock(db), Substitute.For<IAdminEventOutbox>(), NullLogger<FileStorageService>.Instance, TimeProvider.System);

    private async Task<ArchiveFixture> FixtureAsync(bool singleBin)
    {
        string path = NativeTestBuilds.LibchdrFixture(singleBin ? "cd-edge-single.chd" : "cd-edge-multi.chd");
        var outcome = await ChdCdInspectionService.InspectAsync(path, Native, 2L << 30, 2L << 30);
        var inspection = outcome.Inspection.ShouldNotBeNull();
        Dictionary<string, byte[]> originals = new(StringComparer.Ordinal);
        List<ChdCdMemberVerification> members = [];
        await using var pool = new ChdReadPool(new MediaBlockCache(1 << 20), 2, 1);
        foreach (int number in singleBin ? new[] { 0 } : new[] { 1, 2, 3 })
        {
            int[] tracks = number == 0 ? [1, 2, 3] : [number];
            byte[] source = number == 0 ? File.ReadAllBytes(NativeTestBuilds.LibchdrFixture("cd-edge-single.bin"))
                : File.ReadAllBytes(NativeTestBuilds.LibchdrFixture($"cd-edge-track{number}.bin"));
            var device = await ChdBlockDevice.OpenAsync(new RawFile(path), pool, ChdCdLimits.Preflight);
            await using var projection = await ChdCdBinContent.CreateAsync(device, tracks.Select(n => inspection.Tracks[n - 1])
                .Select(t => new ChdCdBinExtent((long)t.FirstFrame, (int)t.Frames, t.Type == Moonlark.Libchdr.ChdCdTrackType.Audio)).ToArray());
            byte[] decoded = new byte[source.Length];
            await projection.ReadExactlyAtAsync(0, decoded);
            string name = number == 0 ? "cd-edge-single.bin" : $"cd-edge-track{number}.bin";
            originals.Add(name, source);
            decoded.ShouldBe(source);
            members.Add(new(name, "bin", tracks, Hashes(Identity(source)), Hashes(Identity(decoded))));
        }
        // Preserve CRLF, comments and arbitrary non-text sidecars without rewriting.
        byte[] cue = [.. System.Text.Encoding.UTF8.GetBytes("REM original descriptor\r\n"),
            .. File.ReadAllBytes(NativeTestBuilds.LibchdrFixture(singleBin ? "cd-edge-single.cue" : "cd-edge-multi.cue"))];
        originals.Add("disc.cue", cue);
        originals.Add("disc.sbi", [0x53, 0x42, 0x49, 0, 0xff, 0x81]);
        Dictionary<string, ITempFile> stored = new(StringComparer.Ordinal);
        foreach (string name in new[] { "disc.cue", "disc.sbi" })
        {
            string temp = Path.Combine(_directory, name);
            File.WriteAllBytes(temp, originals[name]);
            stored.Add(name, new BorrowedTemp(temp));
            members.Add(new(name, name.EndsWith("cue") ? "cue" : "sidecar", [], Hashes(Identity(originals[name])), null));
        }
        var comparison = new ChdCdVerification(ChdCdVerification.CurrentProfile, Identity(cue).Sha256.ToString(), members, null);
        return new(RomCdMeasurementMapper.Map(inspection, comparison, outcome.NativeIdentity!, "independent-archive-fixture-verifier", DateTimeOffset.UtcNow),
            new BorrowedTemp(path), stored, originals);
    }

    private static RomCdFileIdentity Identity(byte[] bytes) => new(bytes.Length, Sha256.FromSpan(SHA256.HashData(bytes)),
        Sha1.FromSpan(SHA1.HashData(bytes)), Md5.FromSpan(MD5.HashData(bytes)), Crc32.FromUInt32(System.IO.Hashing.Crc32.HashToUInt32(bytes)));
    private sealed record ArchiveFixture(RomMeasuredRepresentation Measured, ITempFile Chd, IReadOnlyDictionary<string, ITempFile> Stored,
        IReadOnlyDictionary<string, byte[]> Originals)
    {
        public RomReconstructionManifest.CdTracks Manifest => (RomReconstructionManifest.CdTracks)Measured.Manifest;
    }
    private static ChdCdFileHashes Hashes(RomCdFileIdentity identity) => new(identity.Size, identity.Sha256.ToString(), identity.Sha1.ToString(), identity.Md5.ToString(), identity.Crc32.ToString());
    private static RomLogicalIdentity Logical(RomCdFileIdentity member) => new(member.Size, member.Sha256, member.Sha1, member.Md5, member.Crc32);
    private sealed class BorrowedTemp(string path) : ITempFile
    {
        public string Path => path;
        public Sha256 FileSha256 { get; } = Sha256.FromSpan(SHA256.HashData(File.ReadAllBytes(path)));
        public long FileSize { get; } = new FileInfo(path).Length;
        public Romd.Admin.Application.Hashing.DatHashResult DatHashes { get; } = HashesOf(path);
        private static Romd.Admin.Application.Hashing.DatHashResult HashesOf(string source)
        { var identity = Identity(File.ReadAllBytes(source)); return new(identity.Sha1, identity.Md5, identity.Crc32, identity.Size); }
        public bool TryBeginChildUse() => false;
        public bool TryEndChildUse() => false;
        public bool Quarantine() => false;
        public Stream OpenRead() => File.OpenRead(path);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class RawFile(string path) : ISynchronousRawContent
    {
        private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _handle = File.OpenHandle(path);
        public long Length { get; } = new FileInfo(path).Length;
        public int ReadAt(long offset, Span<byte> destination) => RandomAccess.Read(_handle, destination, offset);
        public void Dispose() => _handle.Dispose();
    }
    private sealed class GatedRaw(byte[] bytes) : ISynchronousRawContent
    {
        public bool Armed { get; set; }
        public bool Disposed { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public long Length => bytes.Length;
        public int ReadAt(long offset, Span<byte> destination)
        {
            if (Armed && offset >= 124)
            { Entered.TrySetResult(); Release.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue(); }
            ObjectDisposedException.ThrowIf(Disposed, this);
            int count = (int)Math.Min(destination.Length, Math.Max(0, Length - offset));
            bytes.AsSpan((int)Math.Min(offset, Length), count).CopyTo(destination);
            return count;
        }
        public void Dispose() { Disposed = true; Release.Set(); }
    }
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        foreach (var provider in _providers)
            await provider.DisposeAsync();
        _database.Dispose();
        Directory.Delete(_directory, true);
    }
}
