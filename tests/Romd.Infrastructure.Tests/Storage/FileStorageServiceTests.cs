using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Storage;
using Romd.Infrastructure.Storage;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class FileStorageServiceTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(5.5)).AddTicks(1234567);
    private readonly List<string> _events = [];
    private readonly IContentAddressableStore _cas = Substitute.For<IContentAddressableStore>();
    private readonly IFileRepository _files = Substitute.For<IFileRepository>();
    private readonly IFileMutationLock _locks = Substitute.For<IFileMutationLock>();
    private readonly IUnitOfWork _owner = Substitute.For<IUnitOfWork>();
    private int _flushGeneration;

    public FileStorageServiceTests()
    {
        _cas.StageAsync(Arg.Any<Stream>(), Arg.Any<StorageKey?>(), Arg.Any<IProgress<StoreProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                using var bytes = new MemoryStream();
                await call.Arg<Stream>().CopyToAsync(bytes, call.Arg<CancellationToken>());
                return ContentFor(bytes.ToArray());
            });
        _locks.AcquireAsync(Arg.Any<Sha256>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _events.Add($"lock {Prefix(call.Arg<Sha256>())}");
            return Task.CompletedTask;
        });
        _owner.FlushAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _flushGeneration++;
            _events.Add("owner-flush");
            return Task.CompletedTask;
        });
        _files.AddStagedAsync(Arg.Any<FileEntity>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var file = call.Arg<FileEntity>();
            int stagedGeneration = _flushGeneration;
            var created = FileEntity.Rehydrate(Math.Max(1, (int)file.Sha256.AsSpan()[0]), file.Sha256, file.Size, file.SizeOnDisk,
                file.IsCompressed, file.CreatedAt, file.ArtifactMd5, file.ArtifactSha1);
            var receipt = Substitute.For<IStagedFileCreation>();
            receipt.RequiresFlush.Returns(_ => _flushGeneration <= stagedGeneration);
            receipt.ReadAfterFlush().Returns(_ => _flushGeneration > stagedGeneration ? created
                : throw new InvalidOperationException("The test owner has not flushed this file identity."));
            return receipt;
        });
    }

    [Fact]
    public async Task PublishStaged_SeveralFiles_LocksEveryHashInOrdinalOrderBeforePublishingInInputOrder()
    {
        var service = CreateService();
        var files = new List<IStagedFile>();
        foreach (byte first in new byte[] { 0xcc, 0xaa, 0xbb })
            files.Add(await service.StageAsync(StreamFor(first)));

        var results = await PublishAndFlushAsync(service, files);

        var expectedLocks = files.Select(file => file.Sha256).OrderBy(hash => hash.ToString(), StringComparer.Ordinal)
            .Select(hash => $"lock {Prefix(hash)}");
        _events.ShouldBe(expectedLocks.Concat(files.Select(file => $"publish {Prefix(file.Sha256)}")).Append("owner-flush"));
        results.Select(result => result.File.Sha256).ShouldBe(files.Select(file => file.Sha256));
        results.ShouldAllBe(result => result.File.CreatedAt.Ticks == Now.Ticks && result.File.CreatedAt.Offset == Now.Offset);
    }

    [Fact]
    public async Task PublishStaged_FileAlreadyRecorded_ReusesTheRowWithoutAddingOne()
    {
        var service = CreateService();
        await using var staged = await service.StageAsync(StreamFor(0xaa));
        var existing = FileEntity.Rehydrate(5, staged.Sha256, 1, 1, false, DateTimeOffset.UtcNow,
            staged.ArtifactMd5, staged.ArtifactSha1);
        _files.GetBySha256Async(staged.Sha256, Arg.Any<CancellationToken>()).Returns(existing);

        var result = await PublishAndFlushAsync(service, staged);

        result.File.ShouldBeSameAs(existing);
        result.WasNew.ShouldBeFalse();
        await _files.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
        await _files.DidNotReceiveWithAnyArgs().RecordArtifactChecksumsAsync(default, default, default, default, default, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishStagedAsync_RecordedFile_ChecksCapturedBoundaryBeforeReturningAnyReceiptValue(bool batch)
    {
        var service = CreateService();
        await using var staged = await service.StageAsync(StreamFor(0xaa));
        var existing = FileEntity.Rehydrate(5, staged.Sha256, 1, 1, false, Now, staged.ArtifactMd5, staged.ArtifactSha1);
        _files.GetBySha256Async(staged.Sha256, Arg.Any<CancellationToken>()).Returns(existing);
        var boundary = Substitute.For<IFilePublicationBoundary>();
        _locks.CapturePublicationBoundary().Returns(_ => { _events.Add("capture-boundary"); return boundary; });
        bool active = true;
        boundary.When(value => value.EnsureActive()).Do(_ =>
        {
            if (!active)
                throw new InvalidOperationException("The captured transaction ended.");
        });

        IStagedFilePublication? item = null;
        IStagedFilePublicationBatch? items = null;
        if (batch)
            items = await service.PublishStagedAsync([staged]);
        else
            item = await service.PublishStagedAsync(staged);

        bool RequiresFlush() => batch ? items!.RequiresFlush : item!.RequiresFlush;
        FileStoreResult Read() => batch ? items!.ReadAfterFlush().Single() : item!.ReadAfterFlush();
        RequiresFlush().ShouldBeFalse();
        Read().File.ShouldBeSameAs(existing);
        Read().WasNew.ShouldBeFalse();
        Read().WasDeduplicated.ShouldBeTrue();
        active = false;
        Should.Throw<InvalidOperationException>(() => RequiresFlush());
        Should.Throw<InvalidOperationException>(() => Read());
        _locks.Received(1).CapturePublicationBoundary();
        _events.ShouldBe(["capture-boundary", $"lock {Prefix(staged.Sha256)}", $"publish {Prefix(staged.Sha256)}"]);
        await _files.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
    }

    [Fact]
    public async Task PublishStagedAsync_EmptyBatch_StillRequiresItsCapturedBoundary()
    {
        var boundary = Substitute.For<IFilePublicationBoundary>();
        _locks.CapturePublicationBoundary().Returns(boundary);
        var receipt = await CreateService().PublishStagedAsync(Array.Empty<IStagedFile>());
        receipt.RequiresFlush.ShouldBeFalse();
        receipt.ReadAfterFlush().ShouldBeEmpty();

        boundary.When(value => value.EnsureActive()).Do(_ => throw new InvalidOperationException("The captured transaction ended."));

        Should.Throw<InvalidOperationException>(() => receipt.RequiresFlush);
        Should.Throw<InvalidOperationException>(() => receipt.ReadAfterFlush());
        _locks.Received(1).CapturePublicationBoundary();
        await _locks.DidNotReceiveWithAnyArgs().AcquireAsync(default, default);
        _events.ShouldBeEmpty();
    }

    [Fact]
    public async Task PublishStagedAsync_CancelledEmptyBatch_RejectsBeforeCapturingBoundary()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var error = await Should.ThrowAsync<OperationCanceledException>(() =>
            CreateService().PublishStagedAsync(Array.Empty<IStagedFile>(), cancellation.Token));

        error.CancellationToken.ShouldBe(cancellation.Token);
        _locks.DidNotReceive().CapturePublicationBoundary();
        await _locks.DidNotReceiveWithAnyArgs().AcquireAsync(default, default);
        _events.ShouldBeEmpty();
    }

    [Fact]
    public async Task StageAsync_TempArtifactWithDifferentLogicalDatHashes_MeasuresArtifactBytes()
    {
        byte[] bytes = [82, 86, 90, 1, 7, 8, 9];
        await using var source = new TempFile(bytes);
        await using var staged = await CreateService().StageAsync(source);

        staged.ArtifactMd5.ShouldBe(Md5.FromSpan(MD5.HashData(bytes)));
        staged.ArtifactSha1.ShouldBe(Sha1.FromSpan(SHA1.HashData(bytes)));
        staged.ArtifactMd5.ShouldNotBe(source.DatHashes!.Md5);
        staged.ArtifactSha1.ShouldNotBe(source.DatHashes.Sha1);
        var result = await PublishAndFlushAsync(CreateService(), staged);
        result.File.ArtifactMd5.ShouldBe(staged.ArtifactMd5);
        result.File.ArtifactSha1.ShouldBe(staged.ArtifactSha1);
    }

    [Fact]
    public async Task StageAsync_CasDeduplicatedOrphan_MeasuresBeforePublicationAndRecordsHashes()
    {
        byte[] bytes = [1, 3, 5, 7];
        ConfigureSkippedCopy(bytes);
        await using var source = new TempFile(bytes);
        await using var staged = await CreateService().StageAsync(source);

        source.Opened!.BytesRead.ShouldBe(bytes.Length);
        _events.ShouldBeEmpty();
        staged.ArtifactMd5.ShouldBe(Md5.FromSpan(MD5.HashData(bytes)));
        staged.ArtifactSha1.ShouldBe(Sha1.FromSpan(SHA1.HashData(bytes)));
        var result = await PublishAndFlushAsync(CreateService(), staged);
        result.WasNew.ShouldBeTrue();
        result.WasDeduplicated.ShouldBeTrue();
        result.File.ArtifactMd5.ShouldBe(staged.ArtifactMd5);
        result.File.ArtifactSha1.ShouldBe(staged.ArtifactSha1);
    }

    [Fact]
    public async Task StageAsync_SmallIncompressibleArtifactOnFileSystemStore_ReadsOnceAndMeasuresArtifactHashes()
    {
        string root = Path.Combine(Path.GetTempPath(), "romd-ledger", Guid.NewGuid().ToString("N"));
        try
        {
            await using var provider = new ServiceCollection()
                .AddContentAddressableStorage(options => options.RootPath = root)
                .BuildServiceProvider();
            var service = new FileStorageService(provider.GetRequiredService<IContentAddressableStore>(), _files,
                Substitute.For<IUnitOfWork>(), _locks, Substitute.For<IAdminEventOutbox>(),
                NullLogger<FileStorageService>.Instance, clock: TimeProvider.System);
            byte[] bytes = RandomNumberGenerator.GetBytes(4096);
            await using var source = new TempFile(bytes);

            await using var staged = await service.StageAsync(source);

            // The in-memory decision reads to the end once, completing the artifact measurement in that pass.
            source.Opened!.BytesRead.ShouldBe(bytes.Length);
            staged.ArtifactMd5.ShouldBe(Md5.FromSpan(MD5.HashData(bytes)));
            staged.ArtifactSha1.ShouldBe(Sha1.FromSpan(SHA1.HashData(bytes)));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StageAsync_CasDeduplicatedKnownArtifact_PreservesSkipAndImmutableFacts()
    {
        byte[] bytes = [1, 3, 5, 7];
        ConfigureSkippedCopy(bytes);
        await using var source = new TempFile(bytes);
        var existing = FileEntity.Rehydrate(5, source.FileSha256, bytes.Length, bytes.Length, false,
            DateTimeOffset.UtcNow, Md5.FromSpan(MD5.HashData(bytes)), Sha1.FromSpan(SHA1.HashData(bytes)));
        _files.GetBySha256Async(source.FileSha256, Arg.Any<CancellationToken>()).Returns(existing);
        await using var staged = await CreateService().StageAsync(source);

        source.Opened!.BytesRead.ShouldBe(0);
        staged.ArtifactMd5.ShouldBe(existing.ArtifactMd5);
        staged.ArtifactSha1.ShouldBe(existing.ArtifactSha1);
        var result = await PublishAndFlushAsync(CreateService(), staged);
        result.File.ShouldBeSameAs(existing);
        await _files.DidNotReceiveWithAnyArgs().RecordArtifactChecksumsAsync(default, default, default, default, default, default);
    }

    [Fact]
    public async Task PublishStaged_MeasuredLegacyArtifact_FillsOnlyUnknownLedgerFacts()
    {
        var service = CreateService();
        await using var staged = await service.StageAsync(StreamFor(0xaa));
        var existing = FileEntity.Rehydrate(5, staged.Sha256, 1, 1, false, DateTimeOffset.UtcNow);
        _files.GetBySha256Async(staged.Sha256, Arg.Any<CancellationToken>()).Returns(existing);
        var measured = FileEntity.Rehydrate(5, staged.Sha256, 1, 1, false, existing.CreatedAt,
            staged.ArtifactMd5, staged.ArtifactSha1);
        _files.RecordArtifactChecksumsAsync(5, staged.Sha256, 1, staged.ArtifactMd5!.Value,
            staged.ArtifactSha1!.Value, Arg.Any<CancellationToken>()).Returns(measured);

        var result = await PublishAndFlushAsync(service, staged);

        result.File.ShouldBeSameAs(measured);
        result.WasNew.ShouldBeFalse();
        await _files.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
        _events.ShouldBe([$"lock {Prefix(staged.Sha256)}", $"publish {Prefix(staged.Sha256)}"]);
    }

    [Fact]
    public async Task PublishStaged_KnownHashConflictsWithMeasurement_RejectsUnsafeDataWithoutChangingFacts()
    {
        var service = CreateService();
        await using var staged = await service.StageAsync(StreamFor(0xaa));
        var conflicting = FileEntity.Rehydrate(5, staged.Sha256, 1, 1, false, DateTimeOffset.UtcNow,
            Md5.FromSpan(MD5.HashData([0xbb])), staged.ArtifactSha1);
        _files.GetBySha256Async(staged.Sha256, Arg.Any<CancellationToken>()).Returns(conflicting);

        await Should.ThrowAsync<InvalidDataException>(() => PublishAndFlushAsync(service, staged));

        await _files.DidNotReceiveWithAnyArgs().RecordArtifactChecksumsAsync(default, default, default, default, default, default);
        await _files.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
    }

    [Fact]
    public async Task StageAsync_CasSkippedCopyWithFalseExpectedIdentity_RejectsBeforePublication()
    {
        ConfigureSkippedCopy([9, 9, 9]);
        await using var source = new TempFile([1, 2, 3]);

        await Should.ThrowAsync<InvalidDataException>(() => CreateService().StageAsync(source));

        _events.ShouldBeEmpty();
        await _locks.DidNotReceiveWithAnyArgs().AcquireAsync(default, default);
    }

    [Fact]
    public async Task StageRawAsync_TempArtifact_UsesRawCasAndPreservesMeasuredArtifactOwnership()
    {
        byte[] bytes = [77, 67, 111, 109, 112, 114, 72, 68, 7, 8, 9];
        ConfigureRawCopy();
        await using var source = new TempFile(bytes);
        IFileStorageService service = CreateService();
        var staged = await service.StageRawAsync(source);
        var opened = source.Opened ?? throw new InvalidOperationException("Raw staging did not open the artifact.");

        staged.Sha256.ShouldBe(source.FileSha256);
        staged.ArtifactMd5.ShouldBe(Md5.FromSpan(MD5.HashData(bytes)));
        staged.ArtifactSha1.ShouldBe(Sha1.FromSpan(SHA1.HashData(bytes)));
        staged.ArtifactMd5.ShouldNotBe(source.DatHashes!.Md5);
        staged.ArtifactSha1.ShouldNotBe(source.DatHashes.Sha1);
        await _cas.Received(1).StageRawAsync(Arg.Any<Stream>(), StorageKey.FromHash(source.FileSha256),
            Arg.Any<IProgress<StoreProgress>?>(), Arg.Any<CancellationToken>());
        await _cas.DidNotReceiveWithAnyArgs().StageAsync(default!, default, default, default);
        opened.IsDisposed.ShouldBeFalse();
        source.IsDisposed.ShouldBeFalse();

        var result = await PublishAndFlushAsync(service, staged);
        result.File.IsCompressed.ShouldBeFalse();
        result.File.ArtifactMd5.ShouldBe(staged.ArtifactMd5);
        result.File.ArtifactSha1.ShouldBe(staged.ArtifactSha1);
        await staged.DisposeAsync();
        opened.IsDisposed.ShouldBeTrue();
        source.IsDisposed.ShouldBeFalse();
        source.FileSha256.ShouldBe(Sha256.FromSpan(SHA256.HashData(bytes)));
    }

    [Fact]
    public async Task StageRawAsync_CasRefusal_DisposesOpenedStreamWithoutPublishingOrUsingDefaultPolicy()
    {
        var refusal = new NotSupportedException("The existing object is compressed.");
        _cas.StageRawAsync(Arg.Any<Stream>(), Arg.Any<StorageKey?>(), Arg.Any<IProgress<StoreProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IStagedContent>(refusal));
        await using var source = new TempFile([1, 3, 5, 7]);

        var error = await Should.ThrowAsync<NotSupportedException>(() => CreateService().StageRawAsync(source));

        error.ShouldBeSameAs(refusal);
        source.Opened!.IsDisposed.ShouldBeTrue();
        source.IsDisposed.ShouldBeFalse();
        _events.ShouldBeEmpty();
        await _cas.DidNotReceiveWithAnyArgs().StageAsync(default!, default, default, default);
        await _files.DidNotReceiveWithAnyArgs().GetBySha256Async(default, default);
        await _files.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
        await _locks.DidNotReceiveWithAnyArgs().AcquireAsync(default, default);
    }

    [Fact]
    public async Task StageRawAsync_KnownRawDedup_SkipsInputAndPreservesLedgerFacts()
    {
        byte[] bytes = [1, 3, 5, 7];
        var content = ContentFor(bytes, wasDeduplicated: true);
        _cas.StageRawAsync(Arg.Any<Stream>(), Arg.Any<StorageKey?>(), Arg.Any<IProgress<StoreProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(content);
        await using var source = new TempFile(bytes);
        var known = FileEntity.Rehydrate(5, source.FileSha256, bytes.Length, bytes.Length, false, DateTimeOffset.UtcNow,
            Md5.FromSpan(MD5.HashData(bytes)), Sha1.FromSpan(SHA1.HashData(bytes)));
        _files.GetBySha256Async(source.FileSha256, Arg.Any<CancellationToken>()).Returns(known);
        var service = CreateService();
        var staged = await service.StageRawAsync(source);
        var opened = source.Opened ?? throw new InvalidOperationException("Raw staging did not open the artifact.");

        opened.BytesRead.ShouldBe(0);
        staged.ArtifactMd5.ShouldBe(known.ArtifactMd5);
        staged.ArtifactSha1.ShouldBe(known.ArtifactSha1);
        var result = await PublishAndFlushAsync(service, staged);

        result.File.ShouldBeSameAs(known);
        result.WasDeduplicated.ShouldBeTrue();
        opened.BytesRead.ShouldBe(0);
        await _cas.DidNotReceiveWithAnyArgs().StageAsync(default!, default, default, default);
        await _files.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
        await _files.DidNotReceiveWithAnyArgs().RecordArtifactChecksumsAsync(default, default, default, default, default, default);
        await staged.DisposeAsync();
        await content.Received(1).DisposeAsync();
        opened.IsDisposed.ShouldBeTrue();
        source.IsDisposed.ShouldBeFalse();
    }

    [Fact]
    public async Task PublishStaged_RawStaged_CasPublicationRefusal_LeavesLedgerUnchangedAndReleasesOnDispose()
    {
        byte[] bytes = [1, 3, 5, 7];
        var refusal = new NotSupportedException("Another writer published compressed content.");
        var content = ContentFor(bytes);
        content.PublishAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<StoreResult>(refusal));
        ConfigureRawCopy(content);
        await using var source = new TempFile(bytes);
        var service = CreateService();
        var staged = await service.StageRawAsync(source);

        var error = await Should.ThrowAsync<NotSupportedException>(() => PublishAndFlushAsync(service, staged));

        error.ShouldBeSameAs(refusal);
        _events.ShouldBe([$"lock {Prefix(source.FileSha256)}"]);
        await _files.DidNotReceiveWithAnyArgs().GetBySha256Async(default, default);
        await _files.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
        await _files.DidNotReceiveWithAnyArgs().RecordArtifactChecksumsAsync(default, default, default, default, default, default);
        await staged.DisposeAsync();
        await content.Received(1).DisposeAsync();
        source.Opened!.IsDisposed.ShouldBeTrue();
        source.IsDisposed.ShouldBeFalse();
    }

    [Fact]
    public async Task StageRawAsync_CasSkippedCopyWithFalseIdentity_RefusesAndReleasesOnlyOwnedResources()
    {
        var content = ContentFor([9, 9, 9], wasDeduplicated: true);
        _cas.StageRawAsync(Arg.Any<Stream>(), Arg.Any<StorageKey?>(), Arg.Any<IProgress<StoreProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(content);
        await using var source = new TempFile([1, 2, 3]);

        await Should.ThrowAsync<InvalidDataException>(() => CreateService().StageRawAsync(source));

        await content.Received(1).DisposeAsync();
        source.Opened!.IsDisposed.ShouldBeTrue();
        source.IsDisposed.ShouldBeFalse();
        _events.ShouldBeEmpty();
        await _cas.DidNotReceiveWithAnyArgs().StageAsync(default!, default, default, default);
        await _files.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
        await _locks.DidNotReceiveWithAnyArgs().AcquireAsync(default, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageRawAsync_ChdOutputInterruptedMidCopy_LeavesNoStagingAndRetryPublishesOneRawObject(bool cancelled)
    {
        string root = Path.Combine(Path.GetTempPath(), "romd-raw-stage-interrupt", Guid.NewGuid().ToString("N"));
        string staging = Path.Combine(root, ".staging");
        await using var provider = new ServiceCollection().AddContentAddressableStorage(options => options.RootPath = root)
            .BuildServiceProvider();
        try
        {
            var cas = provider.GetRequiredService<IContentAddressableStore>();
            using var database = PostgreSqlTestDatabase.Create();
            await using var db = database.CreateContext();
            var owner = new EfUnitOfWork(db);
            var service = new FileStorageService(cas, new FileRepository(db), owner, new FileMutationLock(db),
                Substitute.For<IAdminEventOutbox>(), NullLogger<FileStorageService>.Instance, clock: TimeProvider.System);
            // Compressible CHD-shaped output: the default policy would compress it, so the object must stay raw.
            byte[] bytes = new byte[1 << 20];
            "MComprHD"u8.CopyTo(bytes);
            var key = StorageKey.FromHash(Sha256.FromSpan(SHA256.HashData(bytes)));
            using var cancellation = new CancellationTokenSource();
            bool stagingWhenInterrupted = false;
            await using var interrupted = new InterruptedTempFile(bytes, 256 << 10, () =>
            {
                stagingWhenInterrupted = Directory.EnumerateFiles(staging, ".tmp-*").Any();
                if (!cancelled)
                    throw new IOException("worker failed during raw publication staging");
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            });

            if (cancelled)
                await Should.ThrowAsync<OperationCanceledException>(() => service.StageRawAsync(interrupted, cancellation.Token));
            else
                await Should.ThrowAsync<IOException>(() => service.StageRawAsync(interrupted));

            stagingWhenInterrupted.ShouldBeTrue("the interruption must land inside the raw staging copy");
            Directory.EnumerateFiles(staging, ".tmp-*").ShouldBeEmpty();
            (await cas.GetInfoAsync(key)).ShouldBeNull();
            interrupted.Opened.ShouldNotBeNull().IsDisposed.ShouldBeTrue();
            // The interruption above disposed its staging file; a worker that dies mid-copy would instead leave an unlocked
            // partial file, planted here, which the next worker start removes.
            string abandoned = Path.Combine(staging, $".tmp-{Guid.NewGuid():N}");
            await File.WriteAllBytesAsync(abandoned, bytes.AsSpan(0, 256 << 10).ToArray());
            using (var cleanup = new ContentStoreStartupCleanup(cas, NullLogger<ContentStoreStartupCleanup>.Instance))
            {
                await cleanup.StartAsync(CancellationToken.None);
                await cleanup.ExecuteTask!;
            }
            File.Exists(abandoned).ShouldBeFalse();

            await using var retry = new TempFile(bytes);
            FileStoreResult published;
            await using (var staged = await service.StageRawAsync(retry))
            {
                await using var transaction = await owner.BeginTransactionAsync();
                var publication = await service.PublishStagedAsync(staged);
                if (publication.RequiresFlush)
                    await owner.FlushAsync();
                published = publication.ReadAfterFlush();
                await transaction.CommitAsync();
            }

            published.File.Sha256.ShouldBe(key.Hash);
            published.File.IsCompressed.ShouldBeFalse();
            published.File.SizeOnDisk.ShouldBe(published.File.Size);
            var info = (await cas.GetInfoAsync(key)).ShouldNotBeNull();
            info.IsCompressed.ShouldBeFalse();
            var objects = new List<StorageKey>();
            await foreach (var stored in cas.EnumerateAsync())
                objects.Add(stored);
            objects.ShouldBe([key]);
            (await db.Files.CountAsync()).ShouldBe(1);
            (await db.Files.SingleAsync()).Sha256.ShouldBe(key.Hash);
            Directory.EnumerateFiles(staging, ".tmp-*").ShouldBeEmpty();
            (await cas.VerifyAsync(key)).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private void ConfigureRawCopy(IStagedContent? content = null) =>
        _cas.StageRawAsync(Arg.Any<Stream>(), Arg.Any<StorageKey?>(), Arg.Any<IProgress<StoreProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                using var bytes = new MemoryStream();
                await call.Arg<Stream>().CopyToAsync(bytes, call.Arg<CancellationToken>());
                return content ?? ContentFor(bytes.ToArray());
            });

    private async Task<FileStoreResult> PublishAndFlushAsync(IFileStorageService storage, IStagedFile staged)
    {
        var publication = await storage.PublishStagedAsync(staged);
        if (publication.RequiresFlush)
            await _owner.FlushAsync();
        return publication.ReadAfterFlush();
    }

    private async Task<IReadOnlyList<FileStoreResult>> PublishAndFlushAsync(IFileStorageService storage, IReadOnlyList<IStagedFile> staged)
    {
        var publication = await storage.PublishStagedAsync(staged);
        if (publication.RequiresFlush)
            await _owner.FlushAsync();
        return publication.ReadAfterFlush();
    }

    private FileStorageService CreateService() => new(_cas, _files, _owner, _locks,
        Substitute.For<IAdminEventOutbox>(), NullLogger<FileStorageService>.Instance, new ManualTimeProvider(Now));

    private static Stream StreamFor(byte first) => new MemoryStream([first]);

    private void ConfigureSkippedCopy(byte[] bytes)
    {
        var content = ContentFor(bytes, wasDeduplicated: true);
        _cas.StageAsync(Arg.Any<Stream>(), Arg.Any<StorageKey?>(), Arg.Any<IProgress<StoreProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(content);
    }

    private IStagedContent ContentFor(byte[] bytes, bool wasDeduplicated = false)
    {
        var key = StorageKey.FromHash(Sha256.FromSpan(SHA256.HashData(bytes)));
        var content = Substitute.For<IStagedContent>();
        content.Key.Returns(key);
        content.Size.Returns(bytes.Length);
        content.PublishAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _events.Add($"publish {Prefix(key.Hash)}");
            return new StoreResult(key, bytes.Length, bytes.Length, WasDeduplicated: wasDeduplicated, IsCompressed: false);
        });
        return content;
    }

    private sealed class TempFile(byte[] bytes) : ITempFile
    {
        public bool TryBeginChildUse() => true;
        public bool TryEndChildUse() => true;
        public bool Quarantine() => true;
        public string Path => "unused";
        public Sha256 FileSha256 => Sha256.FromSpan(SHA256.HashData(bytes));
        public long FileSize => bytes.Length;
        public Romd.Admin.Application.Hashing.DatHashResult? DatHashes => new(
            Sha1.FromSpan(SHA1.HashData([0])), Md5.FromSpan(MD5.HashData([0])), Crc32.Parse("01020304"), 99);
        public CountingStream? Opened { get; private set; }
        public bool IsDisposed { get; private set; }
        public Stream OpenRead() => Opened = new(bytes);
        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A staged artifact whose read faults once <paramref name="faultAt" /> bytes have been delivered.</summary>
    private sealed class InterruptedTempFile(byte[] bytes, long faultAt, Action fault) : ITempFile
    {
        public bool TryBeginChildUse() => true;
        public bool TryEndChildUse() => true;
        public bool Quarantine() => true;
        public string Path => "unused";
        public Sha256 FileSha256 => Sha256.FromSpan(SHA256.HashData(bytes));
        public long FileSize => bytes.Length;
        public InterruptedStream? Opened { get; private set; }
        public Stream OpenRead() => Opened = new(bytes, faultAt, fault);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InterruptedStream(byte[] bytes, long faultAt, Action fault) : MemoryStream(bytes, writable: false)
    {
        public bool IsDisposed { get; private set; }
        // A derived MemoryStream routes span reads through this array overload.
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= faultAt)
                fault();
            return base.Read(buffer, offset, (int)Math.Min(count, faultAt - Position));
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromResult(Read(buffer.Span));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            Task.FromResult(Read(buffer, offset, count));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public long BytesRead { get; private set; }
        public bool IsDisposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            int read = await base.ReadAsync(buffer, ct);
            BytesRead += read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private static string Prefix(Sha256 hash) => hash.ToString()[..2];
}
