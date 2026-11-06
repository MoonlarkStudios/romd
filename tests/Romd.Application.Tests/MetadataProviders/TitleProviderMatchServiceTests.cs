using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Text.Json;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.MetadataProviders;
using Romd.Application.Common.Concurrency;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.MetadataProviders;

public sealed class TitleProviderMatchServiceTests
{
    private readonly ITitleProviderMatchStore _store = Substitute.For<ITitleProviderMatchStore>();
    private readonly IProviderIdentityAdapter _provider = Substitute.For<IProviderIdentityAdapter>();
    private readonly IUnitOfWork _unit = Substitute.For<IUnitOfWork>();
    private readonly Transaction _transaction = new();
    private readonly Guid _revision = Guid.NewGuid();
    private readonly Guid _pending = Guid.NewGuid();

    public TitleProviderMatchServiceTests()
    {
        _provider.Id.Returns("provider");
        _provider.Name.Returns("Provider");
        _provider.Capabilities.Returns(new[] { "metadata" });
        _provider.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, true));
        _store.ExistsAsync(1, Arg.Any<CancellationToken>()).Returns(true);
        _store.ReadRevisionAsync(1, "provider", Arg.Any<CancellationToken>()).Returns(_revision);
        _store.StageChangeAsync(1, "provider", Arg.Any<RevisionPrecondition>(), Arg.Any<ProviderGameDto?>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Result.Success);
        _store.FindPendingEnrichmentAsync(1, Arg.Any<CancellationToken>()).Returns(_pending);
        _store.ReadFactsAsync(1, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(new TitleProviderMatchFacts(
            [TitleExternalId.CreateNew(1, "provider", "42", 1, createdAt: DateTimeOffset.UnixEpoch)], [], []));
        _unit.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _transaction.Started = true;
            return _transaction;
        });
    }

    [Fact]
    public async Task SetAsync_ProviderResolution_PrecedesTransactionAndResponseFactsPrecedeCommit()
    {
        _provider.ResolveAsync("42", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _transaction.Started.ShouldBeFalse();
            return new ProviderGameDto("42", "Game", "https://provider.test/42");
        });
        _store.ReadFactsAsync(1, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _transaction.Started.ShouldBeTrue();
            _transaction.Committed.ShouldBeFalse();
            return new TitleProviderMatchFacts([TitleExternalId.CreateNew(1, "provider", "42", 1, createdAt: DateTimeOffset.UnixEpoch)],
                [new("provider", _revision, false, false, null, null,
                    new("42", "Game", "https://provider.test/42"), null)], []);
        });

        var result = await Create().SetAsync(1, "provider", "42", Precondition(), default);

        result.IsError.ShouldBeFalse();
        result.Value.PendingJobId.ShouldBe(_pending);
        _transaction.Committed.ShouldBeTrue();
        _transaction.Disposed.ShouldBeTrue();
        await _provider.Received(1).ResolveAsync("42", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_StalePrecondition_PerformsNoProviderOrTransactionWork()
    {
        var result = await Create().SetAsync(1, "provider", "42",
            RevisionPrecondition.For(Guid.NewGuid().ToString()), default);

        result.FirstError.Code.ShouldBe("ProviderMatch.PreconditionFailed");
        _transaction.Started.ShouldBeFalse();
        await _provider.DidNotReceive().ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmAsync_OptionalPreviewFailsAfterCommit_ReturnsDurableSuccess(bool cancellation)
    {
        _provider.ResolveAsync("42", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _transaction.Committed.ShouldBeTrue();
            return Task.FromException<ErrorOr<ProviderGameDto>>(cancellation
                ? new OperationCanceledException() : new HttpRequestException("preview unavailable"));
        });

        var result = await Create().ConfirmAsync(1, "provider", Precondition(), default);

        result.IsError.ShouldBeFalse();
        result.Value.PendingJobId.ShouldBe(_pending);
        result.Value.LastError.ShouldBe("Preview unavailable");
        _transaction.Committed.ShouldBeTrue();
    }

    [Fact]
    public async Task ConfirmAsync_DisposalFailsAfterCommit_ReturnsDurableSuccess()
    {
        _transaction.FailDisposal = true;
        _provider.ResolveAsync("42", Arg.Any<CancellationToken>()).Returns(
            new ProviderGameDto("42", "Game", "https://provider.test/42"));

        var result = await Create().ConfirmAsync(1, "provider", Precondition(), default);

        result.IsError.ShouldBeFalse();
        _transaction.Committed.ShouldBeTrue();
    }

    [Fact]
    public async Task ConfirmAsync_InvalidResponseMetadata_FailsBeforeCommitAndPreview()
    {
        _store.ReadFactsAsync(1, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(new TitleProviderMatchFacts(
            [TitleExternalId.CreateNew(1, "provider", "42", 1, createdAt: DateTimeOffset.UnixEpoch)], [],
            [TitleMetadataLayer.Rehydrate(1, 1, "provider", MetadataSourceType.Provider, "invalid json", DateTimeOffset.UtcNow)]));

        await Should.ThrowAsync<JsonException>(() => Create().ConfirmAsync(1, "provider", Precondition(), default));

        _transaction.Committed.ShouldBeFalse();
        _transaction.Disposed.ShouldBeTrue();
        await _provider.DidNotReceive().ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmAsync_StageRejected_DoesNotQueueOrCommit()
    {
        _store.StageChangeAsync(1, "provider", Arg.Any<RevisionPrecondition>(), Arg.Any<ProviderGameDto?>(),
            false, Arg.Any<CancellationToken>()).Returns(ProviderMatchErrors.PreconditionFailed);

        var result = await Create().ConfirmAsync(1, "provider", Precondition(), default);

        result.FirstError.Code.ShouldBe("ProviderMatch.PreconditionFailed");
        _transaction.Committed.ShouldBeFalse();
        _transaction.Disposed.ShouldBeTrue();
        await _store.DidNotReceive().FindPendingEnrichmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _provider.DidNotReceive().ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_DisabledProvider_IsExcludedFromFactsAndTrustReads()
    {
        var disabled = Substitute.For<IProviderIdentityAdapter>();
        disabled.Id.Returns("disabled");
        disabled.Name.Returns("Disabled");
        disabled.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(false, true));
        var identity = Substitute.For<IProviderIdentityStore>();
        _provider.ResolveAsync("42", Arg.Any<CancellationToken>()).Returns(new ProviderGameDto("42", "Game", "https://provider.test/42"));
        var service = new TitleProviderMatchService(_store, [_provider, disabled], _unit, TimeProvider.System,
            NullLogger<TitleProviderMatchService>.Instance, identityStore: identity);

        var result = await service.GetAsync(1, default);

        result.IsError.ShouldBeFalse();
        result.Value.ShouldHaveSingleItem().ProviderId.ShouldBe("provider");
        await _store.Received(1).ReadFactsAsync(1,
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "provider" })), Arg.Any<CancellationToken>());
        await identity.Received(1).ReadTrustedAsync(1, "provider", Arg.Any<CancellationToken>());
        await identity.DidNotReceive().ReadTrustedAsync(1, "disabled", Arg.Any<CancellationToken>());
        await identity.DidNotReceive().ReadAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_NoEnabledProviders_PerformsNoFactOrTrustRead()
    {
        _provider.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(false, true));
        var identity = Substitute.For<IProviderIdentityStore>();
        var service = new TitleProviderMatchService(_store, [_provider], _unit, TimeProvider.System,
            NullLogger<TitleProviderMatchService>.Instance, identityStore: identity);

        var result = await service.GetAsync(1, default);

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBeEmpty();
        await _store.DidNotReceive().ReadFactsAsync(Arg.Any<int>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
        await identity.DidNotReceive().ReadTrustedAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await identity.DidNotReceive().ReadAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("42", "AutoMatched")]
    [InlineData("another game", "NeedsReview")]
    [InlineData(null, "NeedsReview")]
    public async Task GetAsync_CurrentTrustMustMatchThePresentedIdentity(string? trustedGameId, string expectedState)
    {
        var identity = Substitute.For<IProviderIdentityStore>();
        identity.ReadTrustedAsync(1, "provider", Arg.Any<CancellationToken>()).Returns(trustedGameId);
        _provider.ResolveAsync("42", Arg.Any<CancellationToken>()).Returns(new ProviderGameDto("42", "Game", "https://provider.test/42"));
        var service = new TitleProviderMatchService(_store, [_provider], _unit, TimeProvider.System,
            NullLogger<TitleProviderMatchService>.Instance, identityStore: identity);

        var result = await service.GetAsync(1, "provider", default);

        result.IsError.ShouldBeFalse();
        result.Value.ExternalId.ShouldBe("42");
        result.Value.State.ShouldBe(expectedState);
        await _store.Received(1).ReadFactsAsync(1,
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "provider" })), Arg.Any<CancellationToken>());
        await identity.DidNotReceive().ReadAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private RevisionPrecondition Precondition() => RevisionPrecondition.For(_revision.ToString());

    private TitleProviderMatchService Create() => new(_store, [_provider], _unit, TimeProvider.System,
        NullLogger<TitleProviderMatchService>.Instance);

    private sealed class Transaction : ITransaction
    {
        public bool Started { get; set; }
        public bool Committed { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailDisposal { get; set; }
        public Task CommitAsync(CancellationToken ct = default) { Committed = true; return Task.CompletedTask; }
        public Task RollbackAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return FailDisposal ? ValueTask.FromException(new IOException("cleanup failed")) : ValueTask.CompletedTask;
        }
    }
}
