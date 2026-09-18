using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Catalog.Commands.SetSourceStatus;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Dat;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Catalog;

/// <summary>
///     Proofs for the DAT-addressed source lifecycle command: strict enum-name parsing at
///     the boundary, a true no-op when the status is unchanged (no transaction, no
///     convergence), and the #160-review invariant on change — status write, per-entry-
///     platform dirty marking plus rematerialization flags, and stats events in one commit.
/// </summary>
public sealed class SetSourceStatusCommandHandlerTests
{
    private const int DatId = 5;
    private const int CatalogSourceId = 77;

    private readonly IDatRepository _datRepository = Substitute.For<IDatRepository>();
    private readonly ISourceLifecycle _sourceLifecycle = Substitute.For<ISourceLifecycle>();
    private readonly ICatalogProjectionService _catalogProjection = Substitute.For<ICatalogProjectionService>();
    private readonly ILibraryRepository _libraryRepository = Substitute.For<ILibraryRepository>();
    private readonly IAdminEventOutbox _outbox = Substitute.For<IAdminEventOutbox>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Create_NonPositiveDatId_ReturnsValidationError(int datId)
    {
        var result = SetSourceStatusCommand.Create(datId, "Disabled");

        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorType.Validation);
        result.FirstError.Code.ShouldBe("Command.InvalidDatId");
    }

    [Theory]
    [InlineData("disabled")] // exact enum-member names only: case differences are rejected
    [InlineData("DISABLED")]
    [InlineData("Retired")]
    [InlineData("")]
    public void Create_UnknownOrWrongCaseStatus_ReturnsValidationError(string status)
    {
        var result = SetSourceStatusCommand.Create(DatId, status);

        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorType.Validation);
        result.FirstError.Code.ShouldBe("Command.InvalidSourceStatus");
    }

    [Theory]
    [InlineData("Active, Discontinued")] // Enum.TryParse flag-sum: 1|2 == 3 == Disabled
    [InlineData("Discontinued, Active")]
    [InlineData("2")]
    [InlineData("1")]
    [InlineData(" Active")] // Enum.TryParse trims whitespace; the boundary must not
    [InlineData("Active ")]
    public void Create_LenientEnumParseFormsOfDefinedValues_ReturnsValidationError(string status)
    {
        var result = SetSourceStatusCommand.Create(DatId, status);

        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorType.Validation);
        result.FirstError.Code.ShouldBe("Command.InvalidSourceStatus");
    }

    [Theory]
    [InlineData("Active", CatalogSourceStatus.Active)]
    [InlineData("Discontinued", CatalogSourceStatus.Discontinued)]
    [InlineData("Disabled", CatalogSourceStatus.Disabled)]
    public void Create_ExactEnumMemberName_ParsesStatus(string status, CatalogSourceStatus expected)
    {
        var result = SetSourceStatusCommand.Create(DatId, status);

        result.IsError.ShouldBeFalse();
        result.Value.DatId.ShouldBe(DatId);
        result.Value.Status.ShouldBe(expected);
    }

    [Fact]
    public async Task HandleAsync_DatMissing_ReturnsDatNotFound()
    {
        _datRepository.GetByIdAsync(DatId, Arg.Any<CancellationToken>()).Returns((DatFile?)null);

        var result = await CreateHandler().HandleAsync(Command(CatalogSourceStatus.Disabled));

        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorType.NotFound);
        result.FirstError.Code.ShouldBe("Catalog.DatNotFound");
    }

    [Fact]
    public async Task HandleAsync_CatalogSourceRowMissing_ReturnsSourceNotFound()
    {
        SetupExistingDat(currentStatus: null);

        var result = await CreateHandler().HandleAsync(Command(CatalogSourceStatus.Disabled));

        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorType.NotFound);
        result.FirstError.Code.ShouldBe("Catalog.SourceNotFound");
    }

    [Fact]
    public async Task HandleAsync_StatusUnchanged_NoOpsWithoutTransactionOrConvergence()
    {
        SetupExistingDat(currentStatus: CatalogSourceStatus.Disabled);

        var result = await CreateHandler().HandleAsync(Command(CatalogSourceStatus.Disabled));

        result.IsError.ShouldBeFalse();
        result.Value.Status.ShouldBe(CatalogSourceStatus.Disabled);
        result.Value.Changed.ShouldBeFalse();

        // The no-op is total: no status write, no transaction, no dirty platforms, no
        // rematerialization flags, no realtime events.
        await _sourceLifecycle.DidNotReceiveWithAnyArgs().SetStatusAsync(default, default);
        await _unitOfWork.DidNotReceive().BeginTransactionAsync(Arg.Any<CancellationToken>());
        await _catalogProjection.DidNotReceiveWithAnyArgs().MarkPlatformDirtyAsync(default);
        await _catalogProjection.DidNotReceiveWithAnyArgs().MarkCatalogSourceDirtyAsync(default, default);
        await _libraryRepository.DidNotReceiveWithAnyArgs().FlagForRematerializationByPlatformAsync(default);
        await _outbox.DidNotReceive().EnqueueAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_StatusChanged_ConvergesEveryEntryPlatformAndCommitsOnce()
    {
        SetupExistingDat(currentStatus: CatalogSourceStatus.Active);
        _sourceLifecycle.GetEntryPlatformIdsAsync(CatalogSourceId, Arg.Any<CancellationToken>())
            .Returns([3, 5]);

        var result = await CreateHandler().HandleAsync(Command(CatalogSourceStatus.Disabled));

        result.IsError.ShouldBeFalse();
        result.Value.Status.ShouldBe(CatalogSourceStatus.Disabled);
        result.Value.Changed.ShouldBeTrue();

        await _sourceLifecycle.Received(1)
            .SetStatusAsync(CatalogSourceId, CatalogSourceStatus.Disabled, Arg.Any<CancellationToken>());
        foreach (int platformId in new[] { 3, 5 })
        {
            await _catalogProjection.Received(1)
                .MarkCatalogSourceDirtyAsync(
                    platformId,
                    CatalogSourceId,
                    Arg.Any<CancellationToken>());
            await _libraryRepository.Received(1)
                .FlagForRematerializationByPlatformAsync(platformId, Arg.Any<CancellationToken>());
        }

        await _outbox.Received(1)
            .EnqueueAsync(AdminRealtimeEventTypes.CoverageStatsChanged, Arg.Any<CancellationToken>());
        await _outbox.Received(1)
            .EnqueueAsync(AdminRealtimeEventTypes.HealthStatsChanged, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).BeginTransactionAsync(Arg.Any<CancellationToken>());
        await _transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_StatusWriteThrows_ReturnsDatabaseFailedWithoutCommit()
    {
        SetupExistingDat(currentStatus: CatalogSourceStatus.Active);
        _sourceLifecycle.SetStatusAsync(CatalogSourceId, CatalogSourceStatus.Disabled, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("status write unavailable"));

        var result = await CreateHandler().HandleAsync(Command(CatalogSourceStatus.Disabled));

        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorType.Failure);
        result.FirstError.Code.ShouldBe("Catalog.DatabaseFailed");
        await _transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    private static SetSourceStatusCommand Command(CatalogSourceStatus status) =>
        SetSourceStatusCommand.Create(DatId, status.ToString()).Value;

    private void SetupExistingDat(CatalogSourceStatus? currentStatus)
    {
        var datFile = DatFile.CreateNew("PSX", "PSX DAT", DatType.NoIntro, "psx.dat", fileId: 1);
        _datRepository.GetByIdAsync(DatId, Arg.Any<CancellationToken>()).Returns(datFile);
        _datRepository.GetCatalogSourceIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(CatalogSourceId);
        _sourceLifecycle.GetStatusAsync(CatalogSourceId, Arg.Any<CancellationToken>())
            .Returns(currentStatus);
        _unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_transaction);
    }

    private SetSourceStatusCommandHandler CreateHandler() =>
        new(
            _datRepository,
            _sourceLifecycle,
            _catalogProjection,
            _libraryRepository,
            _outbox,
            _unitOfWork,
            NullLogger<SetSourceStatusCommandHandler>.Instance);
}
