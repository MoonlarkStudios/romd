using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Libraries.Commands.CreateLibrary;
using Romd.Admin.Application.Libraries.Commands.DeleteLibrary;
using Romd.Admin.Application.Libraries.Commands.ForceMaterializeLibrary;
using Romd.Admin.Application.Libraries.Commands.UpdateLibrary;
using Romd.Application.Common.Security;
using Romd.Contracts.Management.Realtime;
using Romd.Domain.Jobs;
using Romd.Domain.Libraries;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Libraries;

public sealed class LibraryCommandHandlerTests
{
    [Fact]
    public async Task CreateLibrary_ValidDefault_CommitsEventBeforeScheduling()
    {
        var order = new List<string>();
        var repository = Substitute.For<ILibraryRepository>();
        var scheduler = Substitute.For<ILibraryMaterializationScheduler>();
        var outbox = Substitute.For<IAdminEventOutbox>();
        var created = NewLibrary(7, "Arcade");
        var transaction = new RecordingTransaction(order);
        var unitOfWork = new RecordingUnitOfWork(order, transaction);
        repository.ValidateConfigurationReferencesAsync(
                Arg.Any<LibraryConfiguration>(),
                Arg.Any<CancellationToken>())
            .Returns((string?)null);
        repository.ClearDefaultAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Record(order, "clear-default"));
        repository.AddStagedAsync(Arg.Any<Library>(), Arg.Any<CancellationToken>())
            .Returns(_ => Record(order, "add"));
        repository.GetByNameAsync("Arcade", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                order.Add("read-created");
                return created;
            });
        outbox.EnqueueAsync(
                AdminRealtimeEventTypes.LibraryUpdated,
                Arg.Any<AdminRealtimeLibraryUpdatedPayload>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Record(order, "outbox"));
        scheduler.EnqueueIfNeededAsync(7, Arg.Any<CancellationToken>())
            .Returns(_ => RecordTrue(order, "schedule"));
        var handler = new CreateLibraryCommandHandler(TestSystemCatalog.Create(),
            repository,
            scheduler,
            outbox,
            unitOfWork,
            NullLogger<CreateLibraryCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            new CreateLibraryCommand("Arcade", new LibraryConfiguration(), true));

        result.IsError.ShouldBeFalse();
        order.ShouldBe([
            "begin",
            "clear-default",
            "add",
            "flush",
            "read-created",
            "outbox",
            "commit",
            "dispose",
            "schedule"
        ]);
    }

    [Fact]
    public async Task CreateLibrary_TransactionDisposeFailsAfterCommit_StillSchedulesAndSucceeds()
    {
        var order = new List<string>();
        var repository = Substitute.For<ILibraryRepository>();
        var scheduler = Substitute.For<ILibraryMaterializationScheduler>();
        var outbox = Substitute.For<IAdminEventOutbox>();
        var created = NewLibrary(7, "Arcade");
        var unitOfWork = new RecordingUnitOfWork(
            order,
            new RecordingTransaction(order, disposeException: new InvalidOperationException("dispose")));
        repository.ValidateConfigurationReferencesAsync(
                Arg.Any<LibraryConfiguration>(),
                Arg.Any<CancellationToken>())
            .Returns((string?)null);
        repository.GetByNameAsync("Arcade", Arg.Any<CancellationToken>()).Returns(created);
        var handler = new CreateLibraryCommandHandler(TestSystemCatalog.Create(),
            repository,
            scheduler,
            outbox,
            unitOfWork,
            NullLogger<CreateLibraryCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            new CreateLibraryCommand("Arcade", new LibraryConfiguration(), false));

        result.IsError.ShouldBeFalse();
        await scheduler.Received(1).EnqueueIfNeededAsync(7, Arg.Any<CancellationToken>());
        order.IndexOf("commit").ShouldBeLessThan(order.IndexOf("dispose"));
    }

    [Fact]
    public async Task CreateLibrary_SchedulerCancellationAfterCommit_ReturnsDurableSuccess()
    {
        var repository = Substitute.For<ILibraryRepository>();
        var scheduler = Substitute.For<ILibraryMaterializationScheduler>();
        var outbox = Substitute.For<IAdminEventOutbox>();
        var created = NewLibrary(7, "Arcade");
        var unitOfWork = new RecordingUnitOfWork([], new RecordingTransaction([]));
        repository.ValidateConfigurationReferencesAsync(
                Arg.Any<LibraryConfiguration>(),
                Arg.Any<CancellationToken>())
            .Returns((string?)null);
        repository.GetByNameAsync("Arcade", Arg.Any<CancellationToken>()).Returns(created);
        scheduler.EnqueueIfNeededAsync(7, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new OperationCanceledException("post-commit"));
        var handler = new CreateLibraryCommandHandler(TestSystemCatalog.Create(),
            repository,
            scheduler,
            outbox,
            unitOfWork,
            NullLogger<CreateLibraryCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            new CreateLibraryCommand("Arcade", new LibraryConfiguration(), false));

        result.IsError.ShouldBeFalse();
        unitOfWork.Transaction.CommitCount.ShouldBe(1);
    }

    [Fact]
    public async Task UpdateLibrary_SwitchDefault_CommitsTargetAndEventBeforeScheduling()
    {
        var order = new List<string>();
        var repository = Substitute.For<ILibraryRepository>();
        var scheduler = Substitute.For<ILibraryMaterializationScheduler>();
        var outbox = Substitute.For<IAdminEventOutbox>();
        var library = NewLibrary(7, "Old");
        var unitOfWork = new RecordingUnitOfWork(order, new RecordingTransaction(order));
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(library);
        repository.ValidateConfigurationReferencesAsync(
                Arg.Any<LibraryConfiguration>(),
                Arg.Any<CancellationToken>())
            .Returns((string?)null);
        repository.ClearDefaultAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Record(order, "clear-default"));
        repository.UpdateStagedAsync(library, Arg.Any<CancellationToken>())
            .Returns(_ => Record(order, "update"));
        outbox.EnqueueAsync(
                AdminRealtimeEventTypes.LibraryUpdated,
                Arg.Any<AdminRealtimeLibraryUpdatedPayload>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Record(order, "outbox"));
        scheduler.EnqueueIfNeededAsync(7, Arg.Any<CancellationToken>())
            .Returns(_ => RecordTrue(order, "schedule"));
        var handler = new UpdateLibraryCommandHandler(TestSystemCatalog.Create(),
            repository,
            scheduler,
            outbox,
            unitOfWork,
            NullLogger<UpdateLibraryCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            new UpdateLibraryCommand(7, "New", new LibraryConfiguration(), true));

        result.IsError.ShouldBeFalse();
        library.IsDefault.ShouldBeTrue();
        order.ShouldBe([
            "begin",
            "clear-default",
            "update",
            "outbox",
            "commit",
            "dispose",
            "schedule"
        ]);
    }

    [Fact]
    public async Task UpdateLibrary_TransactionDisposeFailsAfterCommit_StillSchedulesWarnsAndSucceeds()
    {
        var order = new List<string>();
        var repository = Substitute.For<ILibraryRepository>();
        var scheduler = Substitute.For<ILibraryMaterializationScheduler>();
        var outbox = Substitute.For<IAdminEventOutbox>();
        var logger = new RecordingLogger<UpdateLibraryCommandHandler>();
        var library = NewLibrary(7, "Old");
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(library);
        repository.ValidateConfigurationReferencesAsync(
                Arg.Any<LibraryConfiguration>(),
                Arg.Any<CancellationToken>())
            .Returns((string?)null);
        scheduler.EnqueueIfNeededAsync(7, Arg.Any<CancellationToken>())
            .Returns(_ => RecordTrue(order, "schedule"));
        var unitOfWork = new RecordingUnitOfWork(
            order,
            new RecordingTransaction(order, disposeException: new InvalidOperationException("dispose")));
        var handler = new UpdateLibraryCommandHandler(TestSystemCatalog.Create(),
            repository,
            scheduler,
            outbox,
            unitOfWork,
            logger);

        var result = await handler.HandleAsync(
            new UpdateLibraryCommand(7, "New", null, null));

        result.IsError.ShouldBeFalse();
        order.ShouldContain("schedule");
        order.IndexOf("dispose").ShouldBeLessThan(order.IndexOf("schedule"));
        var warning = logger.Entries.Single(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("transaction cleanup failed", StringComparison.Ordinal));
        warning.Exception.ShouldBeOfType<InvalidOperationException>();
        warning.Message.ShouldContain("transaction cleanup failed");
    }

    [Fact]
    public async Task UpdateLibrary_SchedulerCancellationAfterCommit_ReturnsDurableSuccessAndWarns()
    {
        var repository = Substitute.For<ILibraryRepository>();
        var scheduler = Substitute.For<ILibraryMaterializationScheduler>();
        var outbox = Substitute.For<IAdminEventOutbox>();
        var logger = new RecordingLogger<UpdateLibraryCommandHandler>();
        var library = NewLibrary(7, "Old");
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(library);
        repository.ValidateConfigurationReferencesAsync(
                Arg.Any<LibraryConfiguration>(),
                Arg.Any<CancellationToken>())
            .Returns((string?)null);
        scheduler.EnqueueIfNeededAsync(7, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new OperationCanceledException("post-commit"));
        var unitOfWork = new RecordingUnitOfWork([], new RecordingTransaction([]));
        var handler = new UpdateLibraryCommandHandler(TestSystemCatalog.Create(),
            repository,
            scheduler,
            outbox,
            unitOfWork,
            logger);

        var result = await handler.HandleAsync(
            new UpdateLibraryCommand(7, "New", null, null));

        result.IsError.ShouldBeFalse();
        unitOfWork.Transaction.CommitCount.ShouldBe(1);
        var warning = logger.Entries.Single(entry => entry.Level == LogLevel.Warning);
        warning.Exception.ShouldBeOfType<OperationCanceledException>();
        warning.Message.ShouldContain("recurring dispatcher will recover");
    }

    [Fact]
    public async Task UpdateLibrary_CommitCancellation_PropagatesAndDoesNotSchedule()
    {
        var repository = Substitute.For<ILibraryRepository>();
        var scheduler = Substitute.For<ILibraryMaterializationScheduler>();
        var outbox = Substitute.For<IAdminEventOutbox>();
        var library = NewLibrary(7, "Old");
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(library);
        repository.ValidateConfigurationReferencesAsync(
                Arg.Any<LibraryConfiguration>(),
                Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var order = new List<string>();
        var unitOfWork = new RecordingUnitOfWork(
            order,
            new RecordingTransaction(order, commitException: new OperationCanceledException("commit")));
        var handler = new UpdateLibraryCommandHandler(TestSystemCatalog.Create(),
            repository,
            scheduler,
            outbox,
            unitOfWork,
            NullLogger<UpdateLibraryCommandHandler>.Instance);

        await Should.ThrowAsync<OperationCanceledException>(() => handler.HandleAsync(
            new UpdateLibraryCommand(7, "New", null, null)));

        await scheduler.DidNotReceiveWithAnyArgs().EnqueueIfNeededAsync(default, default);
    }

    [Fact]
    public async Task DeleteLibrary_DefaultLibrary_ReturnsConflictInsideReadTransaction()
    {
        var repository = Substitute.For<ILibraryRepository>();
        var library = NewLibrary(7, "Default");
        library.MarkAsDefault();
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(library);
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        var outbox = Substitute.For<IAdminEventOutbox>();
        var handler = new DeleteLibraryCommandHandler(
            repository,
            outbox,
            unitOfWork,
            NullLogger<DeleteLibraryCommandHandler>.Instance);

        var result = await handler.HandleAsync(new DeleteLibraryCommand(7));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Libraries.DefaultLibraryUndeletable");
        await unitOfWork.Received(1).BeginTransactionAsync(Arg.Any<CancellationToken>());
        await repository.DidNotReceiveWithAnyArgs().DeleteAsync(default, default);
        await outbox.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default);
    }

    [Fact]
    public async Task DeleteLibrary_ExistingLibrary_DeletesAndCommitsEventOnce()
    {
        var order = new List<string>();
        var repository = Substitute.For<ILibraryRepository>();
        var outbox = Substitute.For<IAdminEventOutbox>();
        var library = NewLibrary(7, "Disposable");
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(library);
        repository.DeleteAsync(7, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            order.Add("delete");
            return true;
        });
        outbox.EnqueueAsync(
                AdminRealtimeEventTypes.LibraryUpdated,
                Arg.Any<AdminRealtimeLibraryUpdatedPayload>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Record(order, "outbox"));
        var unitOfWork = new RecordingUnitOfWork(order, new RecordingTransaction(order));
        var handler = new DeleteLibraryCommandHandler(
            repository,
            outbox,
            unitOfWork,
            NullLogger<DeleteLibraryCommandHandler>.Instance);

        var result = await handler.HandleAsync(new DeleteLibraryCommand(7));

        result.IsError.ShouldBeFalse();
        order.ShouldBe(["begin", "delete", "outbox", "commit", "dispose"]);
    }

    [Fact]
    public async Task DeleteLibrary_TransactionDisposeFailsAfterCommit_ReturnsDurableSuccessAndWarns()
    {
        var order = new List<string>();
        var repository = Substitute.For<ILibraryRepository>();
        var outbox = Substitute.For<IAdminEventOutbox>();
        var logger = new RecordingLogger<DeleteLibraryCommandHandler>();
        var library = NewLibrary(7, "Disposable");
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(library);
        repository.DeleteAsync(7, Arg.Any<CancellationToken>()).Returns(true);
        var unitOfWork = new RecordingUnitOfWork(
            order,
            new RecordingTransaction(order, disposeException: new InvalidOperationException("dispose")));
        var handler = new DeleteLibraryCommandHandler(repository, outbox, unitOfWork, logger);

        var result = await handler.HandleAsync(new DeleteLibraryCommand(7));

        result.IsError.ShouldBeFalse();
        unitOfWork.Transaction.CommitCount.ShouldBe(1);
        var warning = logger.Entries.Single(entry => entry.Level == LogLevel.Warning);
        warning.Exception.ShouldBeOfType<InvalidOperationException>();
        warning.Message.ShouldContain("transaction cleanup failed");
    }

    [Fact]
    public async Task ForceMaterialize_NewRequest_CommitsLibraryJobAndEventBeforeEnqueue()
    {
        var order = new List<string>();
        var repository = Substitute.For<ILibraryRepository>();
        var jobs = Substitute.For<IMaterializationJobRepository>();
        var enqueuer = Substitute.For<IMaterializationJobEnqueuer>();
        var outbox = Substitute.For<IAdminEventOutbox>();
        var currentUser = Substitute.For<ICurrentUser>();
        var creatorId = Guid.NewGuid();
        var library = NewLibrary(7, "Arcade");
        var unitOfWork = new RecordingUnitOfWork(order, new RecordingTransaction(order));
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(library);
        jobs.GetActiveForLibraryAsync(7, Arg.Any<CancellationToken>())
            .Returns((MaterializationJob?)null);
        currentUser.UserId.Returns(creatorId);
        repository.FlagForRematerializationAsync(7, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                order.Add("library");
                return library;
            });
        jobs.AddStagedAsync(Arg.Any<MaterializationJob>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var job = call.Arg<MaterializationJob>();
                job.LibraryId.ShouldBe(7);
                job.CreatedByUserId.ShouldBe(creatorId);
                order.Add("job");
                return Task.CompletedTask;
            });
        outbox.EnqueueAsync(
                AdminRealtimeEventTypes.LibraryUpdated,
                Arg.Any<AdminRealtimeLibraryUpdatedPayload>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Record(order, "outbox"));
        enqueuer.EnqueueAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => Record(order, "enqueue"));
        var handler = new ForceMaterializeLibraryCommandHandler(
            repository,
            jobs,
            enqueuer,
            outbox,
            currentUser,
            unitOfWork,
            NullLogger<ForceMaterializeLibraryCommandHandler>.Instance);

        var result = await handler.HandleAsync(new ForceMaterializeLibraryCommand(7));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldNotBe(Guid.Empty);
        library.NeedsMaterialization.ShouldBeTrue();
        order.ShouldBe(["begin", "library", "job", "outbox", "commit", "dispose", "enqueue"]);
        await enqueuer.Received(1).EnqueueAsync(result.Value, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForceMaterialize_ActiveDispatchedJob_ReturnsSameIdWithoutMutationOrEnqueue()
    {
        var order = new List<string>();
        var repository = Substitute.For<ILibraryRepository>();
        var jobs = Substitute.For<IMaterializationJobRepository>();
        var enqueuer = Substitute.For<IMaterializationJobEnqueuer>();
        var outbox = Substitute.For<IAdminEventOutbox>();
        var activeJob = MaterializationJob.Create(7, "Arcade");
        activeJob.SetHangfireJobId("hangfire-dispatched");
        var library = NewLibrary(7, "Arcade");
        library.MarkMaterialized(12);
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(library);
        jobs.GetActiveForLibraryAsync(7, Arg.Any<CancellationToken>()).Returns(activeJob);
        var unitOfWork = new RecordingUnitOfWork(order, new RecordingTransaction(order));
        var handler = new ForceMaterializeLibraryCommandHandler(
            repository,
            jobs,
            enqueuer,
            outbox,
            Substitute.For<ICurrentUser>(),
            unitOfWork,
            NullLogger<ForceMaterializeLibraryCommandHandler>.Instance);

        var result = await handler.HandleAsync(new ForceMaterializeLibraryCommand(7));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(activeJob.Id);
        library.NeedsMaterialization.ShouldBeFalse();
        order.ShouldBe(["begin", "dispose"]);
        await repository.DidNotReceiveWithAnyArgs().FlagForRematerializationAsync(default, default);
        await jobs.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
        await outbox.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default);
        await enqueuer.DidNotReceiveWithAnyArgs().EnqueueAsync(default, default);
    }

    [Fact]
    public async Task ForceMaterialize_ActivePendingUndispatchedJob_ReturnsSameIdAndAttemptsEnqueue()
    {
        var order = new List<string>();
        var repository = Substitute.For<ILibraryRepository>();
        var jobs = Substitute.For<IMaterializationJobRepository>();
        var enqueuer = Substitute.For<IMaterializationJobEnqueuer>();
        var outbox = Substitute.For<IAdminEventOutbox>();
        var activeJob = MaterializationJob.Create(7, "Arcade");
        var library = NewLibrary(7, "Arcade");
        library.MarkMaterialized(12);
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(library);
        jobs.GetActiveForLibraryAsync(7, Arg.Any<CancellationToken>()).Returns(activeJob);
        enqueuer.EnqueueAsync(activeJob.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Record(order, "enqueue"));
        var unitOfWork = new RecordingUnitOfWork(order, new RecordingTransaction(order));
        var handler = new ForceMaterializeLibraryCommandHandler(
            repository,
            jobs,
            enqueuer,
            outbox,
            Substitute.For<ICurrentUser>(),
            unitOfWork,
            NullLogger<ForceMaterializeLibraryCommandHandler>.Instance);

        var result = await handler.HandleAsync(new ForceMaterializeLibraryCommand(7));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(activeJob.Id);
        library.NeedsMaterialization.ShouldBeFalse();
        order.ShouldBe(["begin", "dispose", "enqueue"]);
        await repository.DidNotReceiveWithAnyArgs().FlagForRematerializationAsync(default, default);
        await jobs.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
        await outbox.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default);
        await enqueuer.Received(1).EnqueueAsync(activeJob.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForceMaterialize_MissingLibrary_ReturnsNotFoundWithoutJobLookup()
    {
        var repository = Substitute.For<ILibraryRepository>();
        var jobs = Substitute.For<IMaterializationJobRepository>();
        var handler = new ForceMaterializeLibraryCommandHandler(
            repository,
            jobs,
            Substitute.For<IMaterializationJobEnqueuer>(),
            Substitute.For<IAdminEventOutbox>(),
            Substitute.For<ICurrentUser>(),
            new RecordingUnitOfWork([], new RecordingTransaction([])),
            NullLogger<ForceMaterializeLibraryCommandHandler>.Instance);

        var result = await handler.HandleAsync(new ForceMaterializeLibraryCommand(404));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Libraries.NotFound");
        await jobs.DidNotReceiveWithAnyArgs().GetActiveForLibraryAsync(default, default);
    }

    [Fact]
    public async Task ForceMaterialize_PersistenceException_ReturnsOpaqueFailure()
    {
        var repository = Substitute.For<ILibraryRepository>();
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns<Task<Library?>>(_ => throw new InvalidOperationException("provider detail"));
        var handler = new ForceMaterializeLibraryCommandHandler(
            repository,
            Substitute.For<IMaterializationJobRepository>(),
            Substitute.For<IMaterializationJobEnqueuer>(),
            Substitute.For<IAdminEventOutbox>(),
            Substitute.For<ICurrentUser>(),
            new RecordingUnitOfWork([], new RecordingTransaction([])),
            NullLogger<ForceMaterializeLibraryCommandHandler>.Instance);

        var result = await handler.HandleAsync(new ForceMaterializeLibraryCommand(7));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Libraries.PersistenceFailed");
        result.FirstError.Description.ShouldNotContain("provider detail");
    }

    [Fact]
    public async Task ForceMaterialize_CommitConflict_ConvergesOnActiveJobWithoutEnqueue()
    {
        var repository = Substitute.For<ILibraryRepository>();
        var jobs = Substitute.For<IMaterializationJobRepository>();
        var enqueuer = Substitute.For<IMaterializationJobEnqueuer>();
        var logger = new RecordingLogger<ForceMaterializeLibraryCommandHandler>();
        var winnerJob = MaterializationJob.Create(7, "Arcade");
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(NewLibrary(7, "Arcade"));
        repository.FlagForRematerializationAsync(7, Arg.Any<CancellationToken>()).Returns(NewLibrary(7, "Arcade"));
        jobs.GetActiveForLibraryAsync(7, Arg.Any<CancellationToken>())
            .Returns((MaterializationJob?)null, winnerJob);
        var unitOfWork = new RecordingUnitOfWork(
            [],
            new RecordingTransaction([], commitException: new InvalidOperationException("unique violation")));
        var handler = new ForceMaterializeLibraryCommandHandler(
            repository,
            jobs,
            enqueuer,
            Substitute.For<IAdminEventOutbox>(),
            Substitute.For<ICurrentUser>(),
            unitOfWork,
            logger);

        var result = await handler.HandleAsync(new ForceMaterializeLibraryCommand(7));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(winnerJob.Id);
        await enqueuer.DidNotReceiveWithAnyArgs().EnqueueAsync(default, default);
        logger.Entries.ShouldContain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("converged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ForceMaterialize_PreCommitCancellation_PropagatesWithoutEnqueue()
    {
        var repository = Substitute.For<ILibraryRepository>();
        var jobs = Substitute.For<IMaterializationJobRepository>();
        var enqueuer = Substitute.For<IMaterializationJobEnqueuer>();
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(NewLibrary(7, "Arcade"));
        repository.FlagForRematerializationAsync(7, Arg.Any<CancellationToken>()).Returns(NewLibrary(7, "Arcade"));
        jobs.AddStagedAsync(Arg.Any<MaterializationJob>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new OperationCanceledException("pre-commit"));
        var handler = new ForceMaterializeLibraryCommandHandler(
            repository,
            jobs,
            enqueuer,
            Substitute.For<IAdminEventOutbox>(),
            Substitute.For<ICurrentUser>(),
            new RecordingUnitOfWork([], new RecordingTransaction([])),
            NullLogger<ForceMaterializeLibraryCommandHandler>.Instance);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            handler.HandleAsync(new ForceMaterializeLibraryCommand(7)));

        await enqueuer.DidNotReceiveWithAnyArgs().EnqueueAsync(default, default);
    }

    [Fact]
    public async Task ForceMaterialize_PostCommitDisposeFailure_ReturnsAcceptedAndEnqueues()
    {
        var order = new List<string>();
        var repository = Substitute.For<ILibraryRepository>();
        var jobs = Substitute.For<IMaterializationJobRepository>();
        var enqueuer = Substitute.For<IMaterializationJobEnqueuer>();
        var logger = new RecordingLogger<ForceMaterializeLibraryCommandHandler>();
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(NewLibrary(7, "Arcade"));
        repository.FlagForRematerializationAsync(7, Arg.Any<CancellationToken>()).Returns(NewLibrary(7, "Arcade"));
        var unitOfWork = new RecordingUnitOfWork(
            order,
            new RecordingTransaction(order, disposeException: new InvalidOperationException("dispose")));
        var handler = new ForceMaterializeLibraryCommandHandler(
            repository,
            jobs,
            enqueuer,
            Substitute.For<IAdminEventOutbox>(),
            Substitute.For<ICurrentUser>(),
            unitOfWork,
            logger);

        var result = await handler.HandleAsync(new ForceMaterializeLibraryCommand(7));

        result.IsError.ShouldBeFalse();
        await enqueuer.Received(1).EnqueueAsync(result.Value, Arg.Any<CancellationToken>());
        logger.Entries.ShouldContain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("transaction cleanup failed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForceMaterialize_PostCommitEnqueueFailure_ReturnsAcceptedAndWarns(bool cancellation)
    {
        var repository = Substitute.For<ILibraryRepository>();
        var enqueuer = Substitute.For<IMaterializationJobEnqueuer>();
        var logger = new RecordingLogger<ForceMaterializeLibraryCommandHandler>();
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(NewLibrary(7, "Arcade"));
        repository.FlagForRematerializationAsync(7, Arg.Any<CancellationToken>()).Returns(NewLibrary(7, "Arcade"));
        enqueuer.EnqueueAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => cancellation
                ? throw new OperationCanceledException("post-commit")
                : throw new InvalidOperationException("enqueue"));
        var handler = new ForceMaterializeLibraryCommandHandler(
            repository,
            Substitute.For<IMaterializationJobRepository>(),
            enqueuer,
            Substitute.For<IAdminEventOutbox>(),
            Substitute.For<ICurrentUser>(),
            new RecordingUnitOfWork([], new RecordingTransaction([])),
            logger);

        var result = await handler.HandleAsync(new ForceMaterializeLibraryCommand(7));

        result.IsError.ShouldBeFalse();
        logger.Entries.ShouldContain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("worker dispatcher will recover", StringComparison.Ordinal));
    }

    private static Task Record(ICollection<string> order, string value)
    {
        order.Add(value);
        return Task.CompletedTask;
    }

    private static bool RecordTrue(ICollection<string> order, string value)
    {
        order.Add(value);
        return true;
    }

    private static Library NewLibrary(int id, string name)
    {
        var method = typeof(Library).GetMethod(
            "Rehydrate",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull();
        return method.Invoke(null,
        [
            id,
            name,
            new LibraryConfiguration(),
            LibraryConfigurationState.Valid,
            null,
            false,
            true,
            null,
            0,
            DateTimeOffset.UtcNow,
            null
        ]).ShouldBeOfType<Library>();
    }

    private sealed class RecordingUnitOfWork(
        List<string> order,
        RecordingTransaction transaction) : IUnitOfWork
    {
        public RecordingTransaction Transaction => transaction;

        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            order.Add("flush");
            return Task.CompletedTask;
        }

        public Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            order.Add("begin");
            return Task.FromResult<ITransaction>(transaction);
        }
    }

    private sealed class RecordingTransaction(
        List<string> order,
        Exception? commitException = null,
        Exception? disposeException = null) : ITransaction
    {
        public int CommitCount { get; private set; }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            order.Add("commit");
            CommitCount++;
            return commitException is null
                ? Task.CompletedTask
                : Task.FromException(commitException);
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            order.Add("dispose");
            return disposeException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(disposeException);
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
