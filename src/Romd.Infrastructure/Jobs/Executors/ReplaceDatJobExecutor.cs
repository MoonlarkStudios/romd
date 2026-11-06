using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.ActivateDatVersion;
using Romd.Admin.Application.Source.Dat.Commands.IngestDat;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Dat;

namespace Romd.Infrastructure.Jobs.Executors;

/// <summary>
///     Resumable three-phase replace saga on the persisted <see cref="ReplaceDatJob" />
///     phase machine. Phase 1 ingests the replacement as a PendingActivation version of the
///     existing source (idempotent: a redelivery that finds its committed pending version
///     skips re-ingest). Phase 2 is the single atomic activate/supersede commit owned by
///     <see cref="ActivateDatVersionCommandHandler" />, including N=1 superseded retention.
///     The recurring convergence sweep is the crash/race backstop.
/// </summary>
public sealed class ReplaceDatJobExecutor : IResumableJobExecutor<ReplaceDatJob>
{
    private readonly ILogger<ReplaceDatJobExecutor> _logger;
    private readonly IServiceProvider _serviceProvider;

    public ReplaceDatJobExecutor(
        IServiceProvider serviceProvider,
        ILogger<ReplaceDatJobExecutor> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task ExecuteAsync(ReplaceDatJob job, JobContext context)
    {
        var ct = context.CancellationToken;
        using var scope = _serviceProvider.CreateScope();

        if (job.PhaseEnum == ReplaceDatPhase.Ingesting)
        {
            await IngestReplacementAsync(job, context, scope.ServiceProvider, ct);
        }

        if (job.PhaseEnum != ReplaceDatPhase.Replacing)
        {
            throw new InvalidOperationException(
                $"Replace DAT job cannot execute from phase {job.Phase}");
        }

        int newDatId = job.NewDatId
                       ?? throw new InvalidOperationException(
                           "Replacing phase requires a persisted new DAT ID");

        job.SetCurrentItem($"Activating replacement DAT (ID: {newDatId})");
        await context.CheckpointAsync(ct);

        var activateHandler = scope.ServiceProvider
            .GetRequiredService<ICommandHandler<ActivateDatVersionCommand, DatFile>>();

        var activateCommandResult = ActivateDatVersionCommand.Create(newDatId, job.ExistingDatId);
        if (activateCommandResult.IsError)
        {
            throw new InvalidOperationException(activateCommandResult.FirstError.Description);
        }

        var activation = await activateHandler.HandleAsync(activateCommandResult.Value, ct);
        if (activation.IsError)
        {
            throw new InvalidOperationException(activation.FirstError.Description);
        }

        _logger.LogInformation(
            "Replace DAT job {Id}: activated DAT {NewId}, superseding the previous version of source {SourceId}",
            job.Id, newDatId, activation.Value.DatSourceId);

    }

    private async Task IngestReplacementAsync(
        ReplaceDatJob job,
        JobContext context,
        IServiceProvider services,
        CancellationToken ct)
    {
        string workDirectory = context.WorkspacePath
                               ?? throw new InvalidOperationException(
                                   "Replace DAT ingestion requires a workspace");

        var datRepository = services.GetRequiredService<IDatRepository>();
        var existing = await datRepository.GetByIdAsync(job.ExistingDatId, ct)
                       ?? throw new InvalidOperationException(
                           $"DAT with ID {job.ExistingDatId} no longer exists");

        int? effectivePlatformId = job.PlatformId ?? existing.PlatformId;

        // Source entries are source-scoped and keep their platform and title links across
        // replacement. Re-platforming a routed source would strand shared entries (and their
        // curated links) on the old platform, so replacement never changes an established
        // platform; routing an unrouted source via replacement remains allowed.
        if (existing.PlatformId is int establishedPlatformId
            && effectivePlatformId is int requestedPlatformId
            && requestedPlatformId != establishedPlatformId)
        {
            throw new InvalidOperationException(
                $"Cross-platform replacement is not supported: source is routed to platform " +
                $"{establishedPlatformId} but the replacement requested platform {requestedPlatformId}. " +
                "Create a new source instead.");
        }

        string? datFile = Directory.GetFiles(workDirectory).FirstOrDefault()
                          ?? throw new InvalidOperationException("No DAT file found in work directory");

        job.SetCurrentItem(Path.GetFileName(datFile));
        await context.CheckpointAsync(ct);

        var ingestHandler = services
            .GetRequiredService<ICommandHandler<IngestDatCommand, DatIngestResult>>();

        await using var stream = File.OpenRead(datFile);
        var commandResult = IngestDatCommand.Create(
            stream,
            Path.GetFileName(datFile),
            effectivePlatformId,
            existing.DatSourceId,
            expectedActiveDatId: job.ExistingDatId);

        if (commandResult.IsError)
        {
            throw new InvalidOperationException(commandResult.FirstError.Description);
        }

        var ingestResult = await ingestHandler.HandleAsync(commandResult.Value, ct);
        if (ingestResult.IsError)
        {
            throw new InvalidOperationException(ingestResult.FirstError.Description);
        }

        job.SetNewDatId(ingestResult.Value.DatFile.Id);
        job.BeginReplacing();
        await context.CheckpointAsync(ct);

        _logger.LogInformation(
            "Replace DAT job {Id}: ingested pending DAT '{Name}' with {Games} games (ID: {NewDatId})",
            job.Id, ingestResult.Value.DatFile.Name, ingestResult.Value.DatFile.GameCount,
            ingestResult.Value.DatFile.Id);
    }

}
