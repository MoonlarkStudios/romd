using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Export.Commands.QueueLibraryExport;
using Romd.Admin.Application.Export.Queries.ResolveExportScope;
using Romd.Application.Common.Content;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Export;

public sealed class QueueLibraryExportCommandTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-04T12:00:00.123456Z");

    [Fact]
    public async Task Handle_InvalidFormat_RejectsBeforeScopeResolutionOrTransaction()
    {
        var context = new Context();

        var result = await context.Handler.HandleAsync(new(null, (RomDeliveryFormat)99));

        result.FirstError.ShouldBe(RomDeliveryErrors.InvalidFormat());
        context.Scope.ReceivedCalls().ShouldBeEmpty();
        context.Unit.ReceivedCalls().ShouldBeEmpty();
        context.Acceptance.ReceivedCalls().ShouldBeEmpty();
    }

    public static TheoryData<Error> ScopeErrors => new()
    {
        ExportErrors.CurrentUserRequired(), ExportErrors.AdminRoleRequired(), ExportErrors.LibraryRequired(), ExportErrors.LibraryUnavailable()
    };

    [Theory]
    [MemberData(nameof(ScopeErrors))]
    public async Task Handle_ScopeRejected_PreservesErrorBeforeOpeningTransaction(Error error)
    {
        var context = new Context();
        context.Scope.HandleAsync(Arg.Any<ResolveExportScopeQuery>(), Arg.Any<CancellationToken>()).Returns(error);

        var result = await context.Handler.HandleAsync(new(7));

        result.FirstError.ShouldBe(error);
        context.Unit.ReceivedCalls().ShouldBeEmpty();
        context.Acceptance.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(RomDeliveryFormat.Original)]
    [InlineData(RomDeliveryFormat.Rvz)]
    [InlineData(RomDeliveryFormat.Chd)]
    public async Task Handle_LibraryScope_CapturesGenerationActorAndFormatBeforeTransaction(RomDeliveryFormat format)
    {
        var context = new Context { AuthorizedScope = new ExportScope.Library(7, 5) };
        var actor = context.Actor;
        context.BeforeBegin = () =>
        {
            context.Actor = Guid.NewGuid();
            context.AuthorizedScope = new ExportScope.Library(8, 6);
        };
        using var cancellation = new CancellationTokenSource();

        var result = await context.Handler.HandleAsync(new(7, format), cancellation.Token);

        result.IsError.ShouldBeFalse();
        context.Committed.ShouldBeTrue();
        var job = context.Job.ShouldNotBeNull();
        result.Value.ShouldBe(job.Id);
        job.Id.ShouldNotBe(Guid.Empty);
        job.ScopeKind.ShouldBe(ExportScopeKind.Library);
        job.LibraryId.ShouldBe(7);
        job.AuthorizedMaterializationGeneration.ShouldBe(5);
        job.CreatedByUserId.ShouldBe(actor);
        job.Format.ShouldBe(format);
        job.CreatedAt.ShouldBe(Now);
        context.Events.ShouldBe(["scope", "begin", "pending", "stage", "commit", "dispose"]);
        await context.Scope.Received(1).HandleAsync(new ResolveExportScopeQuery(7), cancellation.Token);
        await context.Acceptance.Received(1).LockAndHasPendingStagedAsync(cancellation.Token);
        await context.Acceptance.Received(1).AddStagedAsync(job, cancellation.Token);
        await context.Transaction.Received(1).CommitAsync(cancellation.Token);
    }

    [Fact]
    public async Task Handle_ExplicitAllCatalogScope_PreservesUnscopedJobShape()
    {
        var context = new Context();

        var result = await context.Handler.HandleAsync(new(null));

        result.IsError.ShouldBeFalse();
        var job = context.Job.ShouldNotBeNull();
        job.ScopeKind.ShouldBe(ExportScopeKind.AllCatalog);
        job.LibraryId.ShouldBeNull();
        job.AuthorizedMaterializationGeneration.ShouldBeNull();
        job.CreatedByUserId.ShouldBe(context.Actor);
    }

    [Fact]
    public async Task Handle_ExportAlreadyPending_ReturnsExactConflictWithoutCreatingOrCommitting()
    {
        var context = new Context();
        context.Acceptance.LockAndHasPendingStagedAsync(Arg.Any<CancellationToken>()).Returns(true);

        var result = await context.Handler.HandleAsync(new(null));

        result.FirstError.ShouldBe(ExportErrors.AlreadyInProgress());
        context.Job.ShouldBeNull();
        context.Committed.ShouldBeFalse();
        await context.Acceptance.DidNotReceive().AddStagedAsync(Arg.Any<ExportJob>(), Arg.Any<CancellationToken>());
        await context.Transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await context.Transaction.Received(1).DisposeAsync();
        await context.Scope.Received(1).HandleAsync(new ResolveExportScopeQuery(null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_StagingFails_PreservesFailureAndDisposesWithoutCommit()
    {
        var context = new Context();
        var cause = new IOException("staging failed");
        context.Acceptance.AddStagedAsync(Arg.Any<ExportJob>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(cause));

        var error = await CaptureAsync(() => context.Handler.HandleAsync(new(null)));

        error.ShouldBeSameAs(cause);
        await context.Transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await context.Transaction.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Handle_CancellationAfterStaging_DoesNotCommitAndPreservesToken()
    {
        var context = new Context();
        using var cancellation = new CancellationTokenSource();
        context.Acceptance.AddStagedAsync(Arg.Any<ExportJob>(), cancellation.Token).Returns(_ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        });

        var error = await CaptureAsync(() => context.Handler.HandleAsync(new(null), cancellation.Token));

        error.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        await context.Transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await context.Transaction.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Handle_CommitFails_PreservesCauseWithoutReturningAcceptedIdentity()
    {
        var context = new Context();
        var cause = new IOException("commit failed");
        context.Transaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(cause));

        var error = await CaptureAsync(() => context.Handler.HandleAsync(new(null)));

        error.ShouldBeSameAs(cause);
        await context.Transaction.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Handle_DisposalFailsAfterCommit_ReturnsDurableJobIdentity()
    {
        var context = new Context();
        context.Transaction.DisposeAsync().Returns(new ValueTask(Task.FromException(new IOException("cleanup failed"))));

        var result = await context.Handler.HandleAsync(new(null));

        context.Committed.ShouldBeTrue();
        result.Value.ShouldBe(context.Job.ShouldNotBeNull().Id);
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> operation)
    {
        try
        { await operation(); return null; }
        catch (Exception error) { return error; }
    }

    private sealed class Context
    {
        public IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope> Scope { get; } = Substitute.For<IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope>>();
        public ICurrentUser Current { get; } = Substitute.For<ICurrentUser>();
        public IExportJobAcceptance Acceptance { get; } = Substitute.For<IExportJobAcceptance>();
        public IUnitOfWork Unit { get; } = Substitute.For<IUnitOfWork>();
        public ITransaction Transaction { get; } = Substitute.For<ITransaction>();
        public List<string> Events { get; } = [];
        public ExportScope AuthorizedScope { get; set; } = new ExportScope.AllCatalog();
        public Guid Actor { get; set; } = Guid.NewGuid();
        public ExportJob? Job { get; private set; }
        public bool Inside { get; private set; }
        public bool Committed { get; private set; }
        public Action? BeforeBegin { get; set; }
        public QueueLibraryExportCommandHandler Handler { get; }

        public Context()
        {
            Current.UserId.Returns(_ => Actor);
            Scope.HandleAsync(Arg.Any<ResolveExportScopeQuery>(), Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Inside.ShouldBeFalse();
                Events.Add("scope");
                return new AuthorizedExportScope(AuthorizedScope, null);
            });
            Unit.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Events.Add("begin");
                BeforeBegin?.Invoke();
                Inside = true;
                return Transaction;
            });
            Acceptance.LockAndHasPendingStagedAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Inside.ShouldBeTrue();
                Events.Add("pending");
                return false;
            });
            Acceptance.AddStagedAsync(Arg.Any<ExportJob>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Inside.ShouldBeTrue();
                Events.Add("stage");
                Job = call.Arg<ExportJob>();
                return Task.CompletedTask;
            });
            Transaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Events.Add("commit");
                Committed = true;
                return Task.CompletedTask;
            });
            Transaction.DisposeAsync().Returns(_ =>
            {
                Events.Add("dispose");
                Inside = false;
                return ValueTask.CompletedTask;
            });
            Handler = new(Scope, Current, Acceptance, Unit, new FixedClock(), NullLogger<QueueLibraryExportCommandHandler>.Instance);
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
