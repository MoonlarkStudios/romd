using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.IngestDat;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Taxonomy;
using Romd.Application.Common.Configuration;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Dat;
using Romd.Domain.Source.Dat.Parsing;
using Romd.Domain.Storage;
using Romd.Domain.Taxonomy;
using Romd.Infrastructure.Catalog;
using Romd.Infrastructure.Storage;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Catalog;
using Romd.Persistence.Repositories;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests;

public sealed partial class AdminMutationOutboxAtomicityTests
{
    [Theory]
    [InlineData("success", "transaction")]
    [InlineData("fault", "transaction")]
    [InlineData("cancel", "transaction")]
    [InlineData("fence", "transaction")]
    [InlineData("commit-before", "transaction")]
    [InlineData("commit-after", "transaction")]
    [InlineData("fault", "header")]
    [InlineData("cancel", "header")]
    [InlineData("business", "header")]
    [InlineData("success", "header")]
    [InlineData("fault", "game")]
    [InlineData("cancel", "game")]
    [InlineData("fence", "game")]
    [InlineData("cancel", "staged")]
    [InlineData("fence", "staged")]
    [InlineData("commit-before", "staged")]
    [InlineData("commit-after", "staged")]
    [InlineData("success", "staged")]
    [InlineData("cancel", "temp")]
    [InlineData("fence", "temp")]
    [InlineData("commit-before", "temp")]
    [InlineData("commit-after", "temp")]
    [InlineData("success", "temp")]
    [InlineData("cancel", "all")]
    [InlineData("success", "all")]
    [InlineData("replay", "temp")]
    public async Task IngestDat_RealSettlementCleanup_PreservesOutcomeAndOriginalInput(string failure, string cleanupScope)
    {
        string root = Path.Combine(Path.GetTempPath(), "romd-dat-owner-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string original = Path.Combine(root, "original.dat");
            await File.WriteAllBytesAsync(original, DatPublicationBytes);
            await using var database = await TestDatabase.CreateAsync();
            var db = database.Context;
            int? sourceId = null;
            if (failure == "replay")
            {
                await SeedPlatformFileAndDatAsync(db, 7, 17, 10);
                sourceId = (await db.DatFiles.SingleAsync()).DatSourceId;
            }
            var claim = await ClaimOutcomeJobAsync(db, JobItemKind.Dat);
            db.ChangeTracker.Clear();
            var resources = new DatRecoveryResources(cleanupScope);
            using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
            using var cancellation = new CancellationTokenSource();
            Exception primary = failure switch
            {
                "cancel" => new OperationCanceledException("Original DAT cancellation.", null, cancellation.Token),
                "fence" => new JobExecutionOwnershipLostException(),
                _ => new IOException("Original DAT failure.")
            };
            var work = new OwnerTransactionFaults(new EfUnitOfWork(db),
                failure.StartsWith("commit-", StringComparison.Ordinal) ? failure : failure is "success" or "replay" or "business" ? "none" : "flush",
                primary, token => db.DatGames.AnyAsync(token), failure == "cancel" ? cancellation.Cancel : null)
            { ObserveDisposal = () => resources.Disposals.Add("transaction") };
            await using var provider = new ServiceCollection().AddContentAddressableStorage(options => options.RootPath = Path.Combine(root, "cas"))
                .BuildServiceProvider();
            var cas = provider.GetRequiredService<IContentAddressableStore>();
            var storage = new FileStorageService(cas, new FileRepository(db), new EfUnitOfWork(db), new FileMutationLock(db),
                database.Outbox, NullLogger<FileStorageService>.Instance, TimeProvider.System);
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(root);
            options.MaxUploadBytes.Returns(1L << 20);
            var reader = Substitute.For<IDatReader>();
            reader.ReadHeaderAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(new DatMetadata
            {
                Name = "Recovery DAT",
                Description = "Recovery DAT",
                DatType = DatType.NoIntro
            });
            if (cleanupScope == "header")
                reader.ReadHeaderAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(_ =>
                {
                    if (failure == "business")
                        return Task.FromResult<ErrorOr.ErrorOr<DatMetadata>>(ErrorOr.Error.Validation("Dat.InvalidHeader", "Invalid DAT header."));
                    if (failure == "success")
                        return Task.FromResult<ErrorOr.ErrorOr<DatMetadata>>(new DatMetadata
                        { Name = "Recovery DAT", Description = "Recovery DAT", DatType = DatType.NoIntro });
                    if (failure == "cancel")
                        cancellation.Cancel();
                    return Task.FromException<ErrorOr.ErrorOr<DatMetadata>>(primary);
                });
            reader.StreamGamesAsync(Arg.Any<Stream>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call =>
                PayloadGames(call.Arg<int>(), 1, null, call.Arg<CancellationToken>()));
            var regions = Substitute.For<ITaxonomyResolver<Region>>();
            regions.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
            var languages = Substitute.For<ITaxonomyResolver<GameLanguage>>();
            languages.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
            var localTempFiles = new LocalTempFileFactory(options, new System.IO.Abstractions.FileSystem());
            var tempFiles = Substitute.For<ITempFileFactory>();
            tempFiles.CreateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>(), Arg.Any<Func<ReadOnlyMemory<byte>, bool>?>(),
                Arg.Any<Func<ReadOnlyMemory<byte>, CancellationToken, Task>?>(), Arg.Any<Func<ReadOnlyMemory<byte>, int>?>())
                .Returns(async call => resources.Wrap(await localTempFiles.CreateAsync(call.Arg<Stream>(), call.Arg<CancellationToken>(),
                    call.ArgAt<Func<ReadOnlyMemory<byte>, bool>?>(2), call.ArgAt<Func<ReadOnlyMemory<byte>, CancellationToken, Task>?>(3),
                    call.ArgAt<Func<ReadOnlyMemory<byte>, int>?>(4))));
            var files = Substitute.For<IFileStorageService>();
            files.StageAsync(Arg.Any<ITempFile>(), Arg.Any<CancellationToken>()).Returns(async call =>
                resources.Wrap(await storage.StageAsync(call.Arg<ITempFile>(), call.Arg<CancellationToken>())));
            files.PublishStagedAsync(Arg.Any<IStagedFile>(), Arg.Any<CancellationToken>()).Returns(call =>
                storage.PublishStagedAsync(DatRecoveryResources.Unwrap(call.Arg<IStagedFile>()), call.Arg<CancellationToken>()));
            files.DeleteOrphanAsync(Arg.Any<Sha256>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                resources.Disposals.ShouldContain("temp");
                resources.Disposals.ShouldContain("staged");
                return storage.DeleteOrphanAsync(call.Arg<Sha256>(), call.Arg<CancellationToken>());
            });
            var handler = new IngestDatCommandHandler(reader, new DatRepository(db, TimeProvider.System),
                new TitleDerivationService(db, new TitleMatcher(new TitleRepository(db), TimeProvider.System), database.UnitOfWork),
                tempFiles, files,
                Substitute.For<IBiosGrouper>(), work, database.Outbox, Substitute.For<ILibraryRepository>(), regions, languages,
                Substitute.For<ICatalogProjectionService>(), Substitute.For<IPlatformHeaderResolver>(),
                NullLogger<IngestDatCommandHandler>.Instance, new SourceLifecycleStore(db), TimeProvider.System, new JobItemRepository(db));
            await using var input = File.OpenRead(original);
            var command = IngestDatCommand.Create(input, "original.dat", replacesDatSourceId: sourceId).Value with
            { UploadItem = new UploadItemContext(claim.Job.Id, "original.dat") };

