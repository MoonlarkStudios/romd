using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Titles.Commands.MergeTitles;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed partial class MergeTitlesCommandHandlerTests
{
    [Theory]
    [InlineData(101)]
    [InlineData(202)]
    public async Task HandleAsync_InitialReadChurn_ReturnsReadConflictWithoutMutation(int changingTitle)
    {
        var unit = Substitute.For<IUnitOfWork>();
        var (handler, merge, titles) = CreateHandler(NewTitle(101, 7, "Source"), NewTitle(202, 7, "Target"), unit);
        unit.ClearReceivedCalls();
        titles.GetAggregateAsync(changingTitle, Arg.Any<CancellationToken>()).Returns(Task.FromException<Title?>(ReadConflict()));

        var result = await handler.HandleAsync(new(101, 202, Current, Guid.Empty.ToString()));

        result.FirstError.Type.ShouldBe(ErrorType.Conflict);
        result.FirstError.Code.ShouldBe("Titles.ReadConflict");
        unit.ReceivedCalls().ShouldBeEmpty();
        merge.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_ConflictClassificationReadChurn_IsBoundedAndHonorsCancellation(bool cancelled)
    {
        using var request = new CancellationTokenSource();
        var target = NewTitle(202, 7, "Target");
        var (handler, merge, titles) = CreateHandler(NewTitle(101, 7, "Source"), target);
        merge.ApplyMergeStagedAsync(target, Arg.Any<Title>(), request.Token).Returns(Task.FromException(ReadConflict()));
        int reads = 0;
        titles.GetAggregateAsync(202, request.Token).Returns(_ =>
        {
            if (++reads == 1)
                return Task.FromResult<Title?>(target);
            if (cancelled)
                request.Cancel();
            return Task.FromException<Title?>(ReadConflict());
        });

        if (cancelled)
        {
            var error = await Should.ThrowAsync<OperationCanceledException>(() =>
                handler.HandleAsync(new(101, 202, Current, Guid.Empty.ToString()), request.Token));
            error.CancellationToken.ShouldBe(request.Token);
        }
        else
            (await handler.HandleAsync(new(101, 202, Current, Guid.Empty.ToString()), request.Token))
                .FirstError.Code.ShouldBe("Titles.ReadConflict");

        reads.ShouldBe(2);
        await merge.DidNotReceive().DeleteAbsorbedSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private static PersistenceConflictException ReadConflict() => new(new InvalidOperationException("private churn detail"));
}
