using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.ActivateDatVersion;
using Romd.Admin.Application.Source.Dat.Commands.IngestDat;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Dat;
using Romd.Infrastructure.Jobs.Executors;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

/// <summary>
///     Phase-dispatch proofs for the resumable replace saga executor: redelivery at every
///     phase resumes from the persisted phase machine without invalid transitions.
/// </summary>
public sealed class ReplaceDatJobExecutorTests : IDisposable
{
    private const int ExistingDatId = 7;
    private const int NewDatId = 42;
    private const int SourceId = 5;
    private const int PlatformId = 10;

    private readonly IDatRepository _datRepository = Substitute.For<IDatRepository>();
    private readonly ICommandHandler<IngestDatCommand, DatIngestResult> _ingestHandler =
        Substitute.For<ICommandHandler<IngestDatCommand, DatIngestResult>>();

    private readonly ICommandHandler<ActivateDatVersionCommand, DatFile> _activateHandler =
        Substitute.For<ICommandHandler<ActivateDatVersionCommand, DatFile>>();

    private readonly string _workspacePath =
        Path.Combine(Path.GetTempPath(), $"romd-replace-executor-{Guid.NewGuid():N}");

    public ReplaceDatJobExecutorTests()
    {
        Directory.CreateDirectory(_workspacePath);
        File.WriteAllText(Path.Combine(_workspacePath, "replacement.dat"), "<datafile/>");

        _datRepository.GetByIdAsync(ExistingDatId, Arg.Any<CancellationToken>())
            .Returns(ExistingVersion());
        _ingestHandler.HandleAsync(Arg.Any<IngestDatCommand>(), Arg.Any<CancellationToken>())
            .Returns(new DatIngestResult { DatFile = PendingVersion(), Warnings = [] });
        _activateHandler.HandleAsync(Arg.Any<ActivateDatVersionCommand>(), Arg.Any<CancellationToken>())
            .Returns(ActiveVersion());
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
        {
            Directory.Delete(_workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_FromIngesting_IngestsAsPendingOnExistingSourceThenActivates()
    {
        var job = NewStartedJob();
        var executor = NewExecutor();

        await executor.ExecuteAsync(job, NewContext(_workspacePath));

        var ingestCommand = (IngestDatCommand)_ingestHandler.ReceivedCalls().Single().GetArguments()[0]!;
        ingestCommand.ReplacesDatSourceId.ShouldBe(SourceId);
        ingestCommand.ExpectedActiveDatId.ShouldBe(ExistingDatId);
        ingestCommand.PlatformId.ShouldBe(PlatformId);
        job.NewDatId.ShouldBe(NewDatId);
        job.PhaseEnum.ShouldBe(ReplaceDatPhase.Replacing);
        var activateCommand = (ActivateDatVersionCommand)_activateHandler.ReceivedCalls().Single().GetArguments()[0]!;
        activateCommand.NewDatId.ShouldBe(NewDatId);
        activateCommand.ExpectedActiveDatId.ShouldBe(ExistingDatId);
        job.HasErrors.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_RedeliveredInReplacing_SkipsIngestAndResumesActivationWithoutWorkspace()
    {
        var job = NewStartedJob();
        job.SetNewDatId(NewDatId);
        job.BeginReplacing();
        var executor = NewExecutor();

        // A redelivered Replacing-phase job needs no workspace: NewDatId is persisted.
        await executor.ExecuteAsync(job, NewContext(workspacePath: null));

        await _ingestHandler.DidNotReceiveWithAnyArgs().HandleAsync(default!, default);
        var activateCommand = (ActivateDatVersionCommand)_activateHandler.ReceivedCalls().Single().GetArguments()[0]!;
        activateCommand.NewDatId.ShouldBe(NewDatId);
        job.PhaseEnum.ShouldBe(ReplaceDatPhase.Replacing);
        job.HasErrors.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_RedeliveredInIngesting_ReingestsThroughTheIdempotentHandler()
    {
        // A crash after the pending-ingest commit but before the checkpoint leaves the job
        // Ingesting with no NewDatId; the ingest handler converges on the committed pending
        // version (matched on FileId + source), so redelivery just re-dispatches ingest.
        var job = NewStartedJob();
        job.NewDatId.ShouldBeNull();
        var executor = NewExecutor();

        await executor.ExecuteAsync(job, NewContext(_workspacePath));

        await _ingestHandler.ReceivedWithAnyArgs(1).HandleAsync(default!, default);
        job.NewDatId.ShouldBe(NewDatId);
        job.PhaseEnum.ShouldBe(ReplaceDatPhase.Replacing);
    }

    [Fact]
    public async Task ExecuteAsync_CrossPlatformReplacementOfRoutedSource_ThrowsBeforeIngesting()
    {
        var job = NewStartedJob(platformId: PlatformId + 1);
        var executor = NewExecutor();

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(job, NewContext(_workspacePath)));

        exception.Message.ShouldContain("Cross-platform replacement");
        await _ingestHandler.DidNotReceiveWithAnyArgs().HandleAsync(default!, default);
        await _activateHandler.DidNotReceiveWithAnyArgs().HandleAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_ReplacementRoutesUnroutedSource_IngestsWithTheRequestedPlatform()
    {
        _datRepository.GetByIdAsync(ExistingDatId, Arg.Any<CancellationToken>())
            .Returns(ExistingUnroutedVersion());
        var job = NewStartedJob(platformId: PlatformId);
        var executor = NewExecutor();

        await executor.ExecuteAsync(job, NewContext(_workspacePath));

        var ingestCommand = (IngestDatCommand)_ingestHandler.ReceivedCalls().Single().GetArguments()[0]!;
        ingestCommand.PlatformId.ShouldBe(PlatformId);
        job.PhaseEnum.ShouldBe(ReplaceDatPhase.Replacing);
        job.HasErrors.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ActivationErrors_ThrowsAndPreservesTheResumablePhase()
    {
        _activateHandler.HandleAsync(Arg.Any<ActivateDatVersionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Error.Failure("Catalog.DatabaseFailed", "activation failed"));
        var job = NewStartedJob();
        var executor = NewExecutor();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(job, NewContext(_workspacePath)));

        job.PhaseEnum.ShouldBe(ReplaceDatPhase.Replacing);
        job.IsTerminal.ShouldBeFalse();
    }

    private ReplaceDatJobExecutor NewExecutor()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_datRepository);
        services.AddSingleton(_ingestHandler);
        services.AddSingleton(_activateHandler);
        var provider = services.BuildServiceProvider();

        return new ReplaceDatJobExecutor(provider, NullLogger<ReplaceDatJobExecutor>.Instance);
    }

    private static ReplaceDatJob NewStartedJob(int? platformId = null)
    {
        var job = ReplaceDatJob.Create(ExistingDatId, "replacement.dat", platformId);
        job.Start("hangfire-1");
        return job;
    }

    private static JobContext NewContext(string? workspacePath) =>
        new(workspacePath, _ => Task.CompletedTask, CancellationToken.None);

    private static DatFile ExistingVersion() => DatFile.Rehydrate(
        ExistingDatId, "Existing DAT", "Existing DAT", null, null, null, DatType.NoIntro,
        PlatformId, "existing.dat", 17, DateTimeOffset.UtcNow, null, 1, 1, 0,
        SourceId, DatFileLifecycle.Active, null);

    private static DatFile ExistingUnroutedVersion() => DatFile.Rehydrate(
        ExistingDatId, "Existing DAT", "Existing DAT", null, null, null, DatType.NoIntro,
        null, "existing.dat", 17, DateTimeOffset.UtcNow, null, 1, 1, 0,
        SourceId, DatFileLifecycle.Active, null);

    private static DatFile PendingVersion() => DatFile.Rehydrate(
        NewDatId, "Replacement DAT", "Replacement DAT", null, null, null, DatType.NoIntro,
        PlatformId, "replacement.dat", 51, DateTimeOffset.UtcNow, null, 1, 1, 0,
        SourceId, DatFileLifecycle.PendingActivation, null);

    private static DatFile ActiveVersion() => DatFile.Rehydrate(
        NewDatId, "Replacement DAT", "Replacement DAT", null, null, null, DatType.NoIntro,
        PlatformId, "replacement.dat", 51, DateTimeOffset.UtcNow, null, 1, 1, 0,
        SourceId, DatFileLifecycle.Active, null);
}