            if ((failure == "success" && cleanupScope != "header") || failure is "replay" or "business")
            {
                var result = await handler.HandleAsync(command, cancellation.Token);
                if (failure == "business")
                    result.FirstError.Code.ShouldBe("Dat.InvalidHeader");
                else
                {
                    result.IsError.ShouldBeFalse();
                    result.Value.DatFile.GameCount.ShouldBe(1);
                }
                if (failure == "replay")
                {
                    input.Position = 0;
                    var replay = await handler.HandleAsync(command, cancellation.Token);
                    replay.IsError.ShouldBeFalse();
                    replay.Value.DatFile.Id.ShouldBe(result.Value.DatFile.Id);
                    replay.Value.DatFile.Lifecycle.ShouldBe(DatFileLifecycle.PendingActivation);
                }
            }
            else
            {
                Exception? observed = null;
                try
                { await handler.HandleAsync(command, cancellation.Token); }
                catch (Exception error) { observed = error; }
                if (failure.StartsWith("commit-", StringComparison.Ordinal))
                {
                    observed.ShouldBeOfType<UploadOutcomeUncertainException>().InnerException.ShouldBeSameAs(primary);
                    IngestionFailure.Classify(observed!).ShouldBe(IngestionFailureKind.Uncertain);
                }
                else
                    observed.ShouldBeSameAs(failure == "success" ? resources.FailureFor("header") : primary);
                if (observed is OperationCanceledException cancelled)
                    cancelled.CancellationToken.ShouldBe(cancellation.Token);
                work.Injected.ShouldBe(cleanupScope != "header");
                work.RollbackAttempted.ShouldBe(cleanupScope != "header");
            }
            work.Disposed.ShouldBe(cleanupScope != "header");
            if (cleanupScope == "all")
            {
                resources.Disposals.ShouldContain("staged");
                resources.Disposals.ShouldContain("temp");
            }
            else
                resources.Disposals.ShouldContain(cleanupScope);
            resources.TempPaths.ShouldNotBeEmpty();
            resources.TempPaths.ShouldAllBe(path => !File.Exists(path));
            if (cleanupScope != "header")
                resources.Disposals.Where(name => name is "transaction" or "staged" or "temp").ShouldBe(
                    failure == "replay" ? new[] { "transaction", "staged", "temp", "temp" } : new[] { "transaction", "staged", "temp" });
            input.CanRead.ShouldBeTrue();
            (await File.ReadAllBytesAsync(original)).ShouldBe(DatPublicationBytes);
            bool durable = (failure is "success" or "commit-after" or "replay") && cleanupScope != "header";
            if (!durable)
                db.ChangeTracker.Entries().ShouldBeEmpty();
            await db.SaveChangesAsync();
            await using var verify = database.CreateReadContext();
            (await verify.DatFiles.CountAsync()).ShouldBe((durable ? 1 : 0) + (sourceId.HasValue ? 1 : 0));
            (await verify.DatSources.CountAsync()).ShouldBe(durable || sourceId.HasValue ? 1 : 0);
            (await verify.SourceEntries.CountAsync()).ShouldBe(durable ? 1 : 0);
            (await verify.DatGames.CountAsync()).ShouldBe(durable ? 1 : 0);
            (await verify.JobItems.CountAsync()).ShouldBe(durable ? 1 : 0);
            (await GetEventTypesAsync(verify)).ShouldBe(durable ? StatsEventTypes() : []);
            var hash = Sha256.FromSpan(System.Security.Cryptography.SHA256.HashData(DatPublicationBytes));
            (await cas.ExistsAsync(StorageKey.FromHash(hash))).ShouldBe(durable || failure == "cancel" && cleanupScope != "header");
            if (durable)
            {
                var outcome = await new JobItemRepository(verify).FindOutcomeAsync(claim.Job.Id, JobItemKind.Dat, "original.dat");
                outcome.ShouldNotBeNull();
                int fileId = (await verify.Files.SingleAsync(row => row.Sha256 == hash)).Id;
                outcome.DatFileId.ShouldBe((await verify.DatFiles.SingleAsync(row => row.FileId == fileId)).Id);
                outcome.GameCount.ShouldBe(1);
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
