using System.Reflection;
using System.Text.Json;
using ErrorOr;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Romd.Application.Common.Security;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Systems;
using Romd.Admin.Application.Ingestion.Jobs.Queries.ListJobs;
using Romd.Admin.Application.Ingestion.Jobs.Queries.GetJob;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Common.Models;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Commands.SetJobArchived;
using Romd.Admin.Application.Ingestion.Jobs.Commands.CancelJob;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs.Queries.ExportJobItems;
using Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobItems;
using Romd.Contracts.Management.Models;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Host.Endpoints;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class JobEndpointsTests
{
    [Fact]
    public async Task GetRecent_User_ReturnsOnlyOwnedJobs()
    {
        var userId = Guid.NewGuid();
        var jobRepository = Substitute.For<IJobRepository>();
        var currentUser = CreateCurrentUser(userId);
        var ownedJob = UploadJob.Create("owned.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: userId);

        jobRepository.ListAsync(Arg.Is<JobListFilter>(filter => filter.OwnerId == userId && filter.Limit == 25 && filter.Archive == "all"), Arg.Any<CancellationToken>())
            .Returns([ownedJob]);

        var result = await InvokeGetRecentAsync(25, true, jobRepository, currentUser);

        var ok = result.ShouldBeOfType<Ok<Page<JobDto>>>();
        ok.Value.ShouldNotBeNull();
        ok.Value.Items.Select(j => j.Id).ShouldBe([ownedJob.Id]);
        await jobRepository.Received(1).ListAsync(Arg.Is<JobListFilter>(filter => filter.OwnerId == userId && filter.Limit == 25 && filter.Archive == "all"), Arg.Any<CancellationToken>());
        await jobRepository.DidNotReceiveWithAnyArgs().GetRecentAsync();
    }

    [Fact]
    public async Task GetRecent_Manager_ReturnsOperationalJobList()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var jobRepository = Substitute.For<IJobRepository>();
        var currentUser = CreateCurrentUser(userId, RomdRoleType.Manager);
        var otherUserJob = UploadJob.Create("other.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: otherUserId);

        jobRepository.ListAsync(Arg.Is<JobListFilter>(filter => filter.OwnerId == null && filter.Limit == 50 && filter.Archive == "active"), Arg.Any<CancellationToken>())
            .Returns([otherUserJob]);

        var result = await InvokeGetRecentAsync(null, null, jobRepository, currentUser);

        var ok = result.ShouldBeOfType<Ok<Page<JobDto>>>();
        ok.Value.ShouldNotBeNull();
        ok.Value.Items.Select(j => j.Id).ShouldBe([otherUserJob.Id]);
        await jobRepository.Received(1).ListAsync(Arg.Is<JobListFilter>(filter => filter.OwnerId == null && filter.Limit == 50 && filter.Archive == "active"), Arg.Any<CancellationToken>());

    }

    [Fact]
    public async Task GetById_UserOwnedJob_ReturnsJob()
    {
        var userId = Guid.NewGuid();
        var jobRepository = Substitute.For<IJobRepository>();
        var currentUser = CreateCurrentUser(userId);
        var job = UploadJob.Create("owned.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: userId);

        jobRepository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(job);

        var result = await InvokeGetByIdAsync(job.Id, jobRepository, currentUser);

        var ok = result.ShouldBeOfType<Ok<JobDto>>();
        ok.Value.ShouldNotBeNull();
        ok.Value.Id.ShouldBe(job.Id);
    }

    [Fact]
    public async Task GetById_UserOtherUserJob_ReturnsNotFound()
    {
        var userId = Guid.NewGuid();
        var jobRepository = Substitute.For<IJobRepository>();
        var currentUser = CreateCurrentUser(userId);
        var job = UploadJob.Create("other.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: Guid.NewGuid());

        jobRepository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(job);

        var result = await InvokeGetByIdAsync(job.Id, jobRepository, currentUser);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(404);
    }

    [Fact]
    public async Task Archive_HandlerSucceeds_ReturnsUpdatedResource()
    {
        var jobId = Guid.NewGuid();
        var handler = Substitute.For<ICommandHandler<SetJobArchivedCommand, JobDto>>();
        var expected = UploadJob.Create("terminal.zip", createdAt: DateTimeOffset.UtcNow).ToContract(await TestReferenceCatalog.Create().GetSystemKeysAsync(CancellationToken.None)) with { Id = jobId };
        handler.HandleAsync(new SetJobArchivedCommand(jobId, true), Arg.Any<CancellationToken>()).Returns(expected);

        var result = await InvokeArchiveAsync(jobId, handler);

        result.ShouldBeOfType<Ok<JobDto>>().Value!.Id.ShouldBe(jobId);
    }

    [Theory]
    [InlineData(404, "Jobs.NotFound")]
    [InlineData(409, "Jobs.NotTerminal")]
    public async Task Archive_HandlerError_ReturnsItsEnvelope(int status, string errorCode)
    {
        var jobId = Guid.NewGuid();
        var handler = Substitute.For<ICommandHandler<SetJobArchivedCommand, JobDto>>();
        handler.HandleAsync(new SetJobArchivedCommand(jobId, true), Arg.Any<CancellationToken>())
            .Returns(status == 404 ? JobErrors.NotFound(jobId) : JobErrors.NotTerminal());

        var result = await InvokeArchiveAsync(jobId, handler);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!.StatusCode.ShouldBe(status);
        result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>()!.Value!
            .Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe(errorCode);
    }

    [Fact]
    public async Task Cancel_HandlerSucceeds_ForwardsRequestTokenAndReturnsNoContent()
    {
        var jobId = Guid.NewGuid();
        var handler = Substitute.For<ICommandHandler<CancelJobCommand, Success>>();
        using var cancellation = new CancellationTokenSource();
        handler.HandleAsync(new(jobId), cancellation.Token).Returns((ErrorOr<Success>)Result.Success);

        var result = await InvokeCancelAsync(jobId, handler, cancellation.Token);

        result.ShouldBeOfType<NoContent>();
        await handler.Received(1).HandleAsync(new(jobId), cancellation.Token);
    }

    [Theory]
    [InlineData(404, "Jobs.NotFound")]
    [InlineData(409, "Jobs.NotCancellable")]
    [InlineData(409, "Jobs.AlreadyComplete")]
    public async Task Cancel_HandlerRejects_ReturnsItsErrorEnvelope(int status, string code)
    {
        var jobId = Guid.NewGuid();
        var handler = Substitute.For<ICommandHandler<CancelJobCommand, Success>>();
        handler.HandleAsync(new(jobId), Arg.Any<CancellationToken>()).Returns(code switch
        {
            "Jobs.NotFound" => JobErrors.NotFound(jobId),
            "Jobs.NotCancellable" => JobErrors.NotCancellable(),
            _ => JobErrors.AlreadyComplete()
        });

        var result = await InvokeCancelAsync(jobId, handler);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(status);
        result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>().Value.ShouldNotBeNull()
            .Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe(code);
    }

    [Fact]
    public async Task GetItems_UserOwnedJob_ReturnsMappedProvenance()
    {
        var userId = Guid.NewGuid();
        var jobRepository = Substitute.For<IJobRepository>();
        var itemRepository = Substitute.For<IJobItemRepository>();
        var snapshots = Substitute.For<IReadSnapshotTransactionFactory>();
        var snapshot = Substitute.For<IReadSnapshotTransaction>();
        var currentUser = CreateCurrentUser(userId);
        var job = UploadJob.Create("owned.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: userId);

        jobRepository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        snapshots.BeginAsync(Arg.Any<CancellationToken>()).Returns(snapshot);

        var view = new JobItemView
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            Kind = JobItemKind.Rom,
            FileName = "nhl94.bin",
            SizeBytes = 1024,
            Outcome = JobItemOutcome.Ingested,
            RomFileId = 10,
            PlatformId = 5,
            PlatformName = "Genesis",
            MatchedTitles = [new JobItemTitle(1, "NHL 94 (USA)")],
            ArchiveOnly = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        itemRepository.GetByJobAsync(job.Id, null, 100, Arg.Any<CancellationToken>(), null, null)
            .Returns(new JobItemPageResult([view], false));
        var handler = new GetJobItemsQueryHandler(itemRepository, snapshots, jobRepository, currentUser,
            TestReferenceCatalog.Create());

        var result = await InvokeGetItemsAsync(job.Id, null, null, handler);

        var ok = result.ShouldBeOfType<Ok<Page<JobItemDto>>>();
        ok.Value.ShouldNotBeNull();
        ok.Value.HasNextPage.ShouldBeFalse();
        var item = ok.Value.Items.ShouldHaveSingleItem();
        item.FileName.ShouldBe("nhl94.bin");
        item.Outcome.ShouldBe("ingested");
        item.PlatformName.ShouldBe("Genesis");
        item.MatchedTitles.ShouldHaveSingleItem().Name.ShouldBe("NHL 94 (USA)");
        item.ArchiveOnly.ShouldBe(true);
        await itemRepository.Received(1).GetByJobAsync(job.Id, null, 100, Arg.Any<CancellationToken>(), null, null);
        await snapshot.Received(1).CompleteAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExportItems_Json_WritesUnsafeInt64SizeAsCanonicalString()
    {
        var userId = Guid.NewGuid();
        var jobRepository = Substitute.For<IJobRepository>();
        var handler = Substitute.For<
            IQueryHandler<ExportJobItemsQuery, JobItemExport>>();
        var currentUser = CreateCurrentUser(userId);
        var job = UploadJob.Create("owned.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: userId);
        jobRepository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        handler.HandleAsync(
                Arg.Is<ExportJobItemsQuery>(query => query.JobId == job.Id),
                Arg.Any<CancellationToken>())
            .Returns(new JobItemExport(Substitute.For<IJobItemRepository>(), Substitute.For<IReadSnapshotTransaction>(), job.Id, new JobItemPageResult(
            [
                new JobItemView
                {
                    Id = Guid.NewGuid(),
                    JobId = job.Id,
                    Kind = JobItemKind.Rom,
                    FileName = "largest.rom",
                    SizeBytes = long.MaxValue,
                    Outcome = JobItemOutcome.Ingested,
                    MatchedTitles = [],
                    CreatedAt = DateTimeOffset.UtcNow
                }
            ], false)));

        var result = await InvokeExportItemsAsync(
            job.Id, "json", jobRepository, handler, currentUser);

        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        await result.ShouldBeOfType<JobItemExportResult>().ExecuteAsync(context);
        using var document = JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
        JsonElement size = document.RootElement[0].GetProperty("sizeBytes");
        size.ValueKind.ShouldBe(JsonValueKind.String);
        size.GetString().ShouldBe(long.MaxValue.ToString());
    }

    [Fact]
    public void BuildCsv_IncludesArchiveOnlyChoiceAndHistoricalNull()
    {
        var items = new[]
        {
            CsvItem("collection.rom", archiveOnly: false),
            CsvItem("archive.rom", archiveOnly: true),
            CsvItem("catalog.dat", archiveOnly: null, kind: "dat")
        };

        string csv = JobItemExportResult.CsvHeader + "\n" + string.Join("\n", items.Select(JobItemExportResult.CsvRow));
        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines[0].ShouldBe(
            "filename,kind,outcome,sizeBytes,platform,matchedTitles,gameCount,archiveOnly,error");
        lines[1].ShouldBe("collection.rom,rom,ingested,1,,,,false,");
        lines[2].ShouldBe("archive.rom,rom,ingested,1,,,,true,");
        lines[3].ShouldBe("catalog.dat,dat,dat_routed,1,,,,,");
    }

    [Fact]
    public async Task GetItems_OtherUserJob_ReturnsNotFound()
    {
        var userId = Guid.NewGuid();
        var jobRepository = Substitute.For<IJobRepository>();
        var itemRepository = Substitute.For<IJobItemRepository>();
        var snapshots = Substitute.For<IReadSnapshotTransactionFactory>();
        var snapshot = Substitute.For<IReadSnapshotTransaction>();
        var catalog = TestReferenceCatalog.Create();
        var currentUser = CreateCurrentUser(userId);
        var job = UploadJob.Create("other.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: Guid.NewGuid());

        jobRepository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        itemRepository.GetByJobAsync(job.Id, null, 100, Arg.Any<CancellationToken>(), null, null)
            .Returns(new JobItemPageResult([], false));
        snapshots.BeginAsync(Arg.Any<CancellationToken>()).Returns(snapshot);
        itemRepository.ClearReceivedCalls();
        snapshots.ClearReceivedCalls();
        catalog.ClearReceivedCalls();
        var handler = new GetJobItemsQueryHandler(itemRepository, snapshots, jobRepository, currentUser, catalog);

        var result = await InvokeGetItemsAsync(job.Id, null, null, handler);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(404);
        result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>().Value.ShouldNotBeNull()
            .Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe("Jobs.NotFound");
        await itemRepository.DidNotReceiveWithAnyArgs().GetByJobAsync(Guid.Empty, null, 0, CancellationToken.None, null, null);
        await snapshots.DidNotReceiveWithAnyArgs().BeginAsync(CancellationToken.None);
        await catalog.DidNotReceiveWithAnyArgs().GetSystemKeysAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportItems_ReferenceKeysAndSnapshotDisposalFail_PreservesOriginalPreResponseFailure(bool cancel)
    {
        var owner = Guid.NewGuid();
        var job = UploadJob.Create("owned.zip", DateTimeOffset.UtcNow, createdByUserId: owner);
        var jobs = Substitute.For<IJobRepository>();
        jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        var snapshot = Substitute.For<IReadSnapshotTransaction>();
        var cleanup = new IOException("Secondary export snapshot disposal failure");
        snapshot.DisposeAsync().Returns(_ => ValueTask.FromException(cleanup));
        var export = new JobItemExport(Substitute.For<IJobItemRepository>(), snapshot, job.Id, new([], false));
        var handler = Substitute.For<IQueryHandler<ExportJobItemsQuery, JobItemExport>>();
        handler.HandleAsync(Arg.Any<ExportJobItemsQuery>(), Arg.Any<CancellationToken>()).Returns(export);
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancel ? new OperationCanceledException("Original export key cancellation", cancellation.Token)
            : new IOException("Original export key failure");
        var catalog = Substitute.For<IReferenceCatalogService>();
        catalog.GetSystemKeysAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<SystemKeys>(primary));

        var observed = await Record.ExceptionAsync(() => (Task<IResult>)InvokeEndpoint("ExportItems",
            [job.Id, "json", jobs, CreateCurrentUser(owner), catalog, handler, cancellation.Token])!);

        observed.ShouldBeSameAs(primary);
        if (cancel)
            ((OperationCanceledException)observed!).CancellationToken.ShouldBe(cancellation.Token);
        primary.Data["Romd.ReadSnapshot.CleanupFailure"].ShouldBeSameAs(cleanup);
        await snapshot.DidNotReceive().CompleteAsync(Arg.Any<CancellationToken>());
        await snapshot.Received(1).DisposeAsync();
    }

    private static async Task<IResult> InvokeGetItemsAsync(Guid id, string? outcome, int? limit,
        IQueryHandler<GetJobItemsQuery, Page<JobItemDto>> handler) =>
        await (Task<IResult>)InvokeEndpoint("Items", [id, handler, CancellationToken.None, outcome, null, null, limit ?? 100])!;

    private static async Task<IResult> InvokeExportItemsAsync(Guid id, string? format, IJobRepository jobRepository,
        IQueryHandler<ExportJobItemsQuery, JobItemExport> handler, ICurrentUser currentUser) =>
        await (Task<IResult>)InvokeEndpoint("ExportItems", [id, format, jobRepository, currentUser, TestReferenceCatalog.Create(), handler, CancellationToken.None])!;

    private static ICurrentUser CreateCurrentUser(Guid userId, RomdRoleType role = RomdRoleType.User)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(userId);
        currentUser.HasRole(Arg.Any<RomdRoleType>())
            .Returns(call => role >= call.Arg<RomdRoleType>());

        return currentUser;
    }

    private static async Task<IResult> InvokeGetRecentAsync(int? limit, bool? includeArchived, IJobRepository repository, ICurrentUser user) =>
        await (Task<IResult>)InvokeEndpoint("List", [new ListJobsQueryHandler(TestReferenceCatalog.Create(), repository, user), CancellationToken.None,
            null, null, null, includeArchived == true ? "all" : "active", null, null, null, null, limit ?? 50])!;

    private static async Task<IResult> InvokeGetByIdAsync(Guid id, IJobRepository repository, ICurrentUser user) =>
        await (Task<IResult>)InvokeEndpoint("Get", [id, new GetJobQueryHandler(repository, user, TestReferenceCatalog.Create()), CancellationToken.None])!;

    private static async Task<IResult> InvokeArchiveAsync(Guid id, ICommandHandler<SetJobArchivedCommand, JobDto> handler) =>
        await (Task<IResult>)InvokeEndpoint("Update", [id, new UpdateJobRequest(true), handler, CancellationToken.None])!;

    private static async Task<IResult> InvokeCancelAsync(Guid id,
        ICommandHandler<CancelJobCommand, Success> handler, CancellationToken ct = default) =>
        await (Task<IResult>)InvokeEndpoint("Cancel", [id, handler, ct])!;

    private static object? InvokeEndpoint(string name, object?[] args)
    {
        var method = GetEndpointMethod(name);
        if (method.GetParameters().FirstOrDefault()?.ParameterType == typeof(Romd.Application.Common.ReferenceCatalog.IReferenceCatalogService))
            args = [Romd.Hosting.IntegrationTests.Infrastructure.TestReferenceCatalog.Create(), .. args];
        return method.Invoke(null, args);
    }

    private static MethodInfo GetEndpointMethod(string name)
    {
        var method = typeof(JobEndpoints).GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Static);

        method.ShouldNotBeNull();
        return method;
    }

    private static JobItemDto CsvItem(string fileName, bool? archiveOnly, string kind = "rom") => new()
    {
        Id = Guid.NewGuid().ToString(),
        Kind = kind,
        FileName = fileName,
        SizeBytes = (ByteCount)1,
        Outcome = kind == "dat" ? "dat_routed" : "ingested",
        MatchedTitles = [],
        ArchiveOnly = archiveOnly,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
