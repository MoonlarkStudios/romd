using System.Reflection;
using System.Text.Json;
using ErrorOr;
using Hangfire;
using Hangfire.States;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Application.Common.Security;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Common.Models;
using Romd.Admin.Application.Ingestion.Jobs;
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
        var ownedJob = UploadJob.Create("owned.zip", createdByUserId: userId);

        jobRepository.GetRecentForUserAsync(userId, 25, true, Arg.Any<CancellationToken>())
            .Returns([ownedJob]);

        var result = await InvokeGetRecentAsync(25, true, jobRepository, currentUser);

        var ok = result.ShouldBeOfType<Ok<List<JobDto>>>();
        ok.Value.ShouldNotBeNull();
        ok.Value.Select(j => j.Id).ShouldBe([ownedJob.Id]);
        await jobRepository.Received(1).GetRecentForUserAsync(userId, 25, true, Arg.Any<CancellationToken>());
        await jobRepository.DidNotReceiveWithAnyArgs().GetRecentAsync();
    }

    [Fact]
    public async Task GetRecent_Manager_ReturnsOperationalJobList()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var jobRepository = Substitute.For<IJobRepository>();
        var currentUser = CreateCurrentUser(userId, RomdRoleType.Manager);
        var otherUserJob = UploadJob.Create("other.zip", createdByUserId: otherUserId);

        jobRepository.GetRecentAsync(50, false, Arg.Any<CancellationToken>())
            .Returns([otherUserJob]);

        var result = await InvokeGetRecentAsync(null, null, jobRepository, currentUser);

        var ok = result.ShouldBeOfType<Ok<List<JobDto>>>();
        ok.Value.ShouldNotBeNull();
        ok.Value.Select(j => j.Id).ShouldBe([otherUserJob.Id]);
        await jobRepository.Received(1).GetRecentAsync(50, false, Arg.Any<CancellationToken>());
        await jobRepository.DidNotReceiveWithAnyArgs().GetRecentForUserAsync(default);
    }

    [Fact]
    public async Task GetById_UserOwnedJob_ReturnsJob()
    {
        var userId = Guid.NewGuid();
        var jobRepository = Substitute.For<IJobRepository>();
        var currentUser = CreateCurrentUser(userId);
        var job = UploadJob.Create("owned.zip", createdByUserId: userId);

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
        var job = UploadJob.Create("other.zip", createdByUserId: Guid.NewGuid());

        jobRepository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(job);

        var result = await InvokeGetByIdAsync(job.Id, jobRepository, currentUser);

        result.ShouldBeOfType<NotFound>();
    }

    [Fact]
    public async Task Archive_UserOwnedTerminalJob_ArchivesJob()
    {
        var userId = Guid.NewGuid();
        var jobRepository = Substitute.For<IJobRepository>();
        var currentUser = CreateCurrentUser(userId);
        var job = UploadJob.Create("owned.zip", createdByUserId: userId);
        job.Fail("complete enough for archive");

        jobRepository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(job);

        var result = await InvokeArchiveAsync(job.Id, jobRepository, currentUser);

        result.ShouldBeOfType<NoContent>();
        job.IsArchived.ShouldBeTrue();
        await jobRepository.Received(1).UpdateAsync(job, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Archive_UserOtherUserJob_ReturnsNotFound()
    {
        var userId = Guid.NewGuid();
        var jobRepository = Substitute.For<IJobRepository>();
        var currentUser = CreateCurrentUser(userId);
        var job = UploadJob.Create("other.zip", createdByUserId: Guid.NewGuid());
        job.Fail("complete enough for archive");

        jobRepository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(job);

        var result = await InvokeArchiveAsync(job.Id, jobRepository, currentUser);

        result.ShouldBeOfType<NotFound>();
        job.IsArchived.ShouldBeFalse();
        await jobRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Fact]
    public async Task Cancel_UserOwnedExportJob_CancelsJob()
    {
        var userId = Guid.NewGuid();
        var jobRepository = Substitute.For<IJobRepository>();
        var currentUser = CreateCurrentUser(userId);
        var backgroundJobs = Substitute.For<IBackgroundJobClient>();
        var job = ExportJob.CreateLibrary(7, 1, userId);
        job.SetHangfireJobId("hf-1");

        jobRepository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(job);

        var result = await InvokeCancelAsync(job.Id, jobRepository, currentUser, backgroundJobs);

        result.ShouldBeOfType<NoContent>();
        job.PhaseEnum.ShouldBe(ExportPhase.Cancelled);
        backgroundJobs.Received(1).ChangeState("hf-1", Arg.Any<DeletedState>(), null);
        await jobRepository.Received(1).UpdateAsync(job, CancellationToken.None);
        Received.InOrder(() =>
        {
            jobRepository.UpdateAsync(job, CancellationToken.None);
            backgroundJobs.ChangeState("hf-1", Arg.Any<DeletedState>(), null);
        });
    }

    [Fact]
    public async Task Cancel_HangfireDeleteFails_RemainsDurablyCancelled()
    {
        var userId = Guid.NewGuid();
        var jobRepository = Substitute.For<IJobRepository>();
        var currentUser = CreateCurrentUser(userId);
        var backgroundJobs = Substitute.For<IBackgroundJobClient>();
        var job = ExportJob.CreateLibrary(7, 1, userId);
        job.SetHangfireJobId("hf-1");
        jobRepository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        backgroundJobs.ChangeState("hf-1", Arg.Any<DeletedState>(), null)
            .Returns(_ => throw new InvalidOperationException("transport unavailable"));

        var result = await InvokeCancelAsync(job.Id, jobRepository, currentUser, backgroundJobs);

        result.ShouldBeOfType<NoContent>();
        job.PhaseEnum.ShouldBe(ExportPhase.Cancelled);
        await jobRepository.Received(1).UpdateAsync(job, CancellationToken.None);
    }

    [Fact]
    public async Task Cancel_RequestCancelledAfterRead_CommitsWithIndependentTokenBeforeDelete()
    {
        var userId = Guid.NewGuid();
        var jobRepository = Substitute.For<IJobRepository>();
        var currentUser = CreateCurrentUser(userId);
        var backgroundJobs = Substitute.For<IBackgroundJobClient>();
        var job = ExportJob.CreateLibrary(7, 1, userId);
        job.SetHangfireJobId("hf-1");
        using var requestCancellation = new CancellationTokenSource();
        jobRepository.GetByIdAsync(job.Id, requestCancellation.Token).Returns(_ =>
        {
            requestCancellation.Cancel();
            return job;
        });

        var result = await InvokeCancelAsync(
            job.Id,
            jobRepository,
            currentUser,
            backgroundJobs,
            requestCancellation.Token);

        result.ShouldBeOfType<NoContent>();
        await jobRepository.Received(1).UpdateAsync(job, CancellationToken.None);
        backgroundJobs.Received(1).ChangeState("hf-1", Arg.Any<DeletedState>(), null);
    }

    [Fact]
    public async Task Cancel_DurableSaveFails_DoesNotDeleteHangfireDelivery()
    {
        var userId = Guid.NewGuid();
        var jobRepository = Substitute.For<IJobRepository>();
        var currentUser = CreateCurrentUser(userId);
        var backgroundJobs = Substitute.For<IBackgroundJobClient>();
        var job = ExportJob.CreateLibrary(7, 1, userId);
        job.SetHangfireJobId("hf-1");
        jobRepository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        jobRepository.UpdateAsync(job, CancellationToken.None)
            .Returns(_ => Task.FromException(new InvalidOperationException("database unavailable")));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            InvokeCancelAsync(job.Id, jobRepository, currentUser, backgroundJobs));

        backgroundJobs.DidNotReceiveWithAnyArgs().ChangeState(default!, default!, default);
    }

    [Fact]
    public async Task Cancel_UserOtherUserJob_ReturnsNotFound()
    {
        var userId = Guid.NewGuid();
        var jobRepository = Substitute.For<IJobRepository>();
        var currentUser = CreateCurrentUser(userId);
        var backgroundJobs = Substitute.For<IBackgroundJobClient>();
        var job = ExportJob.CreateLibrary(7, 1, Guid.NewGuid());
        job.SetHangfireJobId("hf-1");

        jobRepository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(job);

        var result = await InvokeCancelAsync(job.Id, jobRepository, currentUser, backgroundJobs);

        result.ShouldBeOfType<NotFound>();
        job.PhaseEnum.ShouldBe(ExportPhase.Pending);
        backgroundJobs.DidNotReceiveWithAnyArgs().ChangeState(default!, default!, default);
        await jobRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Fact]
    public async Task GetItems_UserOwnedJob_ReturnsMappedProvenance()
    {
        var userId = Guid.NewGuid();
        var jobRepository = Substitute.For<IJobRepository>();
        var handler = Substitute.For<IQueryHandler<GetJobItemsQuery, JobItemPageResult>>();
        var currentUser = CreateCurrentUser(userId);
        var job = UploadJob.Create("owned.zip", createdByUserId: userId);

        jobRepository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

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
        handler.HandleAsync(
                Arg.Is<GetJobItemsQuery>(query =>
                    query.JobId == job.Id && query.Outcome == null && query.Limit == 100),
                Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From(new JobItemPageResult([view], false)));

        var result = await InvokeGetItemsAsync(job.Id, null, null, jobRepository, handler, currentUser);

        var ok = result.ShouldBeOfType<Ok<JobItemPage>>();
        ok.Value.ShouldNotBeNull();
        ok.Value.HasMore.ShouldBeFalse();
        var item = ok.Value.Items.ShouldHaveSingleItem();
        item.FileName.ShouldBe("nhl94.bin");
        item.Outcome.ShouldBe("ingested");
        item.PlatformName.ShouldBe("Genesis");
        item.MatchedTitles.ShouldHaveSingleItem().Name.ShouldBe("NHL 94 (USA)");
        item.ArchiveOnly.ShouldBe(true);
    }

    [Fact]
    public async Task ExportItems_Json_WritesUnsafeInt64SizeAsCanonicalString()
    {
        var userId = Guid.NewGuid();
        var jobRepository = Substitute.For<IJobRepository>();
        var handler = Substitute.For<
            IQueryHandler<ExportJobItemsQuery, IReadOnlyList<JobItemView>>>();
        var currentUser = CreateCurrentUser(userId);
        var job = UploadJob.Create("owned.zip", createdByUserId: userId);
        jobRepository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        handler.HandleAsync(
                Arg.Is<ExportJobItemsQuery>(query => query.JobId == job.Id),
                Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From<IReadOnlyList<JobItemView>>(
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
            ]));

        var result = await InvokeExportItemsAsync(
            job.Id, "json", jobRepository, handler, currentUser);

        var file = result.ShouldBeOfType<FileContentHttpResult>();
        using var document = JsonDocument.Parse(file.FileContents);
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

        string csv = GetEndpointMethod("BuildCsv").Invoke(null, [items])
            .ShouldBeOfType<string>();
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
        var handler = Substitute.For<IQueryHandler<GetJobItemsQuery, JobItemPageResult>>();
        var currentUser = CreateCurrentUser(userId);
        var job = UploadJob.Create("other.zip", createdByUserId: Guid.NewGuid());

        jobRepository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var result = await InvokeGetItemsAsync(job.Id, null, null, jobRepository, handler, currentUser);

        result.ShouldBeOfType<NotFound>();
        await handler.DidNotReceiveWithAnyArgs().HandleAsync(default!, default);
    }

    private static async Task<IResult> InvokeGetItemsAsync(
        Guid id,
        string? outcome,
        int? limit,
        IJobRepository jobRepository,
        IQueryHandler<GetJobItemsQuery, JobItemPageResult> handler,
        ICurrentUser currentUser)
    {
        var method = GetEndpointMethod("GetItems");

        var task = InvokeEndpoint(method.Name,
        [
            id,
            outcome,
            limit,
            null,
            null,
            jobRepository,
            handler,
            currentUser,
            CancellationToken.None
        ]);

        return await task.ShouldBeOfType<Task<IResult>>();
    }

    private static async Task<IResult> InvokeExportItemsAsync(
        Guid id,
        string? format,
        IJobRepository jobRepository,
        IQueryHandler<ExportJobItemsQuery, IReadOnlyList<JobItemView>> handler,
        ICurrentUser currentUser)
    {
        var task = InvokeEndpoint("ExportItems",
        [
            id,
            format,
            jobRepository,
            handler,
            currentUser,
            CancellationToken.None
        ]);

        return await task.ShouldBeOfType<Task<IResult>>();
    }

    private static ICurrentUser CreateCurrentUser(Guid userId, RomdRoleType role = RomdRoleType.User)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(userId);
        currentUser.HasRole(Arg.Any<RomdRoleType>())
            .Returns(call => role >= call.Arg<RomdRoleType>());

        return currentUser;
    }

    private static async Task<IResult> InvokeGetRecentAsync(
        int? limit,
        bool? includeArchived,
        IJobRepository jobRepository,
        ICurrentUser currentUser)
    {
        var method = GetEndpointMethod("GetRecent");

        var task = InvokeEndpoint(method.Name,
        [
            limit,
            includeArchived,
            jobRepository,
            currentUser,
            CancellationToken.None
        ]);

        return await task.ShouldBeOfType<Task<IResult>>();
    }

    private static async Task<IResult> InvokeGetByIdAsync(
        Guid id,
        IJobRepository jobRepository,
        ICurrentUser currentUser)
    {
        var method = GetEndpointMethod("GetById");

        var task = InvokeEndpoint(method.Name,
        [
            id,
            jobRepository,
            currentUser,
            CancellationToken.None
        ]);

        return await task.ShouldBeOfType<Task<IResult>>();
    }

    private static async Task<IResult> InvokeArchiveAsync(
        Guid id,
        IJobRepository jobRepository,
        ICurrentUser currentUser)
    {
        var method = GetEndpointMethod("Archive");

        var task = InvokeEndpoint(method.Name,
        [
            id,
            jobRepository,
            Substitute.For<IUnitOfWork>(),
            currentUser,
            CancellationToken.None
        ]);

        return await task.ShouldBeOfType<Task<IResult>>();
    }

    private static async Task<IResult> InvokeCancelAsync(
        Guid id,
        IJobRepository jobRepository,
        ICurrentUser currentUser,
        IBackgroundJobClient backgroundJobs,
        CancellationToken ct = default)
    {
        var method = GetEndpointMethod("Cancel");

        var task = InvokeEndpoint(method.Name,
        [
            id,
            jobRepository,
            Substitute.For<IUnitOfWork>(),
            currentUser,
            backgroundJobs,
            NullLoggerFactory.Instance,
            ct
        ]);

        return await task.ShouldBeOfType<Task<IResult>>();
    }

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
