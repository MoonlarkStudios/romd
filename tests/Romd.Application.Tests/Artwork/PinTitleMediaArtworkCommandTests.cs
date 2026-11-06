using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Commands.PinTitleMediaArtwork;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Concurrency;
using Romd.Domain.Catalog;
using Romd.Domain.Storage;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Artwork;

public sealed class PinTitleMediaArtworkCommandTests
{
    private static readonly PinTitleMediaArtworkCommand Command = new(1, ArtworkRole.Hero, 7, RevisionPrecondition.For("0"));

    [Theory]
    [InlineData(-1, 50, ArtworkRole.Hero)]
    [InlineData(101, 50, ArtworkRole.Hero)]
    [InlineData(50, -1, ArtworkRole.Hero)]
    [InlineData(50, 101, ArtworkRole.Hero)]
    [InlineData(50, 50, (ArtworkRole)99)]
    public async Task HandleAsync_InvalidRoleOrFocal_RejectsBeforeReading(int x, int y, ArtworkRole role)
    {
        var fixture = new Fixture();

        (await fixture.Handler.HandleAsync(Command with { FocalX = x, FocalY = y, Role = role })).FirstError.Code
            .ShouldBe("Artwork.InvalidRequest");

        fixture.Repository.ReceivedCalls().ShouldBeEmpty();
        fixture.Work.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleAsync_MissingMediaOrBlob_RejectsBeforeProcessingOrTransaction(bool missingMedia)
    {
        var fixture = new Fixture();
        if (missingMedia)
            fixture.Repository.FindMediaAsync(1, 7, Arg.Any<CancellationToken>()).Returns((TitleMedia?)null);
        else
            fixture.Files.RetrieveByIdAsync(12, Arg.Any<CancellationToken>()).Returns((Stream?)null);

        (await fixture.Handler.HandleAsync(Command)).FirstError.Code.ShouldBe("Artwork.InvalidRequest");

        fixture.Processor.ReceivedCalls().ShouldBeEmpty();
        fixture.Work.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_EncodedLimitExceeded_RejectsBeforeProcessingOrTransaction()
    {
        var fixture = new Fixture();
        fixture.Files.RetrieveByIdAsync(12, Arg.Any<CancellationToken>()).Returns(new MemoryStream(new byte[20 * 1024 * 1024 + 1]));

        (await fixture.Handler.HandleAsync(Command)).FirstError.Code.ShouldBe("Artwork.ImageTooLarge");

        fixture.Processor.ReceivedCalls().ShouldBeEmpty();
        fixture.Work.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_InvalidImageWithStaleRevision_PreservesImageErrorBeforeTakingLocks()
    {
        var fixture = new Fixture();
        fixture.Processor.ProcessAsync(Arg.Any<ReadOnlyMemory<byte>>(), ArtworkRole.Hero, Arg.Any<CancellationToken>())
            .Returns(ArtworkImageErrors.Invalid());

        (await fixture.Handler.HandleAsync(Command)).FirstError.Code.ShouldBe("Artwork.InvalidImage");

        fixture.Work.ReceivedCalls().ShouldBeEmpty();
        await fixture.Files.DidNotReceiveWithAnyArgs().StageAsync(default(Stream)!, default);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task HandleAsync_LockRejectionOrFailureAfterPreparation_DoesNotPublishAndPreservesPrimary(bool cleanupFails, bool lockFails)
    {
        var fixture = new Fixture();
        bool inTransaction = false;
        var staged = new List<PreparedArtworkTests.StagedFile>();
        fixture.Processor.ProcessAsync(Arg.Any<ReadOnlyMemory<byte>>(), ArtworkRole.Hero, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                inTransaction.ShouldBeFalse();
                return new ProcessedArtworkImage("image/png", 800, 400, [new("hero", "image/webp", 400, 200, [4])]);
            });
        fixture.Files.StageAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            inTransaction.ShouldBeFalse();
            var file = new PreparedArtworkTests.StagedFile(call.Arg<Stream>(), cleanupFails ? new IOException("cleanup") : null);
            staged.Add(file);
            return Task.FromResult<IStagedFile>(file);
        });
        var transaction = Substitute.For<ITransaction>();
        fixture.Work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            staged.Count.ShouldBe(2);
            inTransaction = true;
            return Task.FromResult(transaction);
        });
        fixture.Repository.LockSelectionAsync(1, ArtworkRole.Hero, Command.IfMatch, Arg.Any<CancellationToken>())
            .Returns(ArtworkCurationErrors.PreconditionFailed());
        var primary = new IOException("title lock failed");
        if (lockFails)
            fixture.Repository.LockSelectionAsync(1, ArtworkRole.Hero, Command.IfMatch, Arg.Any<CancellationToken>())
                .Returns(Task.FromException<ErrorOr<bool>>(primary));

        if (lockFails)
            (await Should.ThrowAsync<IOException>(() => fixture.Handler.HandleAsync(Command))).ShouldBeSameAs(primary);
        else
            (await fixture.Handler.HandleAsync(Command)).FirstError.Code.ShouldBe("Artwork.PreconditionFailed");

