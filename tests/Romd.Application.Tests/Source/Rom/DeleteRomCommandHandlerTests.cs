using Microsoft.Extensions.Logging;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Commands.DeleteRom;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Rom;

public sealed class DeleteRomCommandHandlerTests
{
    private const int RomId = 73;
    private const int FileId = 91;

    private static readonly Sha1 RomSha1 = Sha1.Parse("0123456789abcdef0123456789abcdef01234567");
    private static readonly Md5 RomMd5 = Md5.Parse("0123456789abcdef0123456789abcdef");
    private static readonly Crc32 RomCrc32 = Crc32.Parse("01234567");

    private readonly IFileStorageService _fileStorage = Substitute.For<IFileStorageService>();
    private readonly ILibraryRepository _libraryRepository = Substitute.For<ILibraryRepository>();
    private readonly RecordingLogger<DeleteRomCommandHandler> _logger = new();
    private readonly IAdminEventOutbox _outbox = Substitute.For<IAdminEventOutbox>();
    private readonly IRomOwnershipImpactReader _ownershipImpact = Substitute.For<IRomOwnershipImpactReader>();
    private readonly IRomPayloadAssertionImpactReader _payloadAssertionImpact =
        Substitute.For<IRomPayloadAssertionImpactReader>();
    private readonly IRomRepository _romRepository = Substitute.For<IRomRepository>();
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    public DeleteRomCommandHandlerTests()
    {
        _romRepository.GetByIdAsync(RomId, Arg.Any<CancellationToken>()).Returns(CreateRom());
        _ownershipImpact.ReadTitlePlatformIdsAsync(RomId, Arg.Any<CancellationToken>())
            .Returns([10, 20]);
        _unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_transaction);
        _fileStorage.DeleteIfUnreferencedAsync(FileId, Arg.Any<CancellationToken>()).Returns(true);
    }

    [Fact]
    public async Task HandleAsync_ExistingRom_DeletesFlagsLibrariesAndRecordsThreeEvents()
    {
        var result = await CreateHandler().HandleAsync(new DeleteRomCommand(RomId));

        result.IsError.ShouldBeFalse();
        await _libraryRepository.Received(1)
            .FlagForRematerializationByPlatformAsync(10, Arg.Any<CancellationToken>());
        await _libraryRepository.Received(1)
            .FlagForRematerializationByPlatformAsync(20, Arg.Any<CancellationToken>());
        await _romRepository.Received(1).DeleteAsync(RomId, Arg.Any<CancellationToken>());
        await _outbox.Received(1)
            .EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, Arg.Any<CancellationToken>());
        await _outbox.Received(1)
            .EnqueueAsync(AdminRealtimeEventTypes.CoverageStatsChanged, Arg.Any<CancellationToken>());
        await _outbox.Received(1)
            .EnqueueAsync(AdminRealtimeEventTypes.HealthStatsChanged, Arg.Any<CancellationToken>());
        await _transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().FlushAsync(Arg.Any<CancellationToken>());
        await _fileStorage.Received(1).DeleteIfUnreferencedAsync(FileId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_MissingRom_ReturnsOpaqueNotFoundWithoutStartingTransaction()
    {
        const int missingRomId = 987654;
        _romRepository.GetByIdAsync(missingRomId, Arg.Any<CancellationToken>()).Returns((RomFile?)null);

        var result = await CreateHandler().HandleAsync(new DeleteRomCommand(missingRomId));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Library.RomNotFound");
        result.FirstError.Description.ShouldBe("ROM file not found");
        result.FirstError.Description.ShouldNotContain(missingRomId.ToString());
        await _unitOfWork.DidNotReceive().BeginTransactionAsync(Arg.Any<CancellationToken>());
        await _romRepository.DidNotReceive().DeleteAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _fileStorage.DidNotReceive()
            .DeleteIfUnreferencedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_CancelledBeforeMutation_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _romRepository.GetByIdAsync(RomId, cancellation.Token)
            .Returns<Task<RomFile?>>(_ => throw new OperationCanceledException(cancellation.Token));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            CreateHandler().HandleAsync(new DeleteRomCommand(RomId), cancellation.Token));

        await _unitOfWork.DidNotReceive().BeginTransactionAsync(Arg.Any<CancellationToken>());
        await _romRepository.DidNotReceive().DeleteAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _outbox.DidNotReceive()
            .EnqueueAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _fileStorage.DidNotReceive()
            .DeleteIfUnreferencedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_Success_SequencesCleanupAfterCommit()
    {
        var calls = new List<string>();
        _romRepository.GetByIdAsync(RomId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("load-rom");
                return CreateRom();
            });
        _ownershipImpact.ReadTitlePlatformIdsAsync(RomId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("load-platforms");
                return new[] { 10, 20 };
            });
        _unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("begin");
                return _transaction;
            });
        _libraryRepository
            .When(repository => repository.FlagForRematerializationByPlatformAsync(
                Arg.Any<int>(), Arg.Any<CancellationToken>()))
            .Do(call => calls.Add($"flag:{call.ArgAt<int>(0)}"));
        _romRepository
            .When(repository => repository.DeleteAsync(RomId, Arg.Any<CancellationToken>()))
            .Do(_ => calls.Add("delete-rom"));
        _outbox
            .When(outbox => outbox.EnqueueAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => calls.Add($"event:{call.ArgAt<string>(0)}"));
        _transaction
            .When(transaction => transaction.CommitAsync(Arg.Any<CancellationToken>()))
            .Do(_ => calls.Add("commit"));
        _fileStorage.DeleteIfUnreferencedAsync(FileId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("cleanup");
                return true;
            });

        var result = await CreateHandler().HandleAsync(new DeleteRomCommand(RomId));

        result.IsError.ShouldBeFalse();
        calls.ShouldBe(
        [
            "load-rom",
            "begin",
            "load-platforms",
            "flag:10",
            "flag:20",
            "delete-rom",
            $"event:{AdminRealtimeEventTypes.StorageStatsChanged}",
            $"event:{AdminRealtimeEventTypes.CoverageStatsChanged}",
            $"event:{AdminRealtimeEventTypes.HealthStatsChanged}",
            "commit",
            "cleanup"
        ]);
    }

    [Fact]
    public async Task HandleAsync_CleanupFailsAfterCommit_ReturnsSuccessAndLogsInternalContext()
    {
        var cleanupFailure = new IOException("CAS is temporarily unavailable");
        _fileStorage.DeleteIfUnreferencedAsync(FileId, Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw cleanupFailure);

        var result = await CreateHandler().HandleAsync(new DeleteRomCommand(RomId));

        result.IsError.ShouldBeFalse();
        await _transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        var warning = _logger.Entries
            .Where(entry => entry.Level == LogLevel.Warning)
            .ShouldHaveSingleItem();
        warning.Exception.ShouldBeSameAs(cleanupFailure);
        warning.Message.ShouldContain($"ROM ID: {RomId}");
        warning.Message.ShouldContain($"File ID: {FileId}");
        warning.Message.ShouldContain("recurring cleanup will retry");
    }

    [Fact]
    public async Task HandleAsync_CleanupCancelledAfterCommit_ReturnsSuccessAndLogsWarning()
    {
        using var cancellation = new CancellationTokenSource();
        var cleanupCancellation = new OperationCanceledException(cancellation.Token);
        _transaction
            .When(transaction => transaction.CommitAsync(cancellation.Token))
            .Do(_ => cancellation.Cancel());
        _fileStorage.DeleteIfUnreferencedAsync(FileId, cancellation.Token)
            .Returns<Task<bool>>(_ => throw cleanupCancellation);

        var result = await CreateHandler().HandleAsync(new DeleteRomCommand(RomId), cancellation.Token);

        result.IsError.ShouldBeFalse();
        cancellation.IsCancellationRequested.ShouldBeTrue();
        await _transaction.Received(1).CommitAsync(cancellation.Token);
        var warning = _logger.Entries
            .Where(entry => entry.Level == LogLevel.Warning)
            .ShouldHaveSingleItem();
        warning.Exception.ShouldBeSameAs(cleanupCancellation);
        cleanupCancellation.CancellationToken.ShouldBe(cancellation.Token);
        warning.Message.ShouldContain($"ROM ID: {RomId}");
        warning.Message.ShouldContain($"File ID: {FileId}");
    }

    [Fact]
    public async Task HandleAsync_TransactionDisposeFailsAfterCommit_ReturnsSuccessThenAttemptsCleanup()
    {
        var disposeFailure = new InvalidOperationException("provider failed while disposing committed transaction");
        var transaction = new DisposeThrowingTransaction(disposeFailure);
        _unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        bool cleanupObservedCommitted = false;
        _fileStorage.DeleteIfUnreferencedAsync(FileId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cleanupObservedCommitted = transaction.CommitSucceeded;
                return true;
            });

        var result = await CreateHandler().HandleAsync(new DeleteRomCommand(RomId));

        result.IsError.ShouldBeFalse();
        transaction.CommitSucceeded.ShouldBeTrue();
        transaction.DisposeAttempted.ShouldBeTrue();
        cleanupObservedCommitted.ShouldBeTrue();
        await _fileStorage.Received(1)
            .DeleteIfUnreferencedAsync(FileId, Arg.Any<CancellationToken>());
        var warning = _logger.Entries
            .Where(entry => entry.Level == LogLevel.Warning)
            .ShouldHaveSingleItem();
        warning.Exception.ShouldBeSameAs(disposeFailure);
        warning.Message.ShouldContain($"ROM ID: {RomId}");
        warning.Message.ShouldContain("transaction cleanup failed");
    }

    [Fact]
    public async Task HandleAsync_DeleteFails_ReturnsOpaqueDatabaseErrorWithoutCleanup()
    {
        const string privateFailure = "sqlite-secret-detail";
        _romRepository.DeleteAsync(RomId, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException(privateFailure));

        var result = await CreateHandler().HandleAsync(new DeleteRomCommand(RomId));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Library.DatabaseFailed");
        result.FirstError.Description.ShouldBe("Database operation failed");
        result.FirstError.Description.ShouldNotContain(privateFailure);
        result.FirstError.Description.ShouldNotContain(RomId.ToString());
        await _transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await _fileStorage.DidNotReceive()
            .DeleteIfUnreferencedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private DeleteRomCommandHandler CreateHandler() =>
        new(
            _romRepository,
            _ownershipImpact,
            _payloadAssertionImpact,
            _fileStorage,
            _outbox,
            _unitOfWork,
            _libraryRepository,
            Substitute.For<ITitlePayloadAvailabilityProjection>(),
            _logger);

    private static RomFile CreateRom() =>
        RomFile.CreateNew("managed.rom", FileId, 4096, RomSha1, RomMd5, RomCrc32);

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

    private sealed class DisposeThrowingTransaction(Exception disposeFailure) : ITransaction
    {
        public bool CommitSucceeded { get; private set; }

        public bool DisposeAttempted { get; private set; }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            CommitSucceeded = true;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeAttempted = true;
            return new ValueTask(Task.FromException(disposeFailure));
        }
    }
}
