using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Commands.BatchDelete;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.DeleteTitleMedia;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Application.Common.Concurrency;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed class CommittedDeletionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HandleAsync_FileCleanupFailsAfterCommit_PreservesSuccessfulDeletion(bool batch, bool cancelled)
    {
        var fixture = new Fixture(batch);
        Exception failure = cancelled ? new OperationCanceledException() : new InvalidOperationException("cleanup unavailable");
        fixture.Files.DeleteIfUnreferencedAsync(91, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            fixture.Transaction.Committed.ShouldBeTrue();
            return Task.FromException<bool>(failure);
        });

        await fixture.ExecuteSuccessfullyAsync();

        fixture.Transaction.Committed.ShouldBeTrue();
        fixture.Transaction.Disposed.ShouldBeTrue();
        fixture.Warnings.ShouldContain(failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_TransactionDisposeFailsAfterCommit_PreservesResultAndAttemptsFileCleanup(bool batch)
    {
        var fixture = new Fixture(batch);
        var failure = new InvalidOperationException("transaction disposal unavailable");
        fixture.Transaction.DisposeFailure = failure;

        await fixture.ExecuteSuccessfullyAsync();

        fixture.Transaction.Committed.ShouldBeTrue();
        fixture.Transaction.Disposed.ShouldBeTrue();
        fixture.Transaction.RollbackCount.ShouldBe(0);
        await fixture.Files.Received(1).DeleteIfUnreferencedAsync(91, Arg.Any<CancellationToken>());
        fixture.Warnings.ShouldContain(failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_CommitFails_PreservesFailureWithoutFileCleanup(bool batch)
    {
        var fixture = new Fixture(batch);
        fixture.Transaction.CommitFailure = new InvalidOperationException("commit unavailable");

        if (batch)
        {
            var result = await fixture.BatchHandler!.HandleAsync(new BatchDeleteRomsCommand { RomIds = [73] });
            result.IsError.ShouldBeFalse();
            result.Value.DeletedCount.ShouldBe(0);
            result.Value.FailedCount.ShouldBe(1);
        }
        else
        {
            await Should.ThrowAsync<InvalidOperationException>(() => fixture.MediaHandler!.HandleAsync(fixture.MediaCommand));
        }

        fixture.Transaction.Committed.ShouldBeFalse();
        fixture.Transaction.Disposed.ShouldBeTrue();
        await fixture.Files.DidNotReceive().DeleteIfUnreferencedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_CommitCancelled_PropagatesCancellationWithoutFileCleanup(bool batch)
    {
        var fixture = new Fixture(batch);
        using var cancellation = new CancellationTokenSource();
        fixture.Transaction.CommitFailure = new OperationCanceledException(cancellation.Token);

        var failure = await Should.ThrowAsync<OperationCanceledException>(() => fixture.ExecuteSuccessfullyAsync(cancellation.Token));

        failure.CancellationToken.ShouldBe(cancellation.Token);
        fixture.Transaction.Committed.ShouldBeFalse();
        await fixture.Files.DidNotReceive().DeleteIfUnreferencedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private sealed class Fixture
    {
        public IFileStorageService Files { get; } = Substitute.For<IFileStorageService>();
        public RecordingTransaction Transaction { get; } = new();
        public List<Exception> Warnings { get; } = [];
        public BatchDeleteRomsCommandHandler? BatchHandler { get; }
        public DeleteTitleMediaCommandHandler? MediaHandler { get; }
        public DeleteTitleMediaCommand MediaCommand { get; }

        public Fixture(bool batch)
        {
            var unit = Substitute.For<IUnitOfWork>();
            unit.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Transaction);
            Files.DeleteIfUnreferencedAsync(91, Arg.Any<CancellationToken>()).Returns(true);
            var title = Title.Rehydrate(7, 1, "Synthetic title", "synthetic title", null, null, null, null,
                null, null, null, EnrichmentStatus.None, null, DateTimeOffset.UnixEpoch,
                media: [TitleMedia.Rehydrate(11, 7, MediaType.Cover, 91, "user", "image/png", true, null, DateTimeOffset.UnixEpoch)],
                revision: Guid.NewGuid());
            MediaCommand = new(7, 11, RevisionPrecondition.For(title.Revision.ToString()));
            if (batch)
            {
                var roms = Substitute.For<IRomRepository>();
                roms.GetByIdAsync(73, Arg.Any<CancellationToken>()).Returns(RomFile.CreateNew("synthetic.rom", 91,
                    4096, Sha256.Parse(new string('a', 64)), Sha1.Parse(new string('b', 40)),
                    Md5.Parse(new string('c', 32)), Crc32.Parse("12345678"), DateTimeOffset.UnixEpoch));
                BatchHandler = new(roms, Substitute.For<IRomOwnershipImpactReader>(),
                    Substitute.For<IRomPayloadAssertionImpactReader>(), Files, Substitute.For<IAdminEventOutbox>(),
                    unit, Substitute.For<ILibraryRepository>(), Substitute.For<ITitlePayloadAvailabilityProjection>(),
                    new WarningLogger<BatchDeleteRomsCommandHandler>(Warnings));
            }
            else
            {
                var titles = Substitute.For<ITitleAggregateReader>();
                titles.GetAggregateAsync(7, Arg.Any<CancellationToken>()).Returns(title);
                var details = Substitute.For<ITitleDetailReader>();
                details.GetDetailAsync(7, Arg.Any<CancellationToken>()).Returns(new TitleDetailData
                {
                    Id = 7,
                    Revision = title.Revision,
                    PlatformId = 1,
                    Name = title.Name,
                    EnrichmentStatus = "None",
                    IsTracked = false
                });
                MediaHandler = new(TestSystemCatalog.Create(), titles, details, Substitute.For<ITitleMediaStore>(),
                    Substitute.For<IArtworkCurationRepository>(), Files, Substitute.For<IAdminEventOutbox>(), unit,
                    Options.Create(new EnrichmentOptions()), new WarningLogger<DeleteTitleMediaCommandHandler>(Warnings));
            }
        }

        public async Task ExecuteSuccessfullyAsync(CancellationToken ct = default)
        {
            if (BatchHandler is { } batch)
            {
                var result = await batch.HandleAsync(new BatchDeleteRomsCommand { RomIds = [73] }, ct);
                result.IsError.ShouldBeFalse();
                result.Value.DeletedCount.ShouldBe(1);
                result.Value.FailedCount.ShouldBe(0);
                result.Value.Errors.ShouldBeEmpty();
            }
            else
            {
                var result = await MediaHandler!.HandleAsync(MediaCommand, ct);
                result.IsError.ShouldBeFalse();
                result.Value.Name.ShouldBe("Synthetic title");
            }
        }
    }

    private sealed class RecordingTransaction : ITransaction
    {
        public Exception? CommitFailure { get; set; }
        public Exception? DisposeFailure { get; set; }
        public bool Committed { get; private set; }
        public bool Disposed { get; private set; }
        public int RollbackCount { get; private set; }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            if (CommitFailure is { } failure)
                return Task.FromException(failure);
            Committed = true;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RollbackCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return DisposeFailure is { } failure ? new(Task.FromException(failure)) : ValueTask.CompletedTask;
        }
    }

    private sealed class WarningLogger<T>(List<Exception> warnings) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning && exception is not null)
                warnings.Add(exception);
        }
    }
}
