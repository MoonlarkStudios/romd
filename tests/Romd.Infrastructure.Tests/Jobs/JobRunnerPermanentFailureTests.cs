using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class JobRunnerPermanentFailureTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_PermanentResumableFailure_CheckpointsTerminalOnlyWithOwnership(bool ownsExecution)
    {
        await using var fixture = new Fixture();
        fixture.Repository.HasExecutionOwnershipAsync(fixture.Job.Id, fixture.Fence, Arg.Any<CancellationToken>())
            .Returns(ownsExecution);
        fixture.Executor.ExecuteAsync(fixture.Job, Arg.Any<JobContext>())
            .Returns(Task.FromException(new JobPermanentFailureException("Invalid artwork.")));

        await fixture.Runner.RunAsync(fixture.Job.Id, "delivery", CancellationToken.None);

        fixture.Job.IsTerminal.ShouldBe(ownsExecution);
        if (ownsExecution)
        {
            fixture.Job.PhaseEnum.ShouldBe(ArtworkImportPhase.Failed);
            fixture.Job.Errors.Single().Item.ShouldBe("job");
            await fixture.Repository.Received(1).TryUpdateClaimedAsync(fixture.Job, fixture.Fence, Arg.Any<CancellationToken>());
        }
        else
        {
            fixture.Job.Errors.ShouldBeEmpty();
            await fixture.Repository.DidNotReceive().TryUpdateClaimedAsync(fixture.Job, fixture.Fence, Arg.Any<CancellationToken>());
        }
        await fixture.Repository.DidNotReceive().UpdateAsync(fixture.Job, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ShutdownConcurrentWithPermanentFailure_PreservesClaimWithoutAttemptError()
    {
        await using var fixture = new Fixture();
        using var shutdown = new CancellationTokenSource();
        fixture.Executor.ExecuteAsync(fixture.Job, Arg.Any<JobContext>()).Returns(_ =>
        {
            shutdown.Cancel();
            return Task.FromException(new JobPermanentFailureException("Invalid artwork."));
        });

        await Should.ThrowAsync<OperationCanceledException>(() =>
            fixture.Runner.RunAsync(fixture.Job.Id, "delivery", shutdown.Token));

        fixture.Job.IsTerminal.ShouldBeFalse();
        fixture.Job.Errors.ShouldBeEmpty();
        await fixture.Repository.DidNotReceive().TryUpdateClaimedAsync(fixture.Job, fixture.Fence, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_PermanentFailureCheckpointFails_PropagatesPersistenceFailure()
    {
        await using var fixture = new Fixture();
        fixture.Executor.ExecuteAsync(fixture.Job, Arg.Any<JobContext>())
            .Returns(Task.FromException(new JobPermanentFailureException("Invalid artwork.")));
        fixture.Repository.TryUpdateClaimedAsync(fixture.Job, fixture.Fence, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new IOException("Checkpoint unavailable")));

        await Should.ThrowAsync<IOException>(() => fixture.Runner.RunAsync(fixture.Job.Id, "delivery", CancellationToken.None));

        fixture.Job.Errors.ShouldNotContain(error => error.Item == "attempt");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        public ArtworkImportJob Job { get; } = ArtworkImportJob.Create(Guid.NewGuid(), "Game", 1, 1,
            Romd.Domain.Catalog.ArtworkRole.Poster, 1, "steamgriddb", "2", "3",
            "https://cdn2.steamgriddb.com/grid/a.png", null, TimeProvider.System);
        public Guid Fence { get; } = Guid.NewGuid();
        public IJobExecutionClaimRepository<ArtworkImportJob> Repository { get; } = Substitute.For<IJobExecutionClaimRepository<ArtworkImportJob>>();
        public IResumableJobExecutor<ArtworkImportJob> Executor { get; } = Substitute.For<IResumableJobExecutor<ArtworkImportJob>>();
        public JobRunner<ArtworkImportJob> Runner { get; }

        public Fixture()
        {
            Job.Start("delivery");
            Repository.TryClaimExecutionAsync(Job.Id, "delivery", Arg.Any<CancellationToken>())
                .Returns(new JobExecutionClaim<ArtworkImportJob>(Job, Fence));
            Repository.HasExecutionOwnershipAsync(Job.Id, Fence, Arg.Any<CancellationToken>()).Returns(true);
            Repository.TryUpdateClaimedAsync(Job, Fence, Arg.Any<CancellationToken>()).Returns(true);
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(Path.GetTempPath());
            var services = new ServiceCollection();
            services.AddSingleton<IJobRepository<ArtworkImportJob>>(Repository);
            services.AddScoped<JobAuditContext>();
            services.AddScoped<IAuditContext>(provider => provider.GetRequiredService<JobAuditContext>());
            _provider = services.BuildServiceProvider();
            Runner = new JobRunner<ArtworkImportJob>(_provider.GetRequiredService<IServiceScopeFactory>(), Executor,
                Substitute.For<IJobNotifier>(), options, NullLogger<JobRunner<ArtworkImportJob>>.Instance);
        }

        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }
}
