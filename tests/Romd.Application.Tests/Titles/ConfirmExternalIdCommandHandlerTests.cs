using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.ConfirmExternalId;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed class ConfirmExternalIdCommandHandlerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_RecordsIntentBeforeEdit_CommitsOnlyWhenIntentSucceeds(bool schedulingFails)
    {
        var titles = Substitute.For<ITitleRepository>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        var scheduler = Substitute.For<IRematerializationScheduler>();
        var now = DateTimeOffset.UtcNow;
        var title = Title.Rehydrate(1, 10, "Title", "title", null, null, null, null,
            null, null, null, EnrichmentStatus.None, null, now,
            externalIds: [TitleExternalId.Rehydrate(2, 1, "igdb", "123", 1, false, now)]);
        titles.GetWithCollectionsAsync(1, Arg.Any<CancellationToken>()).Returns(title);
        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        var handler = new ConfirmExternalIdCommandHandler(titles, unitOfWork, scheduler,
            NullLogger<ConfirmExternalIdCommandHandler>.Instance);

        if (schedulingFails)
        {
            scheduler.EnqueueTitleAsync(1, Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new InvalidOperationException("intent storage failed")));

            await Should.ThrowAsync<InvalidOperationException>(() =>
                handler.HandleAsync(new ConfirmExternalIdCommand(1, "igdb")));

            await titles.DidNotReceive().UpdateAsync(Arg.Any<Title>(), Arg.Any<CancellationToken>());
            await transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
            await transaction.Received(1).DisposeAsync();
        }
        else
        {
            var result = await handler.HandleAsync(new ConfirmExternalIdCommand(1, "igdb"));

            result.IsError.ShouldBeFalse();
            Received.InOrder(() =>
            {
                unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>());
                scheduler.EnqueueTitleAsync(1, Arg.Any<CancellationToken>());
                titles.UpdateAsync(title, Arg.Any<CancellationToken>());
                transaction.CommitAsync(Arg.Any<CancellationToken>());
            });
        }
    }
}