        await fixture.Files.DidNotReceiveWithAnyArgs().PublishStagedAsync(default(IReadOnlyList<IStagedFile>)!, default);
        await transaction.DidNotReceiveWithAnyArgs().CommitAsync(default);
        staged.ShouldAllBe(file => file.DisposeCount == 1 && !file.Source.CanRead);
    }

    [Fact]
    public async Task HandleAsync_CommitStillPending_KeepsPreparationAliveAndDoesNotReturnBeforeDurability()
    {
        var fixture = new Fixture();
        var staged = new List<PreparedArtworkTests.StagedFile>();
        fixture.Files.StageAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var file = new PreparedArtworkTests.StagedFile(call.Arg<Stream>(), new IOException("cleanup after settlement"));
            staged.Add(file);
            return Task.FromResult<IStagedFile>(file);
        });
        var image = new ProcessedArtworkImage("image/png", 800, 400, [new("hero", "image/webp", 400, 200, [4])]);
        fixture.Processor.ProcessAsync(Arg.Any<ReadOnlyMemory<byte>>(), ArtworkRole.Hero, Arg.Any<CancellationToken>())
            .Returns(image);
        var publication = Substitute.For<IStagedFilePublicationBatch>();
        publication.RequiresFlush.Returns(false);
        publication.ReadAfterFlush().Returns(_ => staged.Select(file => new FileStoreResult(
            FileEntity.CreateNew(file.Sha256, 3, 3, false, DateTimeOffset.UnixEpoch), false, true)).ToArray());
        fixture.Files.PublishStagedAsync(Arg.Any<IReadOnlyList<IStagedFile>>(), Arg.Any<CancellationToken>()).Returns(publication);
        fixture.Repository.LockSelectionAsync(1, ArtworkRole.Hero, Command.IfMatch, Arg.Any<CancellationToken>()).Returns(true);
        // The gallery is read only after the owner has flushed the asset's generated identity.
        var variant = new ArtworkVariant(12, new PreparedArtworkTests.StagedFile(Stream.Null).Sha256.ToString(), "image/png", 800, 400, "original");
        var asset = new ArtworkAsset(3, 1, ArtworkRole.Hero, "user", null, null, variant,
            [new ArtworkVariant(13, variant.ContentVersion, "image/webp", 400, 200, "hero")], true, DateTimeOffset.UnixEpoch);
        fixture.Repository.GetGalleryAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => ErrorOrFactory.From<IReadOnlyList<ArtworkAsset>>([asset]));
        fixture.Repository.StagePinAsync(1, ArtworkRole.Hero, 3, Command.IfMatch, Arg.Any<CancellationToken>(), 50, 50).Returns(1L);
        var state = new ArtworkCurationState(ArtworkRole.Hero, ArtworkSelectionMode.Pinned, 1, 3, null);
        fixture.Repository.GetStatesAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => ErrorOrFactory.From<IReadOnlyList<ArtworkCurationState>>([state]));
        var committing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transaction = Substitute.For<ITransaction>();
        transaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            committing.SetResult();
            await release.Task.WaitAsync(call.Arg<CancellationToken>());
        });
        fixture.Work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = fixture.Handler.HandleAsync(Command, cancellation.Token);
        try
        {
            await committing.Task.WaitAsync(cancellation.Token);
            pending.IsCompleted.ShouldBeFalse();
            staged.ShouldAllBe(file => file.DisposeCount == 0 && file.Source.CanRead);
            release.SetResult();
            var result = await pending;
            result.Value.ShouldBe(state);
            staged.ShouldAllBe(file => file.DisposeCount == 1 && !file.Source.CanRead);
        }
        finally
        {
            release.TrySetResult();
            await cancellation.CancelAsync();
            try
            { await pending; }
            catch (OperationCanceledException error) when (error.CancellationToken == cancellation.Token) { }
        }
    }

    private sealed class Fixture
    {
        public IArtworkCurationRepository Repository { get; } = Substitute.For<IArtworkCurationRepository>();
        public IArtworkImageProcessor Processor { get; } = Substitute.For<IArtworkImageProcessor>();
        public IFileStorageService Files { get; } = Substitute.For<IFileStorageService>();
        public IUnitOfWork Work { get; } = Substitute.For<IUnitOfWork>();
        public PinTitleMediaArtworkCommandHandler Handler => new(Repository, Processor, Files, Work,
            NullLogger<PinTitleMediaArtworkCommandHandler>.Instance);
        public Fixture()
        {
            Repository.FindMediaAsync(1, 7, Arg.Any<CancellationToken>()).Returns(TitleMedia.CreateNew(1,
                MediaType.Cover, 12, "user", "image/png", DateTimeOffset.UnixEpoch));
            Files.RetrieveByIdAsync(12, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<Stream?>(new MemoryStream([1, 2, 3])));
        }
    }
}
