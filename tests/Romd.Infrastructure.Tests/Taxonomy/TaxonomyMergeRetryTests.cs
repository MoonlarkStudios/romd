using Microsoft.EntityFrameworkCore;
using Npgsql;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Taxonomy;
using Romd.Admin.Application.Taxonomy.ReadModels;
using Romd.Domain.Taxonomy;
using Romd.Persistence;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Taxonomy;

/// <summary>
///     How a merge or an alias write treats each way its work can fail, with PostgreSQL's own errors and
///     classification.
/// </summary>
public sealed class TaxonomyMergeRetryTests
{
    private const int SourceId = 1;
    private const int TargetId = 2;

    private readonly ITaxonomyRepository<Region> _repository = Substitute.For<ITaxonomyRepository<Region>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();
    private readonly TaxonomyService<Region> _service;

    public TaxonomyMergeRetryTests()
    {
        _repository.GetIdByKeyAsync("dat-source", Arg.Any<CancellationToken>()).Returns(SourceId);
        _repository.GetIdByKeyAsync("dat-target", Arg.Any<CancellationToken>()).Returns(TargetId);
        _repository.LockAsync(Arg.Any<IReadOnlyCollection<(int Id, TaxonomyRowLock Mode)>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<int> { SourceId, TargetId });
        _unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_transaction);
        _unitOfWork.ConflictOf(Arg.Any<Exception>())
            .Returns(call => PostgreSqlTransactionConflicts.Classify(call.Arg<Exception>()));
        _service = new TaxonomyService<Region>(_repository, _unitOfWork, Microsoft.Extensions.Logging.Abstractions.NullLogger<TaxonomyService<Region>>.Instance);
    }

    [Fact]
    public async Task MergeAsync_LosesADeadlockTwice_Is409AndCommitsNothing()
    {
        // Arrange
        _repository.ReassignJunctionsAsync(SourceId, TargetId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateException("Save failed.", Postgres(PostgresErrorCodes.DeadlockDetected)));

        // Act
        var merged = await _service.MergeAsync("dat-source", "dat-target", default);

        // Assert
        merged.FirstError.Code.ShouldBe("Taxonomy.MergeConflict");
        await _repository.Received(2).ReassignJunctionsAsync(SourceId, TargetId, Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().DeleteAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await _transaction.Received(2).DisposeAsync();
    }

    [Fact]
    public async Task MergeAsync_FailureThatIsNoConflict_PropagatesWithoutARetry()
    {
        // Arrange
        var failure = new DbUpdateException("Save failed.", Postgres(PostgresErrorCodes.ForeignKeyViolation));
        _repository.ReassignJunctionsAsync(SourceId, TargetId, Arg.Any<CancellationToken>()).ThrowsAsync(failure);

        // Act
        var thrown = await Should.ThrowAsync<DbUpdateException>(() => _service.MergeAsync("dat-source", "dat-target", default));

        // Assert
        thrown.ShouldBeSameAs(failure);
        await _repository.Received(1).ReassignJunctionsAsync(SourceId, TargetId, Arg.Any<CancellationToken>());
        await _transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddAliasAsync_LosesADeadlock_Is409AndCommitsNothing()
    {
        // Arrange
        _unitOfWork.FlushAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateException("Save failed.", Postgres(PostgresErrorCodes.DeadlockDetected)));

        // Act
        var added = await _service.AddAliasAsync("dat-target", "Atlantis", default);

        // Assert
        added.FirstError.Code.ShouldBe("Taxonomy.AliasConflict");
        await _transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await _transaction.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task RemoveAliasAsync_CommitOutlastsTheLockTimeout_Is409()
    {
        // Arrange
        _repository.RemoveAliasAsync(TargetId, 7, Arg.Any<CancellationToken>()).Returns(true);
        _transaction.CommitAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateException("Save failed.", Postgres(PostgresErrorCodes.LockNotAvailable)));

        // Act
        var removed = await _service.RemoveAliasAsync("dat-target", 7, default);

        // Assert
        removed.FirstError.Code.ShouldBe("Taxonomy.AliasConflict");
        await _transaction.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task AliasWrites_BoundTheirLockWaitsBeforeLockingTheRow()
    {
        // Arrange
        var receipt = Substitute.For<IStagedTaxonomyAlias>();
        receipt.ReadAfterFlush().Returns(new TaxonomyAlias(7, "atlantis", "Installation"));
        _repository.AddAliasStagedAsync(TargetId, "atlantis", Arg.Any<CancellationToken>()).Returns(receipt);
        _repository.RemoveAliasAsync(TargetId, 7, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        (await _service.AddAliasAsync("dat-target", "Atlantis", default)).IsError.ShouldBeFalse();
        (await _service.RemoveAliasAsync("dat-target", 7, default)).IsError.ShouldBeFalse();

        // Assert
        Received.InOrder(() =>
        {
            _ = _unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>());
            _ = _repository.LimitLockWaitsAsync(TimeSpan.FromSeconds(10), Arg.Any<CancellationToken>());
            _ = _repository.LockAsync(Arg.Any<IReadOnlyCollection<(int Id, TaxonomyRowLock Mode)>>(), Arg.Any<CancellationToken>());
            _ = _repository.AddAliasStagedAsync(TargetId, "atlantis", Arg.Any<CancellationToken>());
            _ = _unitOfWork.FlushAsync(Arg.Any<CancellationToken>());
            receipt.ReadAfterFlush();
            _ = _transaction.CommitAsync(Arg.Any<CancellationToken>());
            _ = _unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>());
            _ = _repository.LimitLockWaitsAsync(TimeSpan.FromSeconds(10), Arg.Any<CancellationToken>());
            _ = _repository.LockAsync(Arg.Any<IReadOnlyCollection<(int Id, TaxonomyRowLock Mode)>>(), Arg.Any<CancellationToken>());
            _ = _repository.RemoveAliasAsync(TargetId, 7, Arg.Any<CancellationToken>());
            _ = _transaction.CommitAsync(Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public void IngestionFailure_AnIngestThatLosesADeadlock_IsDeferredAsCapacity() =>
        IngestionFailure.Classify(new DbUpdateException("Save failed.", Postgres(PostgresErrorCodes.DeadlockDetected)))
            .ShouldBe(IngestionFailureKind.Capacity);

    private static PostgresException Postgres(string sqlState) => new("Failed.", "ERROR", "ERROR", sqlState);
}
