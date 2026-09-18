using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Source;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Source;

public sealed class DatVersionRetentionCoordinatorTests
{
    [Fact]
    public async Task EnforceAsync_WinnerChangesBetweenDiscoveryAndDelete_DirtiesFullSourcePlatformSuperset()
    {
        var repository = Substitute.For<IDatRepository>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        var projection = Substitute.For<ICatalogProjectionService>();
        var materialization = Substitute.For<ILibraryMaterializationService>();
        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        repository.HasSupersededVersionsBeyondRetentionAsync(5, Arg.Any<CancellationToken>())
            .Returns(true);
        repository.GetRoutedPlatformIdsBySourceIdAsync(5, Arg.Any<CancellationToken>())
            .Returns([3, 4]);
        repository.GetCatalogSourceIdAsync(5, Arg.Any<CancellationToken>()).Returns(7);
        bool winnerChanged = false;
        repository.DeleteSupersededVersionsBeyondMostRecentAsync(5, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // Seam: a different version becomes the retained winner after discovery. The
                // discovery was source-wide, so both routed platforms are already covered.
                winnerChanged = true;
                return 2;
            });
        var sourceLifecycle = Substitute.For<ISourceLifecycle>();
        sourceLifecycle.GetLinkedTitleIdsAsync(7, Arg.Any<CancellationToken>())
            .Returns([100, 200]);
        var coordinator = new DatVersionRetentionCoordinator(
            repository, unitOfWork, projection, materialization, sourceLifecycle);

        int deleted = await coordinator.EnforceAsync(5);

