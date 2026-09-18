using Microsoft.Extensions.Logging;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.SetFieldOverrides;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed class SetFieldOverridesCommandHandlerTests
{
    private const int TitleId = 17;
    private const int PlatformId = 23;

    private readonly ITitleRepository _titles = Substitute.For<ITitleRepository>();
    private readonly IMetadataRematerializer _rematerializer = Substitute.For<IMetadataRematerializer>();
    private readonly ILibraryRepository _libraries = Substitute.For<ILibraryRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly RecordingLogger<SetFieldOverridesCommandHandler> _logger = new();

    [Fact]
    public async Task HandleAsync_SetOverride_FlushesForDetailAndCommitsOnceInOrder()
    {
        var calls = new List<string>();
        var transaction = new RecordingTransaction(calls);
        var title = CreateTitle();
        title.StoreProviderLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload { Rating = 94 });
        title.StoreUserLayer(new TitleMetadataPayload { Rating = 53 });
        ConfigureSuccess(title, transaction, calls);

        var result = await CreateHandler().HandleAsync(
            new SetFieldOverridesCommand(TitleId, new Dictionary<string, string?> { ["Rating"] = "igdb" }));

        result.IsError.ShouldBeFalse();
        result.Value.Rating.ShouldBe(94);
        title.FieldSourceOverrides["Rating"].ShouldBe("igdb");
        calls.ShouldBe(
        [
            "begin", "load", "rematerialize", "stage", "flush", "detail", "commit", "dispose"
        ]);
        transaction.CommitCount.ShouldBe(1);
        await _unitOfWork.Received(1).FlushAsync(Arg.Any<CancellationToken>());
        await _titles.Received(1)
            .UpdateMaterializedMetadataStagedAsync(title, Arg.Any<CancellationToken>());
        await _titles.DidNotReceive().UpdateAsync(Arg.Any<Title>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ClearOverride_RevertsToPrioritySource()
    {
        var transaction = new RecordingTransaction();
        var title = CreateTitle();
        title.StoreProviderLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload { Rating = 94 });
        title.StoreUserLayer(new TitleMetadataPayload { Rating = 53 });
        title.SetFieldSourceOverride("Rating", "igdb");
        title.Rematerialize(["igdb"]);
        ConfigureSuccess(title, transaction);

        var result = await CreateHandler().HandleAsync(
            new SetFieldOverridesCommand(TitleId, new Dictionary<string, string?> { ["Rating"] = null }));

        result.IsError.ShouldBeFalse();
        result.Value.Rating.ShouldBe(53);
        title.FieldSourceOverrides.ContainsKey("Rating").ShouldBeFalse();
    }

    [Fact]
    public async Task HandleAsync_EligibilityChanges_FlagsAffectedLibrariesBeforeCommit()
    {
        var calls = new List<string>();
        var transaction = new RecordingTransaction(calls);
        var title = CreateTitle();
        title.StoreProviderLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload { Genre = "Action" });
        title.StoreUserLayer(new TitleMetadataPayload { Genre = "Role-playing" });
        title.Rematerialize(["igdb"]);
        ConfigureSuccess(title, transaction, calls);
        _libraries
            .When(repository => repository.FlagForRematerializationByPlatformAsync(
                PlatformId, Arg.Any<CancellationToken>()))
            .Do(_ => calls.Add("flag"));

        var result = await CreateHandler().HandleAsync(
            new SetFieldOverridesCommand(TitleId, new Dictionary<string, string?> { ["Genre"] = "igdb" }));

        result.IsError.ShouldBeFalse();
        await _libraries.Received(1)
            .FlagForRematerializationByPlatformAsync(PlatformId, Arg.Any<CancellationToken>());
        calls.IndexOf("flag").ShouldBeLessThan(calls.IndexOf("commit"));
    }

    [Fact]
    public async Task HandleAsync_NonEligibilityFieldChanges_DoesNotFlagLibraries()
    {
        var transaction = new RecordingTransaction();
        var title = CreateTitle();
        title.StoreProviderLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload { Publisher = "Nintendo" });
        ConfigureSuccess(title, transaction);

        var result = await CreateHandler().HandleAsync(
            new SetFieldOverridesCommand(TitleId, new Dictionary<string, string?> { ["Publisher"] = "igdb" }));

        result.IsError.ShouldBeFalse();
        await _libraries.DidNotReceive()
            .FlagForRematerializationByPlatformAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_MissingTitle_ReturnsOpaqueNotFoundAndRollsBack()
    {
        var transaction = new RecordingTransaction();
        _unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        _titles.GetWithMetadataLayersAsync(TitleId, Arg.Any<CancellationToken>()).Returns((Title?)null);

        var result = await CreateHandler().HandleAsync(
            new SetFieldOverridesCommand(TitleId, new Dictionary<string, string?>()));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Catalog.TitleNotFound");
        result.FirstError.Description.ShouldBe("Title not found");
        result.FirstError.Description.ShouldNotContain(TitleId.ToString());
        transaction.RollbackCount.ShouldBe(1);
        transaction.CommitCount.ShouldBe(0);
        await _unitOfWork.DidNotReceive().FlushAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_DetailMissingAfterFlush_ReturnsOpaqueNotFoundAndRollsBackWithoutCommit()
    {
        var transaction = new RecordingTransaction();
        var title = CreateTitle();
        _unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        _titles.GetWithMetadataLayersAsync(TitleId, Arg.Any<CancellationToken>()).Returns(title);
        _titles.GetTitleDetailAsync(TitleId, Arg.Any<CancellationToken>()).Returns((TitleDetailData?)null);

        var result = await CreateHandler().HandleAsync(
            new SetFieldOverridesCommand(TitleId, new Dictionary<string, string?>()));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Catalog.TitleNotFound");
        result.FirstError.Description.ShouldNotContain(TitleId.ToString());
        await _unitOfWork.Received(1).FlushAsync(Arg.Any<CancellationToken>());
        transaction.RollbackCount.ShouldBe(1);
        transaction.CommitCount.ShouldBe(0);
    }

    [Fact]
    public async Task HandleAsync_PreCommitCancellation_PropagatesAndRollsBack()
    {
        using var cancellation = new CancellationTokenSource();
        var transaction = new RecordingTransaction();
        _unitOfWork.BeginTransactionAsync(cancellation.Token).Returns(transaction);
        _titles.GetWithMetadataLayersAsync(TitleId, cancellation.Token)
            .Returns<Task<Title?>>(_ => throw new OperationCanceledException(cancellation.Token));

        var exception = await Should.ThrowAsync<OperationCanceledException>(() =>
            CreateHandler().HandleAsync(
                new SetFieldOverridesCommand(TitleId, new Dictionary<string, string?>()),
                cancellation.Token));

        exception.CancellationToken.ShouldBe(cancellation.Token);
        transaction.RollbackCount.ShouldBe(1);
        transaction.CommitCount.ShouldBe(0);
    }

    [Fact]
    public async Task HandleAsync_TransactionDisposalFailsAfterCommit_ReturnsDurableSuccess()
    {
        var cleanupFailure = new InvalidOperationException("private provider cleanup detail");
        var transaction = new RecordingTransaction(disposeFailure: cleanupFailure);
        var title = CreateTitle();
        ConfigureSuccess(title, transaction);

        var result = await CreateHandler().HandleAsync(
            new SetFieldOverridesCommand(TitleId, new Dictionary<string, string?>()));

        result.IsError.ShouldBeFalse();
        transaction.CommitCount.ShouldBe(1);
        var warning = _logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Exception.ShouldBeSameAs(cleanupFailure);
        warning.Message.ShouldNotContain(TitleId.ToString());
    }

    [Fact]
    public async Task HandleAsync_CommitFails_ReturnsOpaqueFailureAndRollsBack()
    {
        const string privateFailure = "secret SQLite failure detail";
        var transaction = new RecordingTransaction(commitFailure: new InvalidOperationException(privateFailure));
        var title = CreateTitle();
        ConfigureSuccess(title, transaction);

        var result = await CreateHandler().HandleAsync(
            new SetFieldOverridesCommand(TitleId, new Dictionary<string, string?>()));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Catalog.TitleUpdateFailed");
        result.FirstError.Description.ShouldBe("Title update failed");
        result.FirstError.Description.ShouldNotContain(privateFailure);
        result.FirstError.Description.ShouldNotContain(TitleId.ToString());
        transaction.CommitCount.ShouldBe(1);
        transaction.RollbackCount.ShouldBe(1);
    }

    [Fact]
    public async Task HandleAsync_ConcurrentWrite_ReturnsConflictAndRollsBack()
    {
        var transaction = new RecordingTransaction(commitFailure:
            new PersistenceConflictException(new InvalidOperationException("private provider detail")));
        ConfigureSuccess(CreateTitle(), transaction);

        var result = await CreateHandler().HandleAsync(
            new SetFieldOverridesCommand(TitleId, new Dictionary<string, string?>()));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Catalog.TitleConflict");
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Conflict);
        result.FirstError.Description.ShouldNotContain("private provider detail");
        transaction.RollbackCount.ShouldBe(1);
    }

    private void ConfigureSuccess(
        Title title,
        RecordingTransaction transaction,
        List<string>? calls = null)
    {
        _unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls?.Add("begin");
                return transaction;
            });
        _titles.GetWithMetadataLayersAsync(TitleId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls?.Add("load");
                return title;
            });
        _rematerializer.RematerializeAsync(title, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls?.Add("rematerialize");
                title.Rematerialize(["igdb"]);
                return Task.CompletedTask;
            });
        _titles
            .When(repository => repository.UpdateMaterializedMetadataStagedAsync(
                title, Arg.Any<CancellationToken>()))
            .Do(_ => calls?.Add("stage"));
        _unitOfWork
            .When(unitOfWork => unitOfWork.FlushAsync(Arg.Any<CancellationToken>()))
            .Do(_ => calls?.Add("flush"));
        _titles.GetTitleDetailAsync(TitleId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls?.Add("detail");
                return DetailFrom(title);
            });
    }

    private SetFieldOverridesCommandHandler CreateHandler() =>
        new(TestSystemCatalog.Create(), _titles, _rematerializer, _libraries, _unitOfWork, _logger);

    private static Title CreateTitle() =>
        Title.Rehydrate(
            id: TitleId,
            platformId: PlatformId,
            name: "Super Mario World",
            normalizedName: "super mario world",
            description: null,
            publisher: null,
            developer: null,
            genre: null,
            releaseDate: null,
            players: null,
            rating: null,
            enrichmentStatus: EnrichmentStatus.Completed,
            lastEnrichedAt: DateTimeOffset.UtcNow,
            createdAt: DateTimeOffset.UtcNow,
            metadataLayers: []);

    private static TitleDetailData DetailFrom(Title title) =>
        new()
        {
            Id = title.Id,
            PlatformId = title.PlatformId,
            Name = title.Name,
            EnrichmentStatus = title.EnrichmentStatus.ToString(),
            IsTracked = false,
            Description = title.Description,
            Publisher = title.Publisher,
            Developer = title.Developer,
            Genre = title.Genre,
            ReleaseDate = title.ReleaseDate,
            Players = title.Players,
            Rating = title.Rating,
            CreatedAt = title.CreatedAt,
            LastEnrichedAt = title.LastEnrichedAt,
            FieldProvenance = title.FieldProvenance
        };

    private sealed class RecordingTransaction(
        List<string>? calls = null,
        Exception? commitFailure = null,
        Exception? disposeFailure = null) : ITransaction
    {
        private bool _committed;

        public int CommitCount { get; private set; }
        public int RollbackCount { get; private set; }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            CommitCount++;
            calls?.Add("commit");
            if (commitFailure is not null)
            {
                throw commitFailure;
            }

            _committed = true;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RollbackCount++;
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            calls?.Add("dispose");
            if (!_committed)
            {
                await RollbackAsync();
            }

            if (disposeFailure is not null)
            {
                throw disposeFailure;
            }
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, exception, formatter(state, exception)));
    }

    private sealed record LogEntry(LogLevel Level, Exception? Exception, string Message);
}
