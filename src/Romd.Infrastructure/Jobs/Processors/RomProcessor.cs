using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly.Registry;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Resilience;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Commands.IngestRom;
using Romd.Application.Common.Content;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Storage;

namespace Romd.Infrastructure.Jobs.Processors;

public sealed class RomProcessor
{
    private readonly ILogger<RomProcessor> _logger;
    private readonly ResiliencePipelineProvider<string> _resilience;
    private readonly IServiceProvider _serviceProvider;

    public RomProcessor(
        IServiceProvider serviceProvider,
        ResiliencePipelineProvider<string> resilience,
        ILogger<RomProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _resilience = resilience;
        _logger = logger;
    }

    public async Task<RomProcessingResult> ProcessAsync(
        string path,
        bool allowUnidentified,
        bool archiveOnly,
        CancellationToken ct,
        bool trackedOnly = false,
        UploadItemContext? uploadItem = null)
    {
        var policy = _resilience.GetPipeline(ResiliencePolicyKey.TransientFault);
        long sizeBytes = TryGetSize(path);

        try
        {
            if (uploadItem is not null)
                return await ProcessCoreAsync(path, sizeBytes, allowUnidentified, archiveOnly, trackedOnly, uploadItem, ct);
            return await policy.ExecuteAsync(
                async token => await ProcessCoreAsync(
                    path, sizeBytes, allowUnidentified, archiveOnly, trackedOnly, uploadItem, token),
                ct);
        }
        catch (Exception ex) when (IngestionFailure.Classify(ex) is IngestionFailureKind.Permanent or IngestionFailureKind.Uncertain)
        {
            var committed = await UploadOutcomeRecovery.ReadAsync(_serviceProvider, uploadItem, JobItemKind.Rom, CancellationToken.None);
            if (committed is not null)
                return FromOutcome(committed);
            if (ex is UploadOutcomeUncertainException)
                throw;
            _logger.LogError(ex, "Error processing ROM {File}", path);
            return new RomProcessingResult(RomIngestOutcome.Failed, ex.Message, sizeBytes);
        }
    }

    private async Task<RomProcessingResult> ProcessCoreAsync(
        string path,
        long sizeBytes,
        bool allowUnidentified,
        bool archiveOnly,
        bool trackedOnly,
        UploadItemContext? uploadItem,
        CancellationToken ct)
    {
        var prior = await UploadOutcomeRecovery.ReadAsync(_serviceProvider, uploadItem, JobItemKind.Rom, ct);
        if (prior is not null)
            return FromOutcome(prior);
        using var scope = _serviceProvider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<IngestRomCommand, RomIngestResult>>();

        await using var stream = File.OpenRead(path);
        if (uploadItem is not null && stream.Length == 0 && Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase))
        {
            if (trackedOnly)
                return new RomProcessingResult(RomIngestOutcome.Rejected, CatalogErrors.UntrackedRom().Description, sizeBytes);
            var intake = scope.ServiceProvider.GetRequiredService<IOriginalDiscDescriptorIntake>();
            return FromOutcome(await intake.IntakeAsync(stream, uploadItem.JobId, uploadItem.RelativePath, ct));
        }
        string filename = Path.GetFileName(path);
        var command = new IngestRomCommand(
            stream, filename, Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase) ? false : allowUnidentified, archiveOnly, trackedOnly, uploadItem);
        var result = await handler.HandleAsync(command, ct);
        var committed = await UploadOutcomeRecovery.ReadAsync(_serviceProvider, uploadItem, JobItemKind.Rom, CancellationToken.None);
        if (committed is not null)
            return FromOutcome(committed);
        if (uploadItem is not null && !result.IsError)
            throw new UploadOutcomeUncertainException("Successful ROM ingestion did not publish an upload outcome.");

        if (result.IsError)
        {
            var error = result.FirstError;
            if (!trackedOnly && uploadItem is not null && Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase)
                && error.Code == "Library.UnidentifiedRom")
            {
                // DAT-matched descriptors retain the existing ROM admission path. Unmatched CUEs
                // enter the separate bounded attachment ledger, never unidentified-ROM admission.
                // Tracked-only imports must retain their admission rejection and source originals.
                stream.Position = 0;
                var intake = scope.ServiceProvider.GetRequiredService<IOriginalDiscDescriptorIntake>();
                return FromOutcome(await intake.IntakeAsync(stream, uploadItem.JobId, uploadItem.RelativePath, ct));
            }
            if (error.Code == RomPublicationErrors.RetryRequiredCode)
                return new RomProcessingResult(default, null, sizeBytes, RetryReason: error.Description);
            if (error.Code == "Library.UntrackedRom")
                return new RomProcessingResult(RomIngestOutcome.Rejected, error.Description, sizeBytes);

            if (error.Code == "Library.UnidentifiedRom")
            {
                return new RomProcessingResult(RomIngestOutcome.Rejected, null, sizeBytes);
            }

            return new RomProcessingResult(RomIngestOutcome.Failed, error.Description, sizeBytes);
        }

        return new RomProcessingResult(
            result.Value.IsNew ? RomIngestOutcome.Ingested : RomIngestOutcome.Deduplicated,
            null,
            sizeBytes,
            result.Value switch
            {
                RomIngestResult.WholeFile whole => whole.RomFile.Id,
                RomIngestResult.CdArchive => (int?)null,
                _ => throw new InvalidDataException("Unknown ingestion receipt.")
            },
            result.Value.MatchedTitleIds,
            result.Value.PlatformId);
    }

    private static RomProcessingResult FromOutcome(JobItem item) => new(
        item.Outcome switch
        {
            JobItemOutcome.Ingested => RomIngestOutcome.Ingested,
            JobItemOutcome.Deduplicated => RomIngestOutcome.Deduplicated,
            JobItemOutcome.Rejected => RomIngestOutcome.Rejected,
            JobItemOutcome.Failed => RomIngestOutcome.Failed,
            _ => throw new InvalidDataException("Invalid ROM outcome.")
        }, item.Error, item.SizeBytes, item.RomFileId, item.MatchedTitleIds, item.PlatformId, item);

    private static long TryGetSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }
}

public readonly record struct RomProcessingResult(
    RomIngestOutcome Outcome,
    string? Error,
    long SizeBytes,
    int? RomFileId = null,
    IReadOnlyList<int>? MatchedTitleIds = null,
    int? PlatformId = null,
    JobItem? JournaledOutcome = null,
    string? RetryReason = null);
