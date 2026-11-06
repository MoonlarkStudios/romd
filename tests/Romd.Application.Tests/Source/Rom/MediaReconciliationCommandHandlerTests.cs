using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Rom.Commands.RetryMediaReconciliation;
using Romd.Admin.Application.Source.Rom.Commands.StartMediaReconciliationSelection;
using Romd.Admin.Application.Source.Rom.Commands.StartMediaReconciliationSubject;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Application.Tests.Content;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Rom;

public sealed class MediaReconciliationCommandHandlerTests
{
    private readonly IMediaReconciliationRequests _requests = Substitute.For<IMediaReconciliationRequests>();
    private readonly IUnitOfWork _work = Substitute.For<IUnitOfWork>();
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IReferenceCatalogService _catalog = Substitute.For<IReferenceCatalogService>();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _requestId = Guid.NewGuid();
    private readonly Guid _previous = Guid.NewGuid();

    public MediaReconciliationCommandHandlerTests()
    {
        _user.UserId.Returns(_actor);
        _work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_transaction);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("selection")]
    [InlineData("retry")]
    public async Task HandleAsync_AcceptedIntent_CommitsOnceAndReturnsActualJob(string operation)
    {
        var accepted = Acceptance();
        Stage(operation).Returns((ErrorOr<MediaReconciliationAcceptance>)accepted);
        using var cancellation = new CancellationTokenSource();

        var result = await HandleAsync(operation, cancellation.Token);

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(accepted.Job.Id);
        await _work.Received(1).BeginTransactionAsync(cancellation.Token);
        await _transaction.Received(1).CommitAsync(cancellation.Token);
        await _transaction.Received(1).DisposeAsync();
        var call = _requests.ReceivedCalls().ShouldHaveSingleItem();
        call.GetArguments().Last().ShouldBe(cancellation.Token);
        if (operation == "subject")
        {
            var demand = call.GetArguments()[0].ShouldBeOfType<MediaReconciliationDemand>();
            demand.RequestId.ShouldBe(_requestId);
            demand.Subject.ShouldBe(new MediaReconciliationSubject.CatalogCdLayout(7));
            demand.ConversionRequested.ShouldBeTrue();
            demand.Cause.ShouldBe(MediaReconciliationCause.Admin);
            demand.ActorId.ShouldBe(_actor);
            demand.SelectionJobId.ShouldBeNull();
        }
        else if (operation == "selection")
        {
            call.GetArguments()[0].ShouldBe(_requestId);
            call.GetArguments()[1].ShouldBeOfType<MediaReconciliationFilter.AllSubjects>();
            call.GetArguments()[2].ShouldBe(4L * int.MaxValue);
            call.GetArguments()[3].ShouldBe(true);
            call.GetArguments()[4].ShouldBe(MediaReconciliationCause.Admin);
            call.GetArguments()[5].ShouldBe(_actor);
        }
        else
        {
            call.GetArguments()[0].ShouldBe(_requestId);
            call.GetArguments()[1].ShouldBe(_previous);
            call.GetArguments()[2].ShouldBe(_actor);
        }
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("selection")]
    [InlineData("retry")]
    public async Task HandleAsync_StoreRejection_DisposesWithoutCommitAndPreservesErrors(string operation)
    {
        var error = Error.Conflict("MediaReconciliation.Cancelling", "Wait for settlement.");
        Stage(operation).Returns((ErrorOr<MediaReconciliationAcceptance>)error);

        var result = await HandleAsync(operation);

        result.Errors.ShouldBe([error]);
        await _transaction.DidNotReceiveWithAnyArgs().CommitAsync(default);
        await _transaction.Received(1).DisposeAsync();
    }

    [Theory]
    [InlineData("subject", false)]
    [InlineData("selection", false)]
    [InlineData("retry", false)]
    [InlineData("subject", true)]
    [InlineData("selection", true)]
    [InlineData("retry", true)]
    public async Task HandleAsync_StagingThrowsOrCancels_DisposesWithoutCommitAndPreservesCause(string operation, bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        Exception failure = cancel ? new OperationCanceledException(cancellation.Token) : new InvalidOperationException("database unavailable");
        Stage(operation).Returns(Task.FromException<ErrorOr<MediaReconciliationAcceptance>>(failure));

        var thrown = await Should.ThrowAsync<Exception>(() => HandleAsync(operation, cancellation.Token));

        if (cancel)
            thrown.ShouldBeAssignableTo<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        else
            thrown.ShouldBeSameAs(failure);
        await _transaction.DidNotReceiveWithAnyArgs().CommitAsync(default);
        await _transaction.Received(1).DisposeAsync();
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("selection")]
    [InlineData("retry")]
    public async Task HandleAsync_CancelledAfterStaging_DoesNotCommit(string operation)
    {
        using var cancellation = new CancellationTokenSource();
        Stage(operation).Returns(_ =>
        {
            cancellation.Cancel();
            return (ErrorOr<MediaReconciliationAcceptance>)Acceptance();
        });

        await Should.ThrowAsync<OperationCanceledException>(() => HandleAsync(operation, cancellation.Token));

        await _transaction.DidNotReceiveWithAnyArgs().CommitAsync(default);
        await _transaction.Received(1).DisposeAsync();
    }

    [Theory]
    [InlineData("subject", false)]
    [InlineData("selection", false)]
    [InlineData("retry", false)]
    [InlineData("subject", true)]
    [InlineData("selection", true)]
    [InlineData("retry", true)]
    public async Task HandleAsync_MissingActor_RefusesBeforeResolvingOrStaging(string operation, bool empty)
    {
        _user.UserId.Returns(empty ? Guid.Empty : null);

        var result = await HandleAsync(operation);

        result.FirstError.ShouldBe(MediaReconciliationErrors.ActorRequired());
        await AssertNoEffectsAsync();
        _catalog.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("LogicalRom", 0, false)]
    [InlineData("LogicalRom", -1, false)]
    [InlineData("unknown", 7, false)]
    [InlineData("Representation", 7, true)]
    public async Task Subject_InvalidSubject_RefusesBeforeTransaction(string kind, int id, bool conversion)
    {
        var result = await Subject().HandleAsync(new(_requestId, kind, id, conversion));

        result.FirstError.Code.ShouldBe(MediaReconciliationErrors.InvalidRequestCode);
        await AssertNoEffectsAsync();
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("selection")]
    [InlineData("retry")]
    public async Task HandleAsync_EmptyRequestIdentity_RefusesBeforeTransaction(string operation)
    {
        var result = operation switch
        {
            "subject" => await Subject().HandleAsync(new(Guid.Empty, "LogicalRom", 7, false)),
            "selection" => await Selection().HandleAsync(new(Guid.Empty, "AllSubjects", null, null, 5, false)),
            _ => await Retry().HandleAsync(new(Guid.Empty, _previous))
        };

        result.FirstError.Code.ShouldBe(MediaReconciliationErrors.InvalidRequestCode);
        await AssertNoEffectsAsync();
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(8_589_934_589L)]
    public async Task Selection_InvalidLimit_RefusesBeforeResolvingScope(long limit)
    {
        var result = await Selection().HandleAsync(new(_requestId, "KindSubjects", "gamecube-disc", "gc", limit, false));

        result.FirstError.Description.ShouldBe("Provide an exact request identity and a positive bounded selection limit.");
        await AssertNoEffectsAsync();
        _catalog.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("AllSubjects", "gamecube-disc", null, false)]
    [InlineData("AllSubjects", null, "gc", false)]
    [InlineData("RetainedRepresentations", null, null, true)]
    [InlineData("KindSubjects", "unknown", null, false)]
    [InlineData("unknown", null, null, false)]
    public async Task Selection_UnsupportedScope_RefusesBeforeTransaction(string filter, string? kind, string? system, bool conversion)
    {
        _catalog.ResolveSystemIdAsync("gc", Arg.Any<CancellationToken>()).Returns((int?)9);

        var result = await Selection().HandleAsync(new(_requestId, filter, kind, system, 5, conversion));

        result.FirstError.Description.ShouldBe("Choose a supported filter and its exact kind/system scope. Retained representations only request verification.");
        await AssertNoEffectsAsync();
    }

    [Fact]
    public async Task Selection_UnknownSystem_RefusesBeforeTransaction()
    {
        var result = await Selection().HandleAsync(new(_requestId, "KindSubjects", "gamecube-disc", "missing", 5, false));

        result.FirstError.Description.ShouldBe("Choose a known system key.");
        await AssertNoEffectsAsync();
    }

    [Fact]
    public async Task Selection_KnownSystem_StagesResolvedExactKindScope()
    {
        _catalog.ResolveSystemIdAsync("gc", Arg.Any<CancellationToken>()).Returns((int?)9);
        Stage("selection").Returns((ErrorOr<MediaReconciliationAcceptance>)Acceptance());

        var result = await Selection().HandleAsync(new(_requestId, "KindSubjects", "gamecube-disc", "gc", 5, false));

        result.IsError.ShouldBeFalse();
        await _requests.Received(1).StageSelectionAsync(_requestId, Arg.Is<MediaReconciliationFilter>(filter =>
                filter is MediaReconciliationFilter.KindSubjects && ((MediaReconciliationFilter.KindSubjects)filter).Kind == "gamecube-disc" &&
                ((MediaReconciliationFilter.KindSubjects)filter).PlatformId == 9),
            5, false, MediaReconciliationCause.Admin, _actor, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Retry_EmptyPreviousIdentity_RefusesBeforeTransaction()
    {
        var result = await Retry().HandleAsync(new(_requestId, Guid.Empty));

        result.FirstError.Description.ShouldBe("Provide exact request and previous job identities.");
        await AssertNoEffectsAsync();
    }

    private async Task AssertNoEffectsAsync()
    {
        await _work.DidNotReceiveWithAnyArgs().BeginTransactionAsync(default);
        _requests.ReceivedCalls().ShouldBeEmpty();
    }

    private StartMediaReconciliationSubjectCommandHandler Subject() => new(_requests, _work, _user, NullLogger<StartMediaReconciliationSubjectCommandHandler>.Instance);
    private StartMediaReconciliationSelectionCommandHandler Selection() => new(_requests, _work, _user, TestMediaKinds.Production, _catalog, NullLogger<StartMediaReconciliationSelectionCommandHandler>.Instance);
    private RetryMediaReconciliationCommandHandler Retry() => new(_requests, _work, _user, NullLogger<RetryMediaReconciliationCommandHandler>.Instance);
    private Task<ErrorOr<Guid>> HandleAsync(string operation, CancellationToken ct = default) => operation switch
    {
        "subject" => Subject().HandleAsync(new(_requestId, "CatalogCdLayout", 7, true), ct),
        "selection" => Selection().HandleAsync(new(_requestId, "AllSubjects", null, null, 4L * int.MaxValue, true), ct),
        "retry" => Retry().HandleAsync(new(_requestId, _previous), ct),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private Task<ErrorOr<MediaReconciliationAcceptance>> Stage(string operation) => operation switch
    {
        "subject" => _requests.StageSubjectAsync(Arg.Any<MediaReconciliationDemand>(), Arg.Any<CancellationToken>()),
        "selection" => _requests.StageSelectionAsync(Arg.Any<Guid>(), Arg.Any<MediaReconciliationFilter>(), Arg.Any<long>(), Arg.Any<bool>(),
            Arg.Any<MediaReconciliationCause>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()),
        "retry" => _requests.StageRetryAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private MediaReconciliationAcceptance Acceptance() => new(MediaReconciliationJob.Create(Guid.NewGuid(),
        new MediaReconciliationRequest.SubjectWork(new MediaReconciliationSubject.CatalogCdLayout(7)), "subject", null, _actor, DateTimeOffset.UnixEpoch), true, false);
}
