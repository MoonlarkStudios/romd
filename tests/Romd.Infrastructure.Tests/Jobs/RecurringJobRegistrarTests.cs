using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Configuration;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Realtime;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class RecurringJobRegistrarTests
{
    [Fact]
    public async Task StartAsync_Called_RegistersRecurringJobsFromSingleRegistrar()
    {
        var recurringJobs = Substitute.For<IRecurringJobManager>();
        var registrations = new List<RecurringJobRegistration>();

        recurringJobs
            .When(manager => manager.AddOrUpdate(
                Arg.Any<string>(),
                Arg.Any<Job>(),
                Arg.Any<string>(),
                Arg.Any<RecurringJobOptions>()))
            .Do(callInfo => registrations.Add(new RecurringJobRegistration(
                callInfo.ArgAt<string>(0),
                callInfo.ArgAt<Job>(1),
                callInfo.ArgAt<string>(2))));

        var registrar = new RecurringJobRegistrar(
            recurringJobs,
            Substitute.For<IServerImportOptions>(),
            NullLogger<RecurringJobRegistrar>.Instance);

        await registrar.StartAsync(CancellationToken.None);

        registrations.Count.ShouldBe(9);
        recurringJobs.Received(1).RemoveIfExists("server-import-inbox");
        registrations.ShouldContain(job => job.Id == "dat-subscription-checks"
            && job.TargetType == typeof(DatSubscriptionCheckJob)
            && job.MethodName == nameof(DatSubscriptionCheckJob.ExecuteAsync)
            && job.CronExpression == "*/15 * * * *");
        registrations.ShouldContain(job => job.Id == "orphaned-file-cleanup"
            && job.TargetType == typeof(OrphanedFileCleanupJob)
            && job.MethodName == nameof(OrphanedFileCleanupJob.ExecuteAsync)
            && job.CronExpression == Cron.Daily());
        registrations.ShouldContain(job => job.Id == "orphaned-job-cleanup"
            && job.TargetType == typeof(OrphanedJobCleanupJob)
            && job.MethodName == nameof(OrphanedJobCleanupJob.ExecuteAsync)
            && job.CronExpression == "*/30 * * * *");
        registrations.ShouldContain(job => job.Id == "dat-replacement-convergence-sweep"
            && job.TargetType == typeof(DatReplacementConvergenceSweepJob)
            && job.MethodName == nameof(DatReplacementConvergenceSweepJob.ExecuteAsync)
            && job.CronExpression == "*/30 * * * *");
        registrations.ShouldContain(job => job.Id == "title-payload-availability-sweep"
            && job.TargetType == typeof(TitlePayloadAvailabilitySweepJob)
            && job.MethodName == nameof(TitlePayloadAvailabilitySweepJob.ExecuteAsync)
            && job.Job.Args.Count == 1
            && job.CronExpression == "*/30 * * * *");
        registrations.ShouldContain(job => job.Id == "purge-archived-jobs"
            && job.TargetType == typeof(IJobRepository)
            && job.MethodName == nameof(IJobRepository.PurgeArchivedAsync)
            && job.CronExpression == Cron.Daily(3));
        registrations.ShouldContain(job => job.Id == "export-artifact-cleanup"
            && job.TargetType == typeof(ExportArtifactCleanupJob)
            && job.MethodName == nameof(ExportArtifactCleanupJob.ExecuteAsync)
            && job.CronExpression == Cron.Daily(4));
        registrations.ShouldContain(job => job.Id == "admin-realtime-outbox-cleanup"
            && job.TargetType == typeof(AdminRealtimeOutboxCleanupJob)
            && job.MethodName == nameof(AdminRealtimeOutboxCleanupJob.ExecuteAsync)
            && job.CronExpression == Cron.Daily(5));
        registrations.ShouldContain(job => job.Id == "openiddict-prune"
            && job.TargetType == typeof(RomdOpenIddictPruneJob)
            && job.MethodName == nameof(RomdOpenIddictPruneJob.ExecuteAsync)
            && job.CronExpression == Cron.Daily(6));
    }

    [Fact]
    public async Task StartAsync_FolderScheduleConfigured_RegistersTheSharedImportCommandOnUtcCadence()
    {
        var jobs = Substitute.For<IRecurringJobManager>();
        var options = Substitute.For<IServerImportOptions>();
        options.ServerImportDirectory.Returns("/incoming");
        options.ServerImportIntervalMinutes.Returns(15);
        var registrar = new RecurringJobRegistrar(jobs, options, NullLogger<RecurringJobRegistrar>.Instance);

        await registrar.StartAsync(CancellationToken.None);

        jobs.Received(1).AddOrUpdate("server-import-inbox",
            Arg.Is<Job>(job => job.Type == typeof(ServerImportCheckJob) && job.Method.Name == nameof(ServerImportCheckJob.ExecuteAsync)),
            "*/15 * * * *", Arg.Is<RecurringJobOptions>(option => option.TimeZone == TimeZoneInfo.Utc));
        jobs.DidNotReceive().RemoveIfExists("server-import-inbox");
    }

    private sealed record RecurringJobRegistration(string Id, Job Job, string CronExpression)
    {
        public Type TargetType => Job.Type;

        public string MethodName => Job.Method.Name;
    }
}
