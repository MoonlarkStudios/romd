using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.MetadataProviders;
using Romd.Contracts.Management.MetadataProviders;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.MetadataProviders;

public sealed partial class ProviderIdentityServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DiscoverAsync_AppliedOrRejectedDecision_CommitsItsOwnerBeforeReleasingTracking(bool owned, bool applied)
    {
        bool searched = false;
        var service = Create("Game", [], _ =>
        {
            _work.ReceivedCalls().ShouldBeEmpty();
            searched = true;
            return new[] { Game("42", "Game") };
        });
        var receipt = Substitute.For<IStagedProviderIdentityDecision>();
        receipt.Applied.Returns(applied);
        _store.StageDecisionAsync(Arg.Any<ProviderIdentitySnapshot>(), "provider", Guid.Empty,
            Arg.Any<ProviderIdentityDecision>(), Arg.Any<ProviderGame?>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            searched.ShouldBeTrue();
            return receipt;
        });
        var owner = owned ? new JobContext(null, _ => Task.CompletedTask, default,
            ownedMutationDelegate: async (mutation, token) =>
            {
                searched.ShouldBeTrue();
                await using var boundary = _transaction;
                await mutation(token);
                receipt.DidNotReceive().Dispose();
                await boundary.CommitAsync(token);
            }) : null;

        await service.DiscoverAsync(1, owner, default);

        Received.InOrder(() =>
        {
            _store.StageDecisionAsync(Arg.Any<ProviderIdentitySnapshot>(), "provider", Guid.Empty,
                Arg.Any<ProviderIdentityDecision>(), Arg.Any<ProviderGame?>(), CancellationToken.None);
            _transaction.CommitAsync(CancellationToken.None);
            _transaction.DisposeAsync();
            receipt.Dispose();
        });
        if (owned)
            await _work.DidNotReceiveWithAnyArgs().BeginTransactionAsync(default);
        else
            await _work.Received(1).BeginTransactionAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoverAsync_CommitFails_ReleasesReceiptAfterRollbackWithoutReplacingPrimaryFailure(bool owned)
    {
        var service = Create("Game", [], _ => new[] { Game("42", "Game") });
        var receipt = Substitute.For<IStagedProviderIdentityDecision>();
        var failure = new IOException("Injected identity commit failure.");
        _store.StageDecisionAsync(Arg.Any<ProviderIdentitySnapshot>(), "provider", Guid.Empty,
            Arg.Any<ProviderIdentityDecision>(), Arg.Any<ProviderGame?>(), Arg.Any<CancellationToken>()).Returns(receipt);
        _transaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(failure));
        receipt.When(value => value.Dispose()).Do(_ => throw new InvalidOperationException("Injected cleanup failure."));
        var owner = owned ? new JobContext(null, _ => Task.CompletedTask, default,
            ownedMutationDelegate: async (mutation, token) =>
            {
                await using var boundary = _transaction;
                await mutation(token);
                await boundary.CommitAsync(token);
            }) : null;

        (await Should.ThrowAsync<IOException>(() => service.DiscoverAsync(1, owner, default))).ShouldBeSameAs(failure);

        Received.InOrder(() =>
        {
            _transaction.CommitAsync(CancellationToken.None);
            _transaction.DisposeAsync();
            receipt.Dispose();
        });
    }
}
