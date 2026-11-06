using System.Security.Cryptography;
using System.Data.Common;
using ErrorOr;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Polly.Registry;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs.Processors;
using Romd.Infrastructure.Resilience;
using Romd.Application.Common.Content;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Commands.IngestRom;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles;
using Romd.Domain.Hashing;
using Romd.Domain.Storage;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Storage;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Repositories;
using Romd.Persistence.MediaFacts;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>
/// The ledger's guarantees against real PostgreSQL and a real content store: every file row has its
/// blob, and a reference commits under the hash lock, so no deletion can interleave with it.
/// </summary>
public sealed class FileLedgerProtocolTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"romd-ledger-{Guid.NewGuid():N}");
    private readonly byte[] _content = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 7)).ToArray();
    private ServiceProvider _services = null!;
    private IContentAddressableStore _cas = null!;

    private Sha256 ContentHash => Sha256.FromSpan(SHA256.HashData(_content));

    public Task InitializeAsync()
    {
        _services = new ServiceCollection().AddContentAddressableStorage(options => options.RootPath = _root)
            .BuildServiceProvider();
        _cas = _services.GetRequiredService<IContentAddressableStore>();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RecordArtifactChecksumsAsync_UnmeasuredArtifact_CapturesOnlyMeasuredImmutableFactsInCallerTransaction()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        int id = await SeedUnreferencedFileAsync(ct);
        var measurement = new TempFileArtifactMeasurement(ContentHash, Md5.FromSpan(MD5.HashData(_content)),
            Sha1.FromSpan(SHA1.HashData(_content)), _content.LongLength);
        await using var db = _database.CreateContext();
        var ledger = CreateService(db, _cas);
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync(ct))
        {
            var measured = await ledger.RecordArtifactChecksumsAsync(id, measurement, ct);
            measured.ArtifactMd5.ShouldBe(measurement.Md5);
            measured.ArtifactSha1.ShouldBe(measurement.Sha1);
            await using var outside = _database.CreateContext();
            (await outside.Files.SingleAsync(row => row.Id == id, ct)).ArtifactMd5.ShouldBeNull();
            await transaction.RollbackAsync(ct);
        }
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync(ct))
        {
            await ledger.RecordArtifactChecksumsAsync(id, measurement, ct);
            await transaction.CommitAsync(ct);
        }
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync(ct))
        {
            await Should.ThrowAsync<InvalidDataException>(() => ledger.RecordArtifactChecksumsAsync(id,
                measurement with { Md5 = Md5.FromSpan(MD5.HashData([1])) }, ct));
            await transaction.RollbackAsync(ct);
        }
        await using var read = _database.CreateContext();
        var retained = await read.Files.SingleAsync(row => row.Id == id, ct);
        retained.ArtifactMd5.ShouldBe(measurement.Md5);
        retained.ArtifactSha1.ShouldBe(measurement.Sha1);
        (await _cas.VerifyAsync(StorageKey.FromHash(ContentHash), ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task IngestRom_SameContentWhileItsFileIsBeingDeleted_LinksARowWhoseBlobExists()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        int seededId = await SeedUnreferencedFileAsync(ct);
        var deleting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingCas = new DeleteGateStore(_cas, deleting, release.Task);
        await using var deleteDb = _database.CreateContext();
        await using var ingestDb = _database.CreateContext();
        await using var observer = _database.CreateContext();
        await ingestDb.Database.OpenConnectionAsync(ct);
        int ingestPid = await BackendPidAsync(ingestDb, ct);

        var deletion = CreateService(deleteDb, blockingCas).DeleteIfUnreferencedAsync(seededId, ct);
        await deleting.Task.WaitAsync(ct);
        var ingestion = CreateIngestHandler(ingestDb).HandleAsync(
            new IngestRomCommand(new MemoryStream(_content), "race.rom", AllowUnidentified: true), ct);
        try
        {
            // Release only once the ingest waits on PostgreSQL, or has already finished without waiting.
            await WaitForLockWaitOrCompletionAsync(observer, ingestPid, ingestion, ct);
        }
        finally
        {
            release.TrySetResult();
        }

        (await deletion).ShouldBeTrue();
        var ingested = await ingestion;
        ingested.IsError.ShouldBeFalse(ingested.IsError ? ingested.FirstError.Description : null);
        var rom = await observer.RomFiles.SingleAsync(row => row.Id == ingested.Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile.Id, ct);
        var preferred = await ReadPreferredAsync(observer, rom.Id, ct);
        rom.PreferredOutputId.ShouldBe(preferred.OutputId);
        var file = await observer.Files.SingleAsync(row => row.Id == preferred.FileId, ct);
        file.Sha256.ShouldBe(ContentHash);
        (await _cas.VerifyAsync(StorageKey.FromHash(ContentHash), ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteIfUnreferencedAsync_RowDeletionCommitFails_KeepsTheRowAndItsBlob()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        int seededId = await SeedUnreferencedFileAsync(ct);
        await using var db = _database.CreateContext();
        var service = CreateService(db, _cas, new FailingFirstCommitUnitOfWork(new EfUnitOfWork(db)));

        await Should.ThrowAsync<InvalidOperationException>(() => service.DeleteIfUnreferencedAsync(seededId, ct));

        await using var observer = _database.CreateContext();
        (await observer.Files.AnyAsync(row => row.Id == seededId, ct)).ShouldBeTrue();
        (await _cas.ExistsAsync(StorageKey.FromHash(ContentHash), ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task PruneOrphanedFilesAsync_DeletesOnlyBlobsWithoutARow()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        await SeedUnreferencedFileAsync(ct);
        var orphans = new List<StorageKey>();
        for (byte seed = 1; seed <= 3; seed++)
            orphans.Add((await _cas.StoreAsync(new MemoryStream(Enumerable.Repeat(seed, 70_000).ToArray()), ct: ct)).Key);
        await using var db = _database.CreateContext();

        int pruned = await CreateService(db, _cas).PruneOrphanedFilesAsync(ct);

        pruned.ShouldBe(orphans.Count);
        foreach (var orphan in orphans)
            (await _cas.ExistsAsync(orphan, ct)).ShouldBeFalse();
        (await _cas.VerifyAsync(StorageKey.FromHash(ContentHash), ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task IngestRom_VerifiedEncodingOfOwnedDisc_ReleasesOwnershipBeforeProofCollectionAndCasCleanup()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        await using var db = _database.CreateContext();
        var command = new IngestRomCommand(new MemoryStream(_content), "disc.iso", AllowUnidentified: true);
        var original = (await CreateIngestHandler(db).HandleAsync(command, ct)).Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile;
        var originalGraph = await ReadPreferredAsync(db, original.Id, ct);
        var originalProof = originalGraph.Facts.Verifications.Single();
        var preparation = Substitute.For<IRomPreparationService>();
        int build = 0;
        preparation.PrepareAsync(Arg.Any<ITempFile>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => PreparedVariant(original, 1, CanonicalContainer, $"rvz-decoder-{++build}"));
        var handler = CreateIngestHandler(db, preparation);
        var added = await handler.HandleAsync(command with { OriginalFilename = "disc.rvz" }, ct);
        added.IsError.ShouldBeFalse(added.IsError ? added.FirstError.Description : null);
        added.Value.IsNew.ShouldBeFalse();
        await using var observer = _database.CreateContext();
        var retained = await ReadPreferredAsync(observer, original.Id, ct);
        retained.ArtifactSha256.ShouldBe(VariantHash(1));
        retained.Encoding.ShouldBe(RomRepresentationEncoding.Rvz);
        added.Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile.FileId.ShouldBe(retained.FileId);
        (await observer.RomFiles.SingleAsync(ct)).PreferredOutputId.ShouldBe(retained.OutputId);
        (await observer.RepresentationOutputs.SingleAsync(row => row.Id == originalGraph.OutputId, ct)).RomId.ShouldBeNull();
        (await observer.Verifications.SingleAsync(row => row.Id == originalProof.Id, ct)).RepresentationId.ShouldBe(originalGraph.RepresentationId);
        var firstMeasurement = retained.Facts.Verifications.Single();
        firstMeasurement.Outputs.Single().Identity.ShouldBe(originalGraph.Output.Identity);
        var encodedFile = await observer.Files.SingleAsync(row => row.Id == retained.FileId, ct);
        encodedFile.ArtifactMd5.ShouldBe(Md5.FromSpan(MD5.HashData(VariantBytes(1))));
        encodedFile.ArtifactSha1.ShouldBe(Sha1.FromSpan(SHA1.HashData(VariantBytes(1))));
        encodedFile.ArtifactMd5.ShouldNotBe(firstMeasurement.Outputs.Single().Identity.Md5);
        encodedFile.ArtifactSha1.ShouldNotBe(firstMeasurement.Outputs.Single().Identity.Sha1);
        var ledger = new FileRepository(observer);
        (await ledger.IsReferencedAsync(original.FileId, ct)).ShouldBeTrue();
        (await CreateService(observer, _cas).DeleteIfUnreferencedAsync(original.FileId, ct)).ShouldBeFalse();

        var repeated = await handler.HandleAsync(command with { OriginalFilename = "disc.rvz" }, ct);
        repeated.IsError.ShouldBeFalse();
        repeated.Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile.FileId.ShouldBe(retained.FileId);
        var measured = await ReadPreferredAsync(observer, original.Id, ct);
        measured.Facts.Verifications.Count.ShouldBe(2);
        measured.Facts.Verifications.ShouldContain(row => row.Id == firstMeasurement.Id);
        measured.Facts.Verifications.Select(row => ((RomDecoderIdentity.Managed)row.Decoder).Build)
            .ShouldBe(new[] { "rvz-decoder-1", "rvz-decoder-2" }, ignoreOrder: true);
        measured.Facts.Verifications.ShouldAllBe(row => row.ArtifactSha256 == VariantHash(1) &&
            row.Outputs.Single().Identity == originalGraph.Output.Identity);
        (await observer.RomRepresentations.CountAsync(ct)).ShouldBe(2);
        (await observer.Verifications.CountAsync(ct)).ShouldBe(3);
        (await new MediaRepresentationCleanup(observer).CollectAsync(256, ct)).ShouldBe(1);
        (await observer.Verifications.AnyAsync(row => row.Id == originalProof.Id, ct)).ShouldBeFalse();
        (await ledger.IsReferencedAsync(original.FileId, ct)).ShouldBeFalse();
        (await observer.Verifications.CountAsync(ct)).ShouldBe(2);
        (await observer.Files.CountAsync(ct)).ShouldBe(2);
        var storage = CreateService(observer, _cas);
        (await storage.DeleteIfUnreferencedAsync(original.FileId, ct)).ShouldBeTrue();
        (await _cas.ExistsAsync(StorageKey.FromHash(ContentHash), ct)).ShouldBeFalse();
        (await observer.Files.CountAsync(ct)).ShouldBe(1);
        (await storage.DeleteIfUnreferencedAsync(retained.FileId, ct)).ShouldBeFalse();
        (await _cas.VerifyAsync(StorageKey.FromHash(VariantHash(1)), ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task IngestRom_ExistingUnidentifiedRomWithoutPermission_DoesNotPublishAlternate()
    {
        await using var db = _database.CreateContext();
        var command = new IngestRomCommand(new MemoryStream(_content), "disc.iso", AllowUnidentified: true);
        var existing = (await CreateIngestHandler(db).HandleAsync(command)).Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile;
        var result = await CreateIngestHandler(db, PrepareVariant(existing, 1, CanonicalContainer))
            .HandleAsync(command with { AllowUnidentified = false });
        result.IsError.ShouldBeFalse();
        result.Value.IsNew.ShouldBeFalse();
        (await db.Files.CountAsync()).ShouldBe(1);
        (await db.RomRepresentations.CountAsync()).ShouldBe(1);
        (await db.Verifications.CountAsync()).ShouldBe(1);
        (await _cas.ExistsAsync(StorageKey.FromHash(VariantHash(1)))).ShouldBeFalse();
    }

    [Fact]
    public async Task IngestRom_ExactIsoAfterRvz_DiscardsLowerRankWithoutChangingPreferredOutput()
    {
        await using var db = _database.CreateContext();
        var identity = LogicalRom();
        var first = await CreateIngestHandler(db, PrepareVariant(identity, 1, CanonicalContainer))
            .HandleAsync(new IngestRomCommand(new MemoryStream(VariantBytes(1)), "disc.rvz", true));
        first.IsError.ShouldBeFalse(first.IsError ? first.FirstError.Description : null);
        var preferred = await ReadPreferredAsync(db, first.Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile.Id);
        var second = await CreateIngestHandler(db).HandleAsync(new IngestRomCommand(new MemoryStream(_content), "disc.iso", true));
        second.IsError.ShouldBeFalse(second.IsError ? second.FirstError.Description : null);
        second.Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile.FileId.ShouldBe(preferred.FileId);
        (await db.RomFiles.SingleAsync()).PreferredOutputId.ShouldBe(preferred.OutputId);
        (await db.RomRepresentations.CountAsync()).ShouldBe(1);
        (await db.Verifications.CountAsync()).ShouldBe(1);
        (await db.RomRepresentations.AnyAsync(row => row.Encoding == RomRepresentationEncoding.Identity)).ShouldBeFalse();
        (await _cas.ExistsAsync(StorageKey.FromHash(ContentHash))).ShouldBeFalse();
    }

    [Fact]
    public async Task IngestRom_ExactRvz_RetainsEveryDecoderBuildAndDeduplicatesSameBuild()
    {
        await using var db = _database.CreateContext();
        var identity = LogicalRom();
        var command = new IngestRomCommand(new MemoryStream(VariantBytes(1)), "disc.rvz", true);
        var first = await CreateIngestHandler(db, PrepareVariant(identity, 1, CanonicalContainer)).HandleAsync(command);
        first.IsError.ShouldBeFalse(first.IsError ? string.Join("; ", first.Errors.Select(error => $"{error.Code}: {error.Description}")) : "");
        var before = await ReadPreferredAsync(db, first.Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile.Id);
        var firstProof = before.Facts.Verifications.Single();
        var preparation = Substitute.For<IRomPreparationService>();
        preparation.PrepareAsync(Arg.Any<ITempFile>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => PreparedVariant(identity, 1, CanonicalContainer, "rvz-decoder-upgraded"));
        var handler = CreateIngestHandler(db, preparation);
        (await handler.HandleAsync(command)).IsError.ShouldBeFalse();
        (await handler.HandleAsync(command)).IsError.ShouldBeFalse();
        await using var observer = _database.CreateContext();
        var after = await ReadPreferredAsync(observer, first.Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile.Id);
        after.OutputId.ShouldBe(before.OutputId);
        after.RepresentationId.ShouldBe(before.RepresentationId);
        after.Container.Fingerprint.ShouldBe(CanonicalContainer.Fingerprint);
        after.Facts.Verifications.Count.ShouldBe(2);
        var preserved = after.Facts.Verifications.Single(row => row.Id == firstProof.Id);
        preserved.ArtifactSha256.ShouldBe(firstProof.ArtifactSha256);
        preserved.ArtifactSize.ShouldBe(firstProof.ArtifactSize);
        preserved.Decoder.ShouldBe(firstProof.Decoder);
        preserved.CheckedProfile.ShouldBe(firstProof.CheckedProfile);
        preserved.VerifierBuild.ShouldBe(firstProof.VerifierBuild);
        preserved.ObservedAt.ShouldBe(firstProof.ObservedAt);
        preserved.ObservedParametersSha256.ShouldBe(firstProof.ObservedParametersSha256);
        preserved.ComputedRawSha1.ShouldBe(firstProof.ComputedRawSha1);
        preserved.ComputedOverallSha1.ShouldBe(firstProof.ComputedOverallSha1);
        preserved.Outputs.ShouldBe(firstProof.Outputs);
        after.Facts.Verifications.Select(row => ((RomDecoderIdentity.Managed)row.Decoder).Build)
            .ShouldBe(new[] { "fixture-rvz-decoder", "rvz-decoder-upgraded" }, ignoreOrder: true);
        (await observer.Files.CountAsync()).ShouldBe(1);
        (await observer.RomRepresentations.CountAsync()).ShouldBe(1);
        (await observer.RomFiles.SingleAsync()).PreferredOutputId.ShouldBe(before.OutputId);
        (await _cas.VerifyAsync(StorageKey.FromHash(VariantHash(1)))).ShouldBeTrue();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConcurrentAlternateImports_ReevaluateRanksUnderRomLock(bool canonicalSecond, bool retainedAlternates)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        await using var seed = _database.CreateContext();
        var original = (await CreateIngestHandler(seed).HandleAsync(new IngestRomCommand(new MemoryStream(_content), "disc.iso", true), ct)).Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile;
        if (retainedAlternates)
            foreach (byte variant in new byte[] { 3, 4 })
                await SeedAlternateAsync(seed, original, variant, OtherContainer, ct);
        await using var lockTransaction = await seed.Database.BeginTransactionAsync(ct);
        await Romd.PostgreSql.TestSupport.MediaTestServices.CreateRomRepository(seed).LockRepresentationSetAsync(original.Id, ct);
        await using var firstDb = _database.CreateContext();
        await using var secondDb = _database.CreateContext();
        await using var observer = _database.CreateContext();
        await firstDb.Database.OpenConnectionAsync(ct);
        await secondDb.Database.OpenConnectionAsync(ct);
        int firstPid = await BackendPidAsync(firstDb, ct);
        int secondPid = await BackendPidAsync(secondDb, ct);
        var first = CreateIngestHandler(firstDb, PrepareVariant(original, 1, OtherContainer))
            .HandleAsync(new IngestRomCommand(new MemoryStream(_content), "first.rvz", true), ct);
        var second = CreateIngestHandler(secondDb, PrepareVariant(original, 2, canonicalSecond ? CanonicalContainer : OtherContainer))
            .HandleAsync(new IngestRomCommand(new MemoryStream(_content), "second.rvz", true), ct);
        await WaitForLockWaitOrCompletionAsync(observer, firstPid, first, ct);
        await WaitForLockWaitOrCompletionAsync(observer, secondPid, second, ct);
        first.IsCompleted.ShouldBeFalse();
        second.IsCompleted.ShouldBeFalse();
        await lockTransaction.CommitAsync(ct);
        foreach (var result in await Task.WhenAll(first, second))
            result.IsError.ShouldBeFalse(result.IsError ? result.FirstError.Description : null);
        var retained = await ReadPreferredAsync(observer, original.Id, ct);
        retained.Container.Fingerprint.ShouldBe((canonicalSecond ? CanonicalContainer : OtherContainer).Fingerprint);
        (await observer.RomFiles.SingleAsync(ct)).PreferredOutputId.ShouldBe(retained.OutputId);
        retained.Facts.Verifications.Count.ShouldBe(1);
        (await observer.RepresentationOutputs.CountAsync(row => row.RomId != null, ct)).ShouldBe(1);
        int graphsBeforeCollection = await observer.RomRepresentations.CountAsync(ct);
        (await new MediaRepresentationCleanup(observer).CollectAsync(256, ct)).ShouldBe(graphsBeforeCollection - 1);
        (await observer.Verifications.CountAsync(ct)).ShouldBe(1);
        if (canonicalSecond)
            (await observer.Files.SingleAsync(row => row.Id == retained.FileId, ct)).Sha256.ShouldBe(VariantHash(2));
        else
        {
            var hash = (await observer.Files.SingleAsync(row => row.Id == retained.FileId, ct)).Sha256;
            new[] { VariantHash(1), VariantHash(2) }.ShouldContain(hash);
        }
        await CreateService(observer, _cas).PruneUnreferencedFilesAsync(TimeSpan.Zero, ct);
        (await observer.Files.CountAsync(ct)).ShouldBe(1);
        (await _cas.ExistsAsync(StorageKey.FromHash(ContentHash), ct)).ShouldBeFalse();
        (await _cas.VerifyAsync(StorageKey.FromHash((await observer.Files.SingleAsync(ct)).Sha256), ct)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentIsoAndRvzUploads_EndWithOneVerifiedRvz(bool initiallyExists)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        await using var seed = _database.CreateContext();
        var hashes = new DatHashResult(Sha1.FromSpan(SHA1.HashData(_content)), Md5.FromSpan(MD5.HashData(_content)), Crc32.FromSpan([1, 2, 3, 4]), _content.Length);
        var identity = RomFile.CreateNew("disc.iso", 0, _content.Length, ContentHash, hashes.Sha1, hashes.Md5, hashes.Crc32, now: DateTimeOffset.UnixEpoch);
        if (initiallyExists)
            identity = (await CreateIngestHandler(seed).HandleAsync(new IngestRomCommand(new MemoryStream(_content), "disc.iso", true), ct)).Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile;
        await using var firstDb = _database.CreateContext();
        await using var secondDb = _database.CreateContext();
        var first = CreateIngestHandler(firstDb).HandleAsync(new IngestRomCommand(new MemoryStream(_content), "disc.iso", true), ct);
        var second = CreateIngestHandler(secondDb, PrepareVariant(identity, 1, OtherContainer))
            .HandleAsync(new IngestRomCommand(new MemoryStream(_content), "disc.rvz", true), ct);
        foreach (var result in await Task.WhenAll(first, second))
            result.IsError.ShouldBeFalse(result.IsError ? result.FirstError.Description : null);
        await using var observer = _database.CreateContext();
        var logical = await observer.RomFiles.SingleAsync(ct);
        var retained = await ReadPreferredAsync(observer, logical.Id, ct);
        retained.Encoding.ShouldBe(RomRepresentationEncoding.Rvz);
        logical.PreferredOutputId.ShouldBe(retained.OutputId);
        retained.Facts.Verifications.Single().ArtifactSha256.ShouldBe(VariantHash(1));
        (await observer.RepresentationOutputs.CountAsync(row => row.RomId != null, ct)).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IngestRom_CanonicalRvz_ReleasesLowerRanksAndDiscardsFurtherAlternates(bool retainedAlternates)
    {
        await using var db = _database.CreateContext();
        var command = new IngestRomCommand(new MemoryStream(_content), "disc.iso", true);
        var original = (await CreateIngestHandler(db).HandleAsync(command)).Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile;
        if (retainedAlternates)
        {
            await SeedAlternateAsync(db, original, 3, OtherContainer, default);
            await SeedAlternateAsync(db, original, 4, RvzContainer(12), default);
        }
        else
            (await CreateIngestHandler(db, PrepareVariant(original, 1, OtherContainer)).HandleAsync(command)).IsError.ShouldBeFalse();
        var canonical = await CreateIngestHandler(db, PrepareVariant(original, 2, CanonicalContainer)).HandleAsync(command);
        canonical.IsError.ShouldBeFalse(canonical.IsError ? canonical.FirstError.Description : null);
        await using var observer = _database.CreateContext();
        var retained = await ReadPreferredAsync(observer, original.Id);
        retained.Container.Fingerprint.ShouldBe(CanonicalContainer.Fingerprint);
        (await new MediaRepresentationCleanup(observer).CollectAsync(256)).ShouldBe(retainedAlternates ? 3 : 2);
        canonical.Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile.FileId.ShouldBe(retained.FileId);
        (await observer.Verifications.SingleAsync()).ArtifactSha256.ShouldBe(VariantHash(2));
        int filesBefore = await observer.Files.CountAsync();
        foreach (var (variant, container) in new[] { ((byte)5, OtherContainer), ((byte)6, CanonicalContainer) })
        {
            var discarded = await CreateIngestHandler(db, PrepareVariant(original, variant, container)).HandleAsync(command);
            discarded.IsError.ShouldBeFalse();
            discarded.Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile.FileId.ShouldBe(retained.FileId);
            (await _cas.ExistsAsync(StorageKey.FromHash(VariantHash(variant)))).ShouldBeFalse();
        }
        (await observer.Files.CountAsync()).ShouldBe(filesBefore);
        (await observer.RomRepresentations.CountAsync()).ShouldBe(1);
        (await observer.Verifications.CountAsync()).ShouldBe(1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IngestRom_ExactRetainedCanonicalAlternate_ArchiveOnlyPublishesStorageEventWithOwnership(bool alreadyPreferred, bool failCommit)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        await using var db = _database.CreateContext();
        var command = new IngestRomCommand(new MemoryStream(_content), "disc.iso", true, ArchiveOnly: true);
        var original = (await CreateIngestHandler(db).HandleAsync(command, ct)).Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile;
        var originalGraph = await ReadPreferredAsync(db, original.Id, ct);
        var target = await SeedAlternateAsync(db, original, 1, CanonicalContainer, ct);
        if (alreadyPreferred)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await MediaTestServices.CreateRomRepository(db).SetPreferredRepresentationStagedAsync(original.Id, target.OutputId, ct);
            await transaction.CommitAsync(ct);
        }
        int preferredBefore = alreadyPreferred ? target.OutputId : originalGraph.OutputId;
        var proofsBefore = await db.Verifications.Select(row => row.Id).ToListAsync(ct);
        int storageEventsBefore = await db.AdminRealtimeOutboxEvents.CountAsync(row => row.EventType == AdminRealtimeEventTypes.StorageStatsChanged, ct);
        int eventsBefore = await db.AdminRealtimeOutboxEvents.CountAsync(ct);
        var preparation = Substitute.For<IRomPreparationService>();
        preparation.PrepareAsync(Arg.Any<ITempFile>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => PreparedVariant(original, 1, CanonicalContainer, "rvz-decoder-upgraded"));
        var handler = CreateIngestHandler(db, preparation,
            failCommit ? new FailingFirstCommitUnitOfWork(new EfUnitOfWork(db)) : null);
        var result = await handler.HandleAsync(command with { OriginalFilename = "disc.rvz" }, ct);
        result.IsError.ShouldBe(failCommit);
        await using var observer = _database.CreateContext();
        if (failCommit)
        {
            (await observer.RomFiles.SingleAsync(ct)).PreferredOutputId.ShouldBe(preferredBefore);
            (await observer.RepresentationOutputs.CountAsync(row => row.RomId == original.Id, ct)).ShouldBe(2);
            (await observer.Verifications.Select(row => row.Id).ToListAsync(ct)).ShouldBe(proofsBefore, ignoreOrder: true);
            (await observer.AdminRealtimeOutboxEvents.CountAsync(ct)).ShouldBe(eventsBefore);
            (await new MediaRepresentationCleanup(observer).CollectAsync(256, ct)).ShouldBe(0);
        }
        else
        {
            result.Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile.FileId.ShouldBe(target.FileId);
            (await observer.RomFiles.SingleAsync(ct)).PreferredOutputId.ShouldBe(target.OutputId);
            (await observer.RepresentationOutputs.SingleAsync(row => row.Id == originalGraph.OutputId, ct)).RomId.ShouldBeNull();
            (await observer.Verifications.CountAsync(ct)).ShouldBe(3);
            (await new FileRepository(observer).IsReferencedAsync(original.FileId, ct)).ShouldBeTrue();
            (await new MediaRepresentationCleanup(observer).CollectAsync(256, ct)).ShouldBe(1);
            (await observer.RomRepresentations.SingleAsync(ct)).Id.ShouldBe(target.RepresentationId);
            (await observer.Verifications.CountAsync(ct)).ShouldBe(2);
            (await new FileRepository(observer).IsReferencedAsync(original.FileId, ct)).ShouldBeFalse();
            (await observer.AdminRealtimeOutboxEvents.CountAsync(row => row.EventType == AdminRealtimeEventTypes.StorageStatsChanged, ct))
                .ShouldBe(storageEventsBefore + 1);
        }
        (await _cas.VerifyAsync(StorageKey.FromHash(ContentHash), ct)).ShouldBeTrue();
        (await _cas.VerifyAsync(StorageKey.FromHash(VariantHash(1)), ct)).ShouldBeTrue();
    }

    private RomContainerFacts.Rvz CanonicalContainer => RvzContainer(19);
    private RomContainerFacts.Rvz OtherContainer => RvzContainer(5);
    private RomContainerFacts.Rvz RvzContainer(int level) => MediaFactFixtures.Rvz(_content.Length, 1, 5, level, 131072, false);
    private static byte[] VariantBytes(byte variant) => [82, 86, 90, 1, variant];
    private static Sha256 VariantHash(byte variant) => Sha256.FromSpan(SHA256.HashData(VariantBytes(variant)));
    private RomFile LogicalRom() => RomFile.CreateNew("disc.iso", 0, _content.Length, ContentHash,
        Sha1.FromSpan(SHA1.HashData(_content)), Md5.FromSpan(MD5.HashData(_content)), Crc32.FromSpan([1, 2, 3, 4]), now: DateTimeOffset.UnixEpoch);

    private IRomPreparationService PrepareVariant(RomFile original, byte variant, RomContainerFacts.Rvz container)
    {
        var preparation = Substitute.For<IRomPreparationService>();
        preparation.PrepareAsync(Arg.Any<ITempFile>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => PreparedVariant(original, variant, container, "fixture-rvz-decoder"));
        return preparation;
    }

    private PreparedRom.WholeFile PreparedVariant(RomFile original, byte variant, RomContainerFacts.Rvz container, string decoderBuild)
    {
        byte[] bytes = VariantBytes(variant);
        var hash = VariantHash(variant);
        return new(new MemoryTempFile(bytes, hash), "disc.iso", ContentHash,
            new(original.Sha1, original.Md5, original.Crc32, _content.Length), RomRepresentationEncoding.Rvz,
            new RomVerification(hash, bytes.Length, RomRepresentationEncoding.Rvz, ContentHash, _content.Length,
                original.Sha1, original.Md5, original.Crc32, "test-rvz/v1", decoderBuild, "fixture-verifier", DateTimeOffset.UtcNow), container);
    }

    private async Task<RomRetainedMeasurement> SeedAlternateAsync(RomdDbContext db, RomFile original, byte variant,
        RomContainerFacts.Rvz container, CancellationToken ct)
    {
        var storage = CreateService(db, _cas);
        await using var prepared = PreparedVariant(original, variant, container, "fixture-rvz-decoder");
        await using var staged = await storage.StageAsync(prepared.Artifact, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var repository = MediaTestServices.CreateRomRepository(db);
        await repository.LockRepresentationSetAsync(original.Id, ct);
        var file = await storage.PublishAndFlushAsync(db, staged, ct);
        var retained = (await repository.RecordWholeMeasurementStagedAsync(original.Id, file.File.Id, container, prepared.Measurement, ct)).ShouldNotBeNull();
        await transaction.CommitAsync(ct);
        return retained;
    }

    private static async Task<RomContentArtifact> ReadPreferredAsync(RomdDbContext db, int romId, CancellationToken ct = default) =>
        (await MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(romId, ct)).ShouldNotBeNull().Preferred;

    [Fact]
    public async Task IngestRom_InspectionCapacityOccupied_PropagatesWithoutPublishing()
    {
        await using var db = _database.CreateContext();
        var pressure = new ResourceCapacityException("decoder occupied");
        var preparation = Substitute.For<IRomPreparationService>();
        preparation.PrepareAsync(Arg.Any<ITempFile>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<ErrorOr<PreparedRom>>(_ => throw pressure);
        var thrown = await Should.ThrowAsync<ResourceCapacityException>(() =>
            CreateIngestHandler(db, preparation).HandleAsync(
                new IngestRomCommand(new MemoryStream(_content), "busy.rvz", AllowUnidentified: true)));
        thrown.ShouldBeSameAs(pressure);
        (await db.Files.CountAsync()).ShouldBe(0);
        (await db.RomFiles.CountAsync()).ShouldBe(0);
        (await db.Verifications.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadPublication_LostCommitAcknowledgement_RecoversOriginalOutcomeWithoutReingesting(bool duplicate)
    {
        await using var db = _database.CreateContext();
        if (duplicate)
            (await CreateIngestHandler(db).HandleAsync(new IngestRomCommand(new MemoryStream(_content),
                "original.rom", AllowUnidentified: true))).IsError.ShouldBeFalse();
        var job = UploadJob.Create("fixture", createdAt: DateTimeOffset.UtcNow);
        job.Start("fixture", startedAt: DateTimeOffset.UtcNow);
        job.BeginClassification();
        job.SetDiscoveredFiles(0, 1);
        job.BeginDatIngestion();
        job.BeginRomIngestion();
        var jobs = new UploadJobRepository(db, TimeProvider.System);
        await using (var fixtureAcceptance = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            await jobs.AddStagedAsync(job);
            await fixtureAcceptance.CommitAsync();
        }
        db.ChangeTracker.Clear();
        var claims = new ClaimedJobRepository<UploadJob, UploadJobRepository>(jobs, db, TimeProvider.System, new());
        var claim = (await claims.TryClaimExecutionAsync(job.Id, "fixture"))!;
        using var fence = JobExecutionFenceScope.Enter(job.Id, claim.FenceToken, TimeProvider.System);
        var publication = new LoseCommitAcknowledgement(new EfUnitOfWork(db));
        var identity = (await Romd.PostgreSql.TestSupport.MediaTestServices.CreateRomRepository(db).GetBySha1Async(Sha1.FromSpan(SHA1.HashData(_content))))
            ?? RomFile.CreateNew("disc.iso", 0, _content.Length, ContentHash, Sha1.FromSpan(SHA1.HashData(_content)),
                Md5.FromSpan(MD5.HashData(_content)), Crc32.FromSpan([1, 2, 3, 4]), now: DateTimeOffset.UnixEpoch);
        var handler = CreateIngestHandler(db, PrepareVariant(identity, 1, CanonicalContainer), publication);
        var services = new ServiceCollection().AddResiliencePolicies();
        services.AddScoped(_ => _database.CreateContext());
        services.AddScoped<IJobItemRepository, JobItemRepository>();
        services.AddSingleton<ICommandHandler<IngestRomCommand, RomIngestResult>>(handler);
        await using var provider = services.BuildServiceProvider();
        var processor = new RomProcessor(provider, provider.GetRequiredService<ResiliencePipelineProvider<string>>(),
            NullLogger<RomProcessor>.Instance);
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "input.rom");
        await File.WriteAllBytesAsync(path, _content);
        var item = new UploadItemContext(job.Id, "folder/input.rom");
        var result = await processor.ProcessAsync(path, true, false, default, uploadItem: item);
        result.Outcome.ShouldBe(duplicate ? RomIngestOutcome.Deduplicated : RomIngestOutcome.Ingested);
        result.JournaledOutcome.ShouldNotBeNull();
        publication.Commits.ShouldBe(1);

        // Neither an executor checkpoint nor its outcome buffer has run. A fresh reader must
        // already observe the content reference, verification and original outcome together.
        await using var observed = _database.CreateContext();
        (await observed.RomFiles.CountAsync()).ShouldBe(1);
        var outcome = await observed.JobItems.SingleAsync();
        outcome.Id.ShouldBe(result.JournaledOutcome.Id);
        outcome.FileName.ShouldBe(item.RelativePath);
        (await observed.Set<Romd.Persistence.Entities.UploadJobEntity>().SingleAsync()).RomsProcessed.ShouldBe(0);
        (await _cas.VerifyAsync(StorageKey.FromHash(VariantHash(1)))).ShouldBeTrue();
        var logical = await observed.RomFiles.SingleAsync();
        var retained = await ReadPreferredAsync(observed, logical.Id);
        retained.Facts.Verifications.Single().ArtifactSha256.ShouldBe(VariantHash(1));
        logical.PreferredOutputId.ShouldBe(retained.OutputId);
        (await observed.Verifications.CountAsync()).ShouldBe(duplicate ? 2 : 1);
        (await new MediaRepresentationCleanup(observed).CollectAsync(256)).ShouldBe(duplicate ? 1 : 0);
        (await observed.Verifications.CountAsync()).ShouldBe(1);
        (await observed.Files.SingleAsync(row => row.Id == retained.FileId)).Sha256.ShouldBe(VariantHash(1));
        File.Delete(path);
        var replay = await processor.ProcessAsync(path, true, false, default, uploadItem: item);
        replay.Outcome.ShouldBe(result.Outcome);
        replay.RomFileId.ShouldBe(result.RomFileId);
        replay.SizeBytes.ShouldBe(result.SizeBytes);
        replay.JournaledOutcome!.Id.ShouldBe(result.JournaledOutcome.Id);
        publication.Commits.ShouldBe(1);
        (await observed.JobItems.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task IngestRom_RankedReplacementBlocksGcUntilReleaseCommits_WithOrderedLedgerLocks()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        await using var seed = _database.CreateContext();
        var command = new IngestRomCommand(new MemoryStream(_content), "disc.iso", true);
        var original = (await CreateIngestHandler(seed).HandleAsync(command, ct)).Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile;
        await using var publishingDb = _database.CreateContext();
        await using var cleanupDb = _database.CreateContext();
        await using var observer = _database.CreateContext();
        await cleanupDb.Database.OpenConnectionAsync(ct);
        int cleanupPid = await BackendPidAsync(cleanupDb, ct);
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var locks = new OrderedLockGate(new FileMutationLock(publishingDb), acquired, release.Task);
        var publication = CreateIngestHandler(publishingDb, PrepareVariant(original, 1, CanonicalContainer), fileLocks: locks)
            .HandleAsync(command, ct);
        await acquired.Task.WaitAsync(ct);
        var cleanup = CreateService(cleanupDb, _cas).DeleteIfUnreferencedAsync(original.FileId, ct);
        try
        {
            await WaitForLockWaitOrCompletionAsync(observer, cleanupPid, cleanup, ct);
            cleanup.IsCompleted.ShouldBeFalse();
            (await ReadPreferredAsync(observer, original.Id, ct)).FileId.ShouldBe(original.FileId);
            (await observer.Verifications.CountAsync(ct)).ShouldBe(1);
            (await _cas.VerifyAsync(StorageKey.FromHash(ContentHash), ct)).ShouldBeTrue();
        }
        finally { release.TrySetResult(); }
        var result = await publication;
        result.IsError.ShouldBeFalse(result.IsError ? result.FirstError.Description : null);
        (await cleanup).ShouldBeFalse();
        (await observer.Files.CountAsync(ct)).ShouldBe(2);
        (await observer.Verifications.CountAsync(ct)).ShouldBe(2);
        (await new MediaRepresentationCleanup(observer).CollectAsync(256, ct)).ShouldBe(1);
        (await CreateService(cleanupDb, _cas).DeleteIfUnreferencedAsync(original.FileId, ct)).ShouldBeTrue();
        locks.Hashes.ShouldBe(new[] { ContentHash, VariantHash(1) }.OrderBy(hash => hash.ToString(), StringComparer.Ordinal));
        (await observer.Files.SingleAsync(ct)).Sha256.ShouldBe(VariantHash(1));
        (await observer.Verifications.SingleAsync(ct)).ArtifactSha256.ShouldBe(VariantHash(1));
        (await _cas.ExistsAsync(StorageKey.FromHash(ContentHash), ct)).ShouldBeFalse();
        (await _cas.VerifyAsync(StorageKey.FromHash(VariantHash(1)), ct)).ShouldBeTrue();
    }

    private sealed class OrderedLockGate(IFileMutationLock inner, TaskCompletionSource acquired, Task release) : IFileMutationLock
    {
        public List<Sha256> Hashes { get; } = [];
        public IFilePublicationBoundary CapturePublicationBoundary() => inner.CapturePublicationBoundary();
        public async Task AcquireAsync(Sha256 hash, CancellationToken ct = default)
        {
            await inner.AcquireAsync(hash, ct);
            Hashes.Add(hash);
            if (Hashes.Count == 2)
            {
                acquired.TrySetResult();
                await release.WaitAsync(ct);
            }
        }
    }

    [Fact]
    public async Task IngestRom_RankedReplacementCommitFails_RestoresOriginalAndCleansOrphanBeforeRetry()
    {
        await using var db = _database.CreateContext();
        var command = new IngestRomCommand(new MemoryStream(_content), "disc.iso", true);
        var original = (await CreateIngestHandler(db).HandleAsync(command)).Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile;
        var oldProofId = (await db.Verifications.SingleAsync()).Id;
        var handler = CreateIngestHandler(db, PrepareVariant(original, 1, CanonicalContainer),
            new FailingFirstCommitUnitOfWork(new EfUnitOfWork(db)));
        var failed = await handler.HandleAsync(command);
        failed.IsError.ShouldBeTrue();
        await using var observer = _database.CreateContext();
        (await ReadPreferredAsync(observer, original.Id)).FileId.ShouldBe(original.FileId);
        (await observer.RomRepresentations.SingleAsync()).FileId.ShouldBe(original.FileId);
        (await observer.Verifications.SingleAsync()).Id.ShouldBe(oldProofId);
        (await observer.Files.CountAsync()).ShouldBe(1);
        (await _cas.VerifyAsync(StorageKey.FromHash(ContentHash))).ShouldBeTrue();
        (await _cas.ExistsAsync(StorageKey.FromHash(VariantHash(1)))).ShouldBeTrue();
        (await CreateService(observer, _cas).DeleteOrphanAsync(VariantHash(1), default)).ShouldBeTrue();
        (await _cas.ExistsAsync(StorageKey.FromHash(VariantHash(1)))).ShouldBeFalse();
        (await handler.HandleAsync(command)).IsError.ShouldBeFalse();
        (await observer.Verifications.CountAsync()).ShouldBe(2);
        (await new MediaRepresentationCleanup(observer).CollectAsync(256)).ShouldBe(1);
        (await observer.RomRepresentations.SingleAsync()).Encoding.ShouldBe(RomRepresentationEncoding.Rvz);
        (await observer.Verifications.SingleAsync()).ArtifactSha256.ShouldBe(VariantHash(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IngestRom_FileIdentityFlushInterrupted_RollsBackBeforeLogicalFactsAndLeavesCollectibleBytes(bool cancel)
    {
        await using var db = _database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        var work = new FileIdentityFlushInterruption(new EfUnitOfWork(db), db, cancel, cancellation);
        var handler = CreateIngestHandler(db, publicationWork: work);
        var command = new IngestRomCommand(new MemoryStream(_content), "disc.iso", true);

        if (cancel)
            (await Should.ThrowAsync<OperationCanceledException>(() => handler.HandleAsync(command, cancellation.Token)))
                .CancellationToken.ShouldBe(cancellation.Token);
        else
            await Should.ThrowAsync<IOException>(() => handler.HandleAsync(command, cancellation.Token));

        work.GeneratedFileIds.ShouldHaveSingleItem();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await db.SaveChangesAsync();
        await using var verification = _database.CreateContext();
        (await verification.Files.CountAsync()).ShouldBe(0);
        (await verification.RomFiles.CountAsync()).ShouldBe(0);
        (await verification.RomRepresentations.CountAsync()).ShouldBe(0);
        (await verification.Verifications.CountAsync()).ShouldBe(0);
        (await verification.JobItems.CountAsync()).ShouldBe(0);
        (await verification.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
        (await _cas.ExistsAsync(StorageKey.FromHash(ContentHash))).ShouldBeTrue();
        (await CreateService(verification, _cas).DeleteOrphanAsync(ContentHash, default)).ShouldBeTrue();
    }

    private sealed class LoseCommitAcknowledgement(IUnitOfWork inner) : IUnitOfWork
    {
        public int Commits { get; private set; }
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);

        public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new LostAcknowledgementTransaction(await inner.BeginTransactionAsync(cancellationToken), this);
        private sealed class LostAcknowledgementTransaction(ITransaction inner, LoseCommitAcknowledgement owner) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                await inner.CommitAsync(cancellationToken);
                owner.Commits++;
                throw new IOException("fixture lost commit acknowledgement");
            }
            public Task RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    [Fact]
    public async Task UploadPublication_OutcomeInsertFails_RollsBackOwnershipAndEvidenceThenRetries()
    {
        var failure = new FailOutcomeInsert();
        await using var db = new RomdDbContext(_database.CreateOptions(builder => builder.AddInterceptors(failure)));
        var job = UploadJob.Create("fixture", createdAt: DateTimeOffset.UtcNow);
        job.Start("fixture", startedAt: DateTimeOffset.UtcNow);
        job.BeginClassification();
        job.SetDiscoveredFiles(0, 1);
        job.BeginDatIngestion();
        job.BeginRomIngestion();
        var jobs = new UploadJobRepository(db, TimeProvider.System);
        await using (var fixtureAcceptance = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            await jobs.AddStagedAsync(job);
            await fixtureAcceptance.CommitAsync();
        }
        db.ChangeTracker.Clear();
        var claims = new ClaimedJobRepository<UploadJob, UploadJobRepository>(jobs, db, TimeProvider.System, new());
        var claim = (await claims.TryClaimExecutionAsync(job.Id, "fixture"))!;
        using var fence = JobExecutionFenceScope.Enter(job.Id, claim.FenceToken, TimeProvider.System);
        var handler = CreateIngestHandler(db);
        var command = new IngestRomCommand(new MemoryStream(_content), "input.rom", AllowUnidentified: true,
            UploadItem: new UploadItemContext(job.Id, "folder/input.rom"));
        var error = await Should.ThrowAsync<DbUpdateException>(() => handler.HandleAsync(command));
        IngestionFailure.Classify(error).ShouldBe(IngestionFailureKind.Capacity);
        failure.Fired.ShouldBeTrue();
        await using (var observed = _database.CreateContext())
        {
            (await observed.RomFiles.CountAsync()).ShouldBe(0);
            (await observed.Files.CountAsync()).ShouldBe(0);
            (await observed.RomRepresentations.CountAsync()).ShouldBe(0);
            (await observed.Verifications.CountAsync()).ShouldBe(0);
            (await observed.JobItems.CountAsync()).ShouldBe(0);
        }
        // Publication may leave an unreferenced CAS object. Its ledger-aware cleanup must
        // remove only those bytes; retry then republishes and commits a complete record.
        await CreateService(db, _cas).DeleteOrphanAsync(ContentHash, default);
        (await _cas.ExistsAsync(StorageKey.FromHash(ContentHash))).ShouldBeFalse();
        (await handler.HandleAsync(command)).IsError.ShouldBeFalse();
        await using var recovered = _database.CreateContext();
        (await recovered.RomFiles.CountAsync()).ShouldBe(1);
        (await recovered.Verifications.CountAsync()).ShouldBe(1);
        (await recovered.JobItems.CountAsync()).ShouldBe(1);
        (await _cas.VerifyAsync(StorageKey.FromHash(ContentHash))).ShouldBeTrue();
    }

    private sealed class FailOutcomeInsert : DbCommandInterceptor
    {
        public bool Fired { get; private set; }
        private void Check(DbCommand command)
        {
            if (!Fired && command.CommandText.Contains("INSERT INTO romd.\"JobItems\"", StringComparison.Ordinal))
            {
                Fired = true;
                throw new IOException("fixture outcome insertion failed");
            }
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return ValueTask.FromResult(result);
        }
    }

    private async Task<int> SeedUnreferencedFileAsync(CancellationToken ct)
    {
        var stored = await _cas.StoreAsync(new MemoryStream(_content), ct: ct);
        await using var db = _database.CreateContext();
        var file = await new FileRepository(db).CreateAndCommitAsync(db,
            FileEntity.CreateNew(stored.Key.Hash, stored.Size, stored.StoredSize, stored.IsCompressed, now: DateTimeOffset.UnixEpoch), ct);
        return file.Id;
    }

    private FileStorageService CreateService(RomdDbContext db, IContentAddressableStore cas, IUnitOfWork? unitOfWork = null) =>
        MediaInfrastructureTestServices.CreateFiles(db, cas, unitOfWork ?? new EfUnitOfWork(db), new FileMutationLock(db), TimeProvider.System);

    private IngestRomCommandHandler CreateIngestHandler(RomdDbContext db, IRomPreparationService? preparation = null,
        IUnitOfWork? publicationWork = null, IFileMutationLock? fileLocks = null)
    {
        var sha1 = Sha1.FromSpan(SHA1.HashData(_content));
        var tempFiles = Substitute.For<ITempFileFactory>();
        tempFiles.CreateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>(), Arg.Any<Func<ReadOnlyMemory<byte>, bool>?>(),
                Arg.Any<Func<ReadOnlyMemory<byte>, CancellationToken, Task>?>(), Arg.Any<Func<ReadOnlyMemory<byte>, int>?>())
            .Returns(new MemoryTempFile(_content, ContentHash));
        var hashing = Substitute.For<IHashingService>();
        hashing.ComputeDatHashesAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new DatHashResult(sha1, Md5.FromSpan(MD5.HashData(_content)), Crc32.FromSpan([1, 2, 3, 4]),
                _content.Length));
        var catalogMatches = Substitute.For<IRomCatalogMatchReader>();
        catalogMatches.ReadAsync(sha1, Arg.Any<CancellationToken>()).Returns(new RomCatalogMatch([], [], []));
        var impact = Substitute.For<IRomPayloadAssertionImpactReader>();
        impact.ReadSourceEntryIdsBySha1Async(sha1, Arg.Any<CancellationToken>()).Returns([]);
        var storage = CreateService(db, _cas);
        var work = publicationWork ?? new EfUnitOfWork(db);
        var locks = fileLocks ?? new FileMutationLock(db);
        var publisher = MediaTestServices.CreatePublisher(db, storage, work, TestRomMediaPolicies.Create(), RomEncodingComposition.CreateRegistry(), locks,
            new JobItemRepository(db), new Romd.Infrastructure.Jobs.JobAuditContext(), TimeProvider.System);
        return new IngestRomCommandHandler(MediaTestServices.CreateRomRepository(db), Substitute.For<IDatRepository>(), catalogMatches,
            tempFiles, preparation ?? MediaInfrastructureTestServices.ManagedPreparation(hashing, () => new RvzVerifier(new()), new MediaInfrastructureTestServices.EmptyReferences()), Substitute.For<Romd.Admin.Application.Source.Rom.Compressed.IRomCdIngestor>(), publisher, new AdminRealtimeOutbox(db, TimeProvider.System),
            Substitute.For<ILibraryRepository>(), Substitute.For<ITrackedTitleReader>(), Substitute.For<ITrackedTitleRepository>(), impact,
            Substitute.For<ITitlePayloadAvailabilityProjection>(), new IngestFailureLogger(output), clock: TimeProvider.System);

    }

    private static Task<int> BackendPidAsync(RomdDbContext db, CancellationToken ct) =>
        db.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync(ct);

    private static async Task WaitForLockWaitOrCompletionAsync(RomdDbContext observer, int pid, Task work,
        CancellationToken ct)
    {
        // Observe PostgreSQL's actual lock queue instead of assuming scheduling from elapsed time.
        while (!work.IsCompleted && !await observer.Database.SqlQuery<bool>(
                   $"SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid = {pid} AND NOT granted) AS \"Value\"")
                   .SingleAsync(ct))
        {
            await Task.Delay(10, ct);
        }
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _database.DisposeAsync();
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    /// <summary>Deletes for real, then holds the deleting caller until released.</summary>
    private sealed class DeleteGateStore(IContentAddressableStore inner, TaskCompletionSource deleting, Task release)
        : IContentAddressableStore
    {
        public async Task<bool> DeleteAsync(StorageKey key, CancellationToken ct = default)
        {
            bool deleted = await inner.DeleteAsync(key, ct);
            deleting.TrySetResult();
            await release.WaitAsync(ct);
            return deleted;
        }

        public Task<IStagedContent> StageAsync(Stream content, StorageKey? expectedKey = null,
            IProgress<StoreProgress>? progress = null, CancellationToken ct = default) =>
            inner.StageAsync(content, expectedKey, progress, ct);
        public Task<StoreResult> StoreAsync(Stream content, IProgress<StoreProgress>? progress = null,
            CancellationToken ct = default) => inner.StoreAsync(content, progress, ct);
        public Task<Stream?> RetrieveAsync(StorageKey key, CancellationToken ct = default) => inner.RetrieveAsync(key, ct);
        public Task<bool> VerifyAsync(StorageKey key, CancellationToken ct = default) => inner.VerifyAsync(key, ct);
        public Task<int> CleanupTemporaryFilesAsync(CancellationToken ct = default) => inner.CleanupTemporaryFilesAsync(ct);
        public Task<IRandomAccessContent?> OpenRandomAccessAsync(StorageKey key, CancellationToken ct = default) =>
            inner.OpenRandomAccessAsync(key, ct);
        public ISynchronousRawContent? OpenSynchronousRaw(StorageKey key) => inner.OpenSynchronousRaw(key);
        public Task<bool> ExistsAsync(StorageKey key, CancellationToken ct = default) => inner.ExistsAsync(key, ct);
        public IAsyncEnumerable<StorageKey> EnumerateAsync(CancellationToken ct = default) => inner.EnumerateAsync(ct);
        public Task<ContentInfo?> GetInfoAsync(StorageKey key, CancellationToken ct = default) => inner.GetInfoAsync(key, ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingFirstCommitUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        private int _commits;

        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);

        public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);

        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(this, await inner.BeginTransactionAsync(cancellationToken));

        private sealed class Transaction(FailingFirstCommitUnitOfWork owner, ITransaction inner) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                if (Interlocked.Increment(ref owner._commits) == 1)
                {
                    await inner.RollbackAsync(CancellationToken.None);
                    throw new InvalidOperationException("Injected commit failure.");
                }
                await inner.CommitAsync(cancellationToken);
            }

            public Task RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private sealed class IngestFailureLogger(ITestOutputHelper output) : ILogger<IngestRomCommandHandler>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            if (IsEnabled(level))
                output.WriteLine(error?.ToString() ?? format(state, error));
        }
    }

    private sealed class MemoryTempFile(byte[] content, Sha256 sha256) : ITempFile
    {
        public bool TryBeginChildUse() => true;
        public bool TryEndChildUse() => true;
        public bool Quarantine() => true;
        public string Path => "memory.tmp";
        public Sha256 FileSha256 { get; } = sha256;
        public long FileSize => content.LongLength;
        public Stream OpenRead() => new MemoryStream(content, writable: false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
