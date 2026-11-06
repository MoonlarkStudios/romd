using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.UploadTitleMedia;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Application.Common.Concurrency;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Storage;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed class TitleMediaUploadSettlementTests
{
    [Theory]
    [InlineData("stage", false)]
    [InlineData("stage", true)]
    [InlineData("append", false)]
    [InlineData("append", true)]
    [InlineData("response", false)]
    [InlineData("response", true)]
    [InlineData("commit", false)]
    [InlineData("commit", true)]
    [InlineData("acknowledged", false)]
    [InlineData("acknowledged", true)]
    public async Task HandleAsync_OwnedCleanupFails_PreservesPrimaryOrAcknowledgedResponse(string phase, bool canceled)
    {
        var titleLock = Substitute.For<ITitleRowLock>();
        var store = Substitute.For<ITitleMediaStore>();
        var titles = Substitute.For<ITitleReader>();
        var media = Substitute.For<ITitleMediaReader>();
        var storage = Substitute.For<IFileStorageService>();
        var temps = Substitute.For<ITempFileFactory>();
        var temp = Substitute.For<ITempFile>();
        var staged = Substitute.For<IStagedFile>();
        var work = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        var revision = Guid.NewGuid();
        var nextRevision = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        Exception primary = canceled ? new OperationCanceledException(cancellation.Token) : new IOException("primary upload failure");
        titles.GetSummaryAsync(1, Arg.Any<CancellationToken>()).Returns(new TitleSummary(
            1, 2, "Title", EnrichmentStatus.None, null, null, null, null, null, null, null, DateTimeOffset.UnixEpoch, null, false));
        temps.CreateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(temp);
        temp.OpenRead().Returns(_ => new MemoryStream([137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4]));
        storage.StageAsync(temp, Arg.Any<CancellationToken>()).Returns(staged);
        titleLock.LockRevisionAsync(1, Arg.Any<CancellationToken>()).Returns(revision);
        work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        var publication = Substitute.For<IStagedFilePublication>();
        publication.RequiresFlush.Returns(true);
        publication.ReadAfterFlush().Returns(new FileStoreResult(
            FileEntity.Rehydrate(5, Sha256.FromSpan(new byte[32]), 12, 12, false, DateTimeOffset.UnixEpoch), true, false));
        storage.PublishStagedAsync(staged, Arg.Any<CancellationToken>()).Returns(publication);
        store.AddUserMediaStagedAsync(Arg.Any<TitleMedia>(), Arg.Any<CancellationToken>()).Returns(new StagedMediaAppend(nextRevision, true));
        media.ListAsync(1, Arg.Any<CancellationToken>()).Returns([new TitleMediaSummary(7, MediaType.Cover, 5, "user", null, null, null, false)]);
        transaction.DisposeAsync().Returns(_ => ValueTask.FromException(new IOException("transaction cleanup")));
        staged.DisposeAsync().Returns(_ => ValueTask.FromException(new IOException("staging cleanup")));
        temp.DisposeAsync().Returns(_ => ValueTask.FromException(new IOException("temporary upload cleanup")));
        if (phase == "stage")
            storage.StageAsync(temp, Arg.Any<CancellationToken>()).Returns<IStagedFile>(_ => throw primary);
        if (phase == "append")
            store.AddUserMediaStagedAsync(Arg.Any<TitleMedia>(), Arg.Any<CancellationToken>()).Returns<StagedMediaAppend>(_ => throw primary);
        if (phase == "response")
            media.ListAsync(1, Arg.Any<CancellationToken>()).Returns<IReadOnlyList<TitleMediaSummary>>(_ => throw primary);
        if (phase == "commit")
            transaction.When(value => value.CommitAsync(Arg.Any<CancellationToken>())).Do(_ => throw primary);
        if (phase == "acknowledged" && canceled)
            transaction.When(value => value.CommitAsync(Arg.Any<CancellationToken>())).Do(_ => cancellation.Cancel());
        var handler = new UploadTitleMediaCommandHandler(titleLock, store, titles, media, storage, temps,
            Substitute.For<IAdminEventOutbox>(), work, NullLogger<UploadTitleMediaCommandHandler>.Instance, TimeProvider.System);
        var command = new UploadTitleMediaCommand(1, MediaType.Cover, new MemoryStream([1]), "cover.png", RevisionPrecondition.For(revision.ToString()));

        if (phase == "acknowledged")
        {
            var result = await handler.HandleAsync(command, cancellation.Token);
            result.Value.TitleRevision.ShouldBe(nextRevision.ToString());
            result.Value.Created.ShouldBeTrue();
            result.Value.Media.SourceId.ShouldBe("user");
        }
        else
        {
            Exception? observed = null;
            try
            { await handler.HandleAsync(command, cancellation.Token); }
            catch (Exception error) { observed = error; }
            observed.ShouldBeSameAs(primary);
        }

        await temp.Received(1).DisposeAsync();
        await staged.Received(phase == "stage" ? 0 : 1).DisposeAsync();
        await transaction.Received(phase == "stage" ? 0 : 1).DisposeAsync();
        await transaction.Received(phase is "commit" or "acknowledged" ? 1 : 0).CommitAsync(Arg.Any<CancellationToken>());
        await storage.Received(1).StageAsync(temp, Arg.Any<CancellationToken>());
    }
}
