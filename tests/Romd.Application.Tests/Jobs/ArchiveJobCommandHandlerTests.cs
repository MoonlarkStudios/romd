using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Commands.SetJobArchived;
using Romd.Application.Common.Security;
using Romd.Domain.Catalog;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Jobs;

public sealed class SetJobArchivedCommandHandlerTests
{
    private readonly IJobRepository _jobs = Substitute.For<IJobRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    public SetJobArchivedCommandHandlerTests() => _unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Substitute.For<ITransaction>());

    [Fact]
    public async Task HandleAsync_OwnedTerminalJob_ArchivesIt()
    {
        var userId = Guid.NewGuid();
        var job = UploadJob.Create("owned.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: userId);
        job.Fail("complete enough for archive", failedAt: DateTimeOffset.UtcNow);
        _jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        _jobs.SetArchivedAsync(job.Id, true, Arg.Any<JobVisibilityScope>(), Arg.Any<CancellationToken>()).Returns(_ => { job.Archive(archivedAt: DateTimeOffset.UtcNow); return job; });

        var result = await Handler(User(userId)).HandleAsync(new SetJobArchivedCommand(job.Id, true));

        result.IsError.ShouldBeFalse();
        result.Value.IsArchived.ShouldBeTrue();
        await _jobs.Received(1).SetArchivedAsync(job.Id, true, Arg.Any<JobVisibilityScope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ActiveJob_IsAConflict()
    {
        var userId = Guid.NewGuid();
        var job = UploadJob.Create("active.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: userId);
        _jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        _jobs.SetArchivedAsync(job.Id, true, Arg.Any<JobVisibilityScope>(), Arg.Any<CancellationToken>()).Returns(_ => { job.Archive(archivedAt: DateTimeOffset.UtcNow); return job; });

        var result = await Handler(User(userId)).HandleAsync(new SetJobArchivedCommand(job.Id, true));

        result.FirstError.ShouldBe(JobErrors.NotTerminal());
        result.FirstError.Type.ShouldBe(ErrorType.Conflict);
        job.IsArchived.ShouldBeFalse();
        await _jobs.DidNotReceiveWithAnyArgs().UpdateStagedAsync(default!, default);
    }

    [Fact]
    public async Task HandleAsync_AnotherUsersJob_IsNotFound()
    {
        var job = UploadJob.Create("other.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: Guid.NewGuid());
        job.Fail("complete enough for archive", failedAt: DateTimeOffset.UtcNow);
        _jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        _jobs.SetArchivedAsync(job.Id, true, Arg.Any<JobVisibilityScope>(), Arg.Any<CancellationToken>()).Returns(_ => { job.Archive(archivedAt: DateTimeOffset.UtcNow); return job; });

        var result = await Handler(User(Guid.NewGuid())).HandleAsync(new SetJobArchivedCommand(job.Id, true));

        result.FirstError.ShouldBe(JobErrors.NotFound(job.Id));
        job.IsArchived.ShouldBeFalse();
        await _jobs.DidNotReceiveWithAnyArgs().UpdateStagedAsync(default!, default);
    }

    [Fact]
    public async Task HandleAsync_UnknownJob_IsNotFound()
    {
        var id = Guid.NewGuid();

        var result = await Handler(User(Guid.NewGuid(), RomdRoleType.Admin)).HandleAsync(new SetJobArchivedCommand(id, true));

        result.FirstError.ShouldBe(JobErrors.NotFound(id));
    }

    [Fact]
    public async Task HandleAsync_ArtworkImportJob_ArchivesInsideATransaction()
    {
        var userId = Guid.NewGuid();
        var job = ArtworkImportJob.Create(Guid.NewGuid(), "Game", 1, 1, ArtworkRole.Poster, 1,
            "steamgriddb", "2", "3", "https://cdn2.steamgriddb.com/grid/test.png", "Artist", TimeProvider.System.GetUtcNow(), userId);
        job.Fail("complete enough for archive", failedAt: DateTimeOffset.UtcNow);
        var transaction = Substitute.For<ITransaction>();
        _unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        _jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        _jobs.SetArchivedAsync(job.Id, true, Arg.Any<JobVisibilityScope>(), Arg.Any<CancellationToken>()).Returns(_ => { job.Archive(archivedAt: DateTimeOffset.UtcNow); return job; });

        var result = await Handler(User(userId)).HandleAsync(new SetJobArchivedCommand(job.Id, true));

        result.IsError.ShouldBeFalse();
        Received.InOrder(() =>
        {
            _unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>());
            _jobs.SetArchivedAsync(job.Id, true, Arg.Any<JobVisibilityScope>(), Arg.Any<CancellationToken>());
            transaction.CommitAsync(Arg.Any<CancellationToken>());
        });
    }

    [Theory]
    [InlineData("active")]
    [InlineData("missing")]
    [InlineData("invisible")]
    public async Task HandleAsync_StaleTerminalRead_DoesNotCommitZeroRowArchive(string changed)
    {
        var actor = Guid.NewGuid();
        var terminal = UploadJob.Create("terminal.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: actor);
        terminal.Fail("done", failedAt: DateTimeOffset.UtcNow);
        var active = UploadJob.Create("retry.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: changed == "invisible" ? Guid.NewGuid() : actor, requestId: terminal.Id, batchId: terminal.CorrelationId);
        _jobs.GetByIdAsync(terminal.Id, Arg.Any<CancellationToken>()).Returns(terminal, changed == "missing" ? null : active);
        _jobs.SetArchivedAsync(terminal.Id, true, Arg.Any<JobVisibilityScope>(), Arg.Any<CancellationToken>()).Returns((Job?)null);
        var transaction = Substitute.For<ITransaction>();
        _unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        var result = await Handler(User(actor)).HandleAsync(new(terminal.Id, true));
        result.FirstError.ShouldBe(changed == "active" ? JobErrors.NotTerminal() : JobErrors.NotFound(terminal.Id));
        await transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await transaction.Received(1).DisposeAsync();
        await _jobs.DidNotReceiveWithAnyArgs().UpdateStagedAsync(default!, default);
    }

    [Fact]
    public async Task HandleAsync_RepresentationReadFails_DoesNotCommitMutation()
    {
        var actor = Guid.NewGuid();
        var job = UploadJob.Create("terminal.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: actor);
        job.Fail("done", failedAt: DateTimeOffset.UtcNow);
        _jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        _jobs.SetArchivedAsync(job.Id, true, Arg.Any<JobVisibilityScope>(), Arg.Any<CancellationToken>()).Returns(job);
        var transaction = Substitute.For<ITransaction>();
        _unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        var catalog = TestSystemCatalog.Create();
        catalog.GetSystemKeysAsync(Arg.Any<CancellationToken>()).Returns<Task<Romd.Application.Common.Systems.SystemKeys>>(_ => throw new InvalidOperationException("Reference lookup failed."));
        var handler = new SetJobArchivedCommandHandler(_jobs, _unitOfWork, User(actor), catalog, Microsoft.Extensions.Logging.Abstractions.NullLogger<SetJobArchivedCommandHandler>.Instance);
        await Should.ThrowAsync<InvalidOperationException>(() => handler.HandleAsync(new(job.Id, true)));
        await transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await transaction.Received(1).DisposeAsync();
    }

    private SetJobArchivedCommandHandler Handler(ICurrentUser currentUser)
    {
        return new(_jobs, _unitOfWork, currentUser, TestSystemCatalog.Create(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SetJobArchivedCommandHandler>.Instance);
    }

    private static ICurrentUser User(Guid userId, RomdRoleType role = RomdRoleType.User)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(userId);
        currentUser.HasRole(Arg.Any<RomdRoleType>()).Returns(call => role >= call.Arg<RomdRoleType>());
        return currentUser;
    }
}