        deleted.ShouldBe(2);
        winnerChanged.ShouldBeTrue();
        Received.InOrder(() =>
        {
            repository.HasSupersededVersionsBeyondRetentionAsync(5, Arg.Any<CancellationToken>());
            repository.GetRoutedPlatformIdsBySourceIdAsync(5, Arg.Any<CancellationToken>());
            unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>());
            repository.DeleteSupersededVersionsBeyondMostRecentAsync(5, Arg.Any<CancellationToken>());
            projection.RefreshCatalogSourcePayloadAsync(7, Arg.Any<CancellationToken>());
            projection.MarkPlatformDirtyAsync(
                3,
                Arg.Any<CancellationToken>(),
                Arg.Is<IReadOnlyCollection<int>?>(ids =>
                    ids != null && ids.SequenceEqual(new[] { 100, 200 })));
            materialization.FlagAffectedLibrariesAsync(3, Arg.Any<CancellationToken>());
            projection.MarkPlatformDirtyAsync(
                4,
                Arg.Any<CancellationToken>(),
                Arg.Is<IReadOnlyCollection<int>?>(ids =>
                    ids != null && ids.SequenceEqual(new[] { 100, 200 })));
            materialization.FlagAffectedLibrariesAsync(4, Arg.Any<CancellationToken>());
            transaction.CommitAsync(Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task EnforceAsync_InvalidationFails_RollsBackRetentionDelete()
    {
        var repository = Substitute.For<IDatRepository>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        var projection = Substitute.For<ICatalogProjectionService>();
        var materialization = Substitute.For<ILibraryMaterializationService>();
        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        repository.HasSupersededVersionsBeyondRetentionAsync(5, Arg.Any<CancellationToken>())
            .Returns(true);
        repository.GetRoutedPlatformIdsBySourceIdAsync(5, Arg.Any<CancellationToken>()).Returns([3]);
        repository.DeleteSupersededVersionsBeyondMostRecentAsync(5, Arg.Any<CancellationToken>()).Returns(1);
        projection.MarkPlatformDirtyAsync(
                3,
                Arg.Any<CancellationToken>(),
                Arg.Any<IReadOnlyCollection<int>?>())
            .ThrowsAsync(new InvalidOperationException("dirty failed"));
        var coordinator = new DatVersionRetentionCoordinator(
            repository, unitOfWork, projection, materialization, EmptySourceLifecycle());

        var exception = await Should.ThrowAsync<InvalidOperationException>(coordinator.EnforceAsync(5));

        exception.Message.ShouldBe("dirty failed");
        await transaction.Received(1).RollbackAsync(CancellationToken.None);
        await transaction.DidNotReceiveWithAnyArgs().CommitAsync(default);
    }

    [Fact]
    public async Task EnforceAsync_AtRetentionLimit_ReturnsWithoutOpeningMutationTransaction()
    {
        var repository = Substitute.For<IDatRepository>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        repository.HasSupersededVersionsBeyondRetentionAsync(5, Arg.Any<CancellationToken>())
            .Returns(false);
        var coordinator = new DatVersionRetentionCoordinator(
            repository,
            unitOfWork,
            Substitute.For<ICatalogProjectionService>(),
            Substitute.For<ILibraryMaterializationService>(),
            EmptySourceLifecycle());

        (await coordinator.EnforceAsync(5)).ShouldBe(0);

        await unitOfWork.DidNotReceiveWithAnyArgs().BeginTransactionAsync(default);
        await repository.DidNotReceiveWithAnyArgs()
            .GetRoutedPlatformIdsBySourceIdAsync(default, default);
        await repository.DidNotReceiveWithAnyArgs()
            .DeleteSupersededVersionsBeyondMostRecentAsync(default, default);
    }

    [Fact]
    public async Task EnforceAsync_CompetingWriterAfterDiscovery_DeleteStartsWriteAndCompletesBoundedly()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var commands = new RecordingCommandInterceptor();
        var mutationOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(commands)
            .Options;
        var writerOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(database.ConnectionString)
            .Options;

        try
        {
            await using var mutationDb = new RomdDbContext(mutationOptions);
            await SeedSupersededVersionsAsync(mutationDb);

            var realRepository = new DatRepository(mutationDb, TimeProvider.System);
            var repository = Substitute.For<IDatRepository>();
            var discovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writerCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            repository.GetRoutedPlatformIdsBySourceIdAsync(1, Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    var result = await realRepository.GetRoutedPlatformIdsBySourceIdAsync(
                        1, call.Arg<CancellationToken>());
                    commands.Clear();
                    discovered.TrySetResult();
                    await writerCommitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    return result;
                });
            repository.HasSupersededVersionsBeyondRetentionAsync(1, Arg.Any<CancellationToken>())
                .Returns(call => realRepository.HasSupersededVersionsBeyondRetentionAsync(
                    1, call.Arg<CancellationToken>()));
            repository.DeleteSupersededVersionsBeyondMostRecentAsync(1, Arg.Any<CancellationToken>())
                .Returns(call => realRepository.DeleteSupersededVersionsBeyondMostRecentAsync(
                    1, call.Arg<CancellationToken>()));

            var projection = Substitute.For<ICatalogProjectionService>();
            var materialization = Substitute.For<ILibraryMaterializationService>();
            var coordinator = new DatVersionRetentionCoordinator(
                repository,
                new EfUnitOfWork(mutationDb),
                projection,
                materialization,
                EmptySourceLifecycle());

            var writer = Task.Run(async () =>
            {
                await discovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await using var writerDb = new RomdDbContext(writerOptions);
                int changed = await writerDb.Platforms
                    .Where(platform => platform.Id == 3)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(
                        platform => platform.CatalogRebuildError,
                        "competing recovery"));
                changed.ShouldBe(1);
                writerCommitted.TrySetResult();
            });

            int deleted = await coordinator.EnforceAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
            await writer.WaitAsync(TimeSpan.FromSeconds(5));

            deleted.ShouldBe(2);
            (await mutationDb.DatFiles.AsNoTracking().Select(file => file.Id).ToListAsync()).ShouldBe([4]);
            commands.Commands.ShouldNotBeEmpty();
            commands.Commands[0].ShouldContain("DELETE FROM romd.\"DatFiles\"");
            commands.Commands[0].ShouldContain("SELECT");
            await projection.Received(1).MarkPlatformDirtyAsync(
                3,
                Arg.Any<CancellationToken>(),
                Arg.Any<IReadOnlyCollection<int>?>());
            await materialization.Received(1).FlagAffectedLibrariesAsync(3, Arg.Any<CancellationToken>());
        }
        finally
        {
        }
    }

    private static ISourceLifecycle EmptySourceLifecycle()
    {
        var lifecycle = Substitute.For<ISourceLifecycle>();
        lifecycle.GetLinkedTitleIdsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        return lifecycle;
    }

    private static async Task SeedSupersededVersionsAsync(RomdDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var user = Guid.NewGuid();
        db.Platforms.Add(new PlatformEntity
        {
            Id = 3,
            Name = "Platform",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Platform", BaseCompactLabel = "Platform", CanonicalKey = "platform", ShortName = "platform",
            CreatedAt = now,
            CreatedByUserId = user
        });
        var source = new DatSourceEntity
        {
            Id = 1,
            CatalogSource = new CatalogSourceEntity
            {
                Id = 1,
                Kind = nameof(CatalogSourceKind.Dat),
                Status = nameof(CatalogSourceStatus.Active),
                CreatedAt = now,
                CreatedByUserId = user
            },
            CreatedAt = now,
            CreatedByUserId = user
        };
        foreach (int id in new[] { 2, 3, 4 })
        {
            var bytes = new byte[Sha256.ByteLength];
            bytes[0] = (byte)id;
            db.Files.Add(new FileEntityPersistence
            {
                Id = id,
                Sha256 = Sha256.FromBytes(bytes),
                Size = 1,
                SizeOnDisk = 1,
                CreatedAt = now,
                CreatedByUserId = user
            });
            db.DatFiles.Add(new DatFileEntity
            {
                Id = id,
                Source = source,
                Name = $"DAT {id}",
                Description = $"DAT {id}",
                Type = nameof(DatType.NoIntro),
                PlatformId = 3,
                OriginalFilename = $"{id}.dat",
                FileId = id,
                Lifecycle = nameof(DatFileLifecycle.Superseded),
                // Equal timestamps pin the deterministic higher-id retention tiebreak.
                SupersededAt = now,
                CreatedAt = now,
                CreatedByUserId = user
            });
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _commands = new();

        public IReadOnlyList<string> Commands => _commands.ToArray();

        public void Clear()
        {
            while (_commands.TryDequeue(out _)) { }
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Enqueue(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
