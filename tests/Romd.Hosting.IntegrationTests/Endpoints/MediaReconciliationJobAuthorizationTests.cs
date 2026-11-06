using Romd.Admin.Application.Ingestion.Jobs.Commands.CancelJob;
using Romd.Infrastructure.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Commands.SetJobArchived;
using System.Reflection;
using Romd.Admin.Application.Ingestion.Jobs.Queries.GetJob;
using Romd.Admin.Application.Ingestion.Jobs.Queries.ListJobs;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Models;
using ErrorOr;
using Hangfire;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Commands.RetryFailedItems;
using Romd.Admin.Application.Ingestion.Jobs.Queries.ExportJobItems;
using Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobItems;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Source.Rom;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Host.Endpoints;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class MediaReconciliationJobAuthorizationTests
{
    [Theory]
    [InlineData(false, RomdRoleType.User)]
    [InlineData(false, RomdRoleType.Contributor)]
    [InlineData(false, RomdRoleType.Manager)]
    [InlineData(true, RomdRoleType.User)]
    [InlineData(true, RomdRoleType.Contributor)]
    [InlineData(true, RomdRoleType.Manager)]
    public async Task GetAuthorizedJob_MediaOwnedByNonAdmin_ReturnsNull(bool bulk, RomdRoleType role)
    {
        Guid actorId = Guid.NewGuid();
        Job job = CreateMediaJob(bulk, actorId);
        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var result = await AuthorizeAsync(job.Id, repository, User(actorId, role));

        result.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetAuthorizedJob_MediaCreatedByAnotherActor_AdminCanInspect(bool bulk)
    {
        Job job = CreateMediaJob(bulk, Guid.NewGuid());
        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var result = await AuthorizeAsync(job.Id, repository, User(Guid.NewGuid(), RomdRoleType.Admin));

        result.ShouldBeSameAs(job);
    }

    [Fact]
    public async Task GetAuthorizedJob_OrdinaryJob_ManagerCanStillInspectOtherActors()
    {
        var job = UploadJob.Create("other.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: Guid.NewGuid());
        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var result = await AuthorizeAsync(job.Id, repository, User(Guid.NewGuid(), RomdRoleType.Manager));

        result.ShouldBeSameAs(job);
    }

    [Theory]
    [InlineData("Get", false)]
    [InlineData("Get", true)]
    [InlineData("Items", false)]
    [InlineData("Items", true)]
    [InlineData("ExportItems", false)]
    [InlineData("ExportItems", true)]
    [InlineData("Update", false)]
    [InlineData("Update", true)]
    [InlineData("Cancel", false)]
    [InlineData("Cancel", true)]
    [InlineData("Delete", false)]
    [InlineData("Delete", true)]
    [InlineData("RetryFailed", false)]
    [InlineData("RetryFailed", true)]
    public async Task Endpoint_ManagerCannotInspectOrMutateMediaJob(string endpoint, bool bulk)
    {
        Guid actorId = Guid.NewGuid();
        Job job = CreateMediaJob(bulk, actorId);
        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        var store = Substitute.For<IMediaReconciliationRequests>();

        var result = await InvokeAsync(endpoint, job.Id, repository, User(actorId, RomdRoleType.Manager), store);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        job.IsTerminal.ShouldBeFalse();
        job.IsArchived.ShouldBeFalse();
        await repository.DidNotReceiveWithAnyArgs().UpdateStagedAsync(default!, default);
        await store.DidNotReceiveWithAnyArgs().StageCancellationAsync(default, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_AdminMedia_RequestsDurableCancellationWithoutTerminalizing(bool bulk)
    {
        Guid actorId = Guid.NewGuid();
        Job job = CreateMediaJob(bulk, actorId);
        job.SetHangfireJobId("media-delivery");
        var repository = Substitute.For<IJobRepository>();
        using var requestCancellation = new CancellationTokenSource();
        repository.GetByIdAsync(job.Id, requestCancellation.Token).Returns(_ =>
        {
            requestCancellation.Cancel();
            return job;
        });
        var store = Substitute.For<IMediaReconciliationRequests>();
        store.StageCancellationAsync(job.Id, CancellationToken.None).Returns((ErrorOr<Success>)Result.Success);
        var transport = Substitute.For<IBackgroundJobClient>();

        var result = await InvokeAsync("Cancel", job.Id, repository, User(actorId, RomdRoleType.Admin),
            store, transport, ct: requestCancellation.Token);

        result.ShouldBeOfType<NoContent>();
        job.IsTerminal.ShouldBeFalse();
        await repository.DidNotReceiveWithAnyArgs().UpdateStagedAsync(default!, default);
        transport.DidNotReceiveWithAnyArgs().ChangeState(default!, default!, default);
        await store.Received(1).StageCancellationAsync(job.Id, CancellationToken.None);
    }

    [Fact]
    public async Task Cancel_AdminMediaWhenSnapshotChanged_ReturnsConflictWithoutTerminalizing()
    {
        Guid actorId = Guid.NewGuid();
        Job job = CreateMediaJob(false, actorId);
        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        var store = Substitute.For<IMediaReconciliationRequests>();
        store.StageCancellationAsync(job.Id, CancellationToken.None).Returns((ErrorOr<Success>)Error.Conflict("MediaReconciliation.AlreadyComplete", "The job settled."));

        var result = await InvokeAsync("Cancel", job.Id, repository, User(actorId, RomdRoleType.Admin), store);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>().Value.ShouldNotBeNull()
            .Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe("MediaReconciliation.AlreadyComplete");
        job.IsTerminal.ShouldBeFalse();
        await repository.DidNotReceiveWithAnyArgs().UpdateStagedAsync(default!, default);
    }

    [Theory]
    [InlineData(RomdRoleType.Manager, false)]
    [InlineData(RomdRoleType.Admin, true)]
    public async Task ClearCompleted_ArchivesOnlyAuthorizedJobFamilies(RomdRoleType role, bool includeMedia)
    {
        var repository = Substitute.For<IJobRepository>();
        repository.ArchiveCompletedVisibleAsync(Arg.Is<JobVisibilityScope>(scope => scope.IncludeAdminOnly == includeMedia && !scope.Empty), Arg.Any<CancellationToken>()).Returns(3);

        var result = await InvokeAsync("ClearCompleted", Guid.Empty, repository, User(Guid.NewGuid(), role),
            Substitute.For<IMediaReconciliationRequests>());

        result.ShouldBeOfType<Ok<Romd.Contracts.Management.Models.ClearCompleted>>().Value.ShouldNotBeNull()
            .ArchivedCount.ShouldBe(3);
        await repository.Received(1).ArchiveCompletedVisibleAsync(Arg.Is<JobVisibilityScope>(scope => scope.IncludeAdminOnly == includeMedia && !scope.Empty), Arg.Any<CancellationToken>());
        await repository.DidNotReceiveWithAnyArgs().ArchiveCompletedAsync(default);
    }

    [Theory]
    [InlineData(RomdRoleType.User, false, true)]
    [InlineData(RomdRoleType.Manager, false, false)]
    [InlineData(RomdRoleType.Admin, true, false)]
    public async Task GetRecent_PassesFamilyVisibilityBeforeRepositoryPagination(RomdRoleType role, bool includeMedia,
        bool ownJobsOnly)
    {
        Guid actorId = Guid.NewGuid();
        Guid? ownerId = ownJobsOnly ? actorId : null;
        var ordinary = UploadJob.Create("visible.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: actorId);
        var repository = Substitute.For<IJobRepository>();
        repository.ListAsync(Arg.Is<JobListFilter>(filter => filter.OwnerId == ownerId && filter.IncludeMediaConvergence == includeMedia && filter.Limit == 50), Arg.Any<CancellationToken>())
            .Returns([ordinary]);

        var result = await InvokeAsync("List", Guid.Empty, repository, User(actorId, role),
            Substitute.For<IMediaReconciliationRequests>());

        result.ShouldBeOfType<Ok<Page<Romd.Contracts.Management.Models.JobDto>>>().Value.ShouldNotBeNull()
            .Items.Select(job => job.Id).ShouldBe([ordinary.Id]);
        await repository.Received(1).ListAsync(Arg.Is<JobListFilter>(filter => filter.OwnerId == ownerId && filter.IncludeMediaConvergence == includeMedia && filter.Limit == 50), Arg.Any<CancellationToken>());
        await repository.DidNotReceiveWithAnyArgs().GetRecentAsync();

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryFailed_AdminMedia_RequiresExactRequestIdentityWithoutMutatingHistory(bool selection)
    {
        Guid actorId = Guid.NewGuid();
        var original = CreateMediaJob(selection, actorId);
        original.ApplyOutcome(MediaReconciliationOutcomeKind.Failed, "fixture-failure", original.CreatedAt.AddSeconds(1));
        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdAsync(original.Id, Arg.Any<CancellationToken>()).Returns(original);
        var requests = Substitute.For<IMediaReconciliationRequests>();
        var result = await InvokeAsync("RetryFailed", original.Id, repository, User(actorId, RomdRoleType.Admin), requests);
        result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>().Value.ShouldNotBeNull()
            .Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe("MediaReconciliation.ExactRetryRequired");
        requests.ReceivedCalls().ShouldBeEmpty();
        await repository.DidNotReceiveWithAnyArgs().UpdateStagedAsync(default!, default);
        original.Outcome.ShouldBe(MediaReconciliationOutcomeKind.Failed);
    }

    private static MediaReconciliationJob CreateMediaJob(bool selection, Guid actorId) => MediaReconciliationJob.Create(Guid.NewGuid(), selection
        ? new MediaReconciliationRequest.SelectionWork(new MediaReconciliationFilter.AllSubjects(), new(42, 7, 8, 0), 10, false)
        : new MediaReconciliationRequest.SubjectWork(new MediaReconciliationSubject.LogicalRom(1)), "media", null, actorId, DateTimeOffset.UtcNow);

    private static ICurrentUser User(Guid actorId, RomdRoleType role)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(actorId);
        user.HasRole(Arg.Any<RomdRoleType>()).Returns(call => role >= call.Arg<RomdRoleType>());
        return user;
    }

    private static Task<Job?> AuthorizeAsync(Guid jobId, IJobRepository repository, ICurrentUser user,
        CancellationToken ct = default) => repository.GetVisibleAsync(jobId, user, ct);

    private static async Task<IResult> InvokeAsync(string endpoint, Guid jobId, IJobRepository repository,
        ICurrentUser user, IMediaReconciliationRequests store,
        IBackgroundJobClient? transport = null, HttpContext? httpContext = null, CancellationToken ct = default)
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Substitute.For<ITransaction>());
        var clock = Substitute.For<TimeProvider>();
        clock.GetUtcNow().Returns(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var dependencies = new Dictionary<Type, object>
        {
            [typeof(IReferenceCatalogService)] = TestReferenceCatalog.Create(),
            [typeof(IJobRepository)] = repository,
            [typeof(ICurrentUser)] = user,
            [typeof(IUnitOfWork)] = unitOfWork,
            [typeof(IMediaReconciliationRequests)] = store,
            [typeof(IBackgroundJobClient)] = transport ?? Substitute.For<IBackgroundJobClient>(),
            [typeof(TimeProvider)] = clock,
            [typeof(ILoggerFactory)] = NullLoggerFactory.Instance,
            [typeof(HttpContext)] = httpContext ?? new DefaultHttpContext(),
            [typeof(IQueryHandler<GetJobItemsQuery, Page<JobItemDto>>)] =
                new GetJobItemsQueryHandler(Substitute.For<IJobItemRepository>(), Substitute.For<IReadSnapshotTransactionFactory>(), repository, user, TestReferenceCatalog.Create()),
            [typeof(IQueryHandler<ExportJobItemsQuery, JobItemExport>)] =
                Substitute.For<IQueryHandler<ExportJobItemsQuery, JobItemExport>>(),
            [typeof(ICommandHandler<RetryFailedItemsCommand, Romd.Contracts.Management.Models.RetryFailedResult>)] =
                Substitute.For<ICommandHandler<RetryFailedItemsCommand, Romd.Contracts.Management.Models.RetryFailedResult>>(),
            [typeof(ICommandHandler<CancelJobCommand, Success>)] =
                new CancelJobCommandHandler(repository, Substitute.For<IJobCancellationStore>(), store, unitOfWork, user, clock,
                    new HangfireJobDeliveryCancellation(transport ?? Substitute.For<IBackgroundJobClient>()), NullLogger<CancelJobCommandHandler>.Instance),
            [typeof(ICommandHandler<SetJobArchivedCommand, JobDto>)] =
                new SetJobArchivedCommandHandler(repository, Substitute.For<IUnitOfWork>(), user, TestReferenceCatalog.Create(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SetJobArchivedCommandHandler>.Instance),
            [typeof(UpdateJobRequest)] = new UpdateJobRequest(true),
            [typeof(IQueryHandler<GetJobQuery, JobDto>)] = new GetJobQueryHandler(repository, user, TestReferenceCatalog.Create()),
            [typeof(IQueryHandler<ListJobsQuery, Page<JobDto>>)] = new ListJobsQueryHandler(TestReferenceCatalog.Create(), repository, user)
        };
        var method = typeof(JobEndpoints).GetMethod(endpoint, BindingFlags.Static | BindingFlags.NonPublic).ShouldNotBeNull();
        object?[] arguments = method.GetParameters().Select(parameter => parameter.ParameterType == typeof(Guid) ? jobId
            : parameter.Name == "format" ? "json"
            : parameter.ParameterType == typeof(CancellationToken) ? (object)ct
            : dependencies.TryGetValue(parameter.ParameterType, out var dependency) ? dependency
            : parameter.HasDefaultValue ? parameter.DefaultValue : null).ToArray();
        return await method.Invoke(null, arguments).ShouldBeOfType<Task<IResult>>();
    }
}
