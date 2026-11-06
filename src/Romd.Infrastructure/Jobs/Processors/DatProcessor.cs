using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Application.Common.Content;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly.Registry;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Resilience;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.IngestDat;

namespace Romd.Infrastructure.Jobs.Processors;

public sealed class DatProcessor
{
    private readonly ILogger<DatProcessor> _logger;
    private readonly ResiliencePipelineProvider<string> _resilience;
    private readonly IServiceProvider _serviceProvider;

    public DatProcessor(
        IServiceProvider serviceProvider,
        ResiliencePipelineProvider<string> resilience,
        ILogger<DatProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _resilience = resilience;
        _logger = logger;
    }

    public async Task<DatProcessingResult> ProcessAsync(
        string path,
        int? platformId,
        CancellationToken ct,
        UploadItemContext? uploadItem = null)
    {
        var policy = _resilience.GetPipeline(ResiliencePolicyKey.TransientFault);
        long sizeBytes = TryGetSize(path);

        try
        {
            if (uploadItem is not null)
                return await ProcessCoreAsync(path, sizeBytes, platformId, uploadItem, ct);
            return await policy.ExecuteAsync(
                async token => await ProcessCoreAsync(path, sizeBytes, platformId, uploadItem, token),
                ct);
        }
        catch (Exception ex) when (IngestionFailure.Classify(ex) is IngestionFailureKind.Permanent or IngestionFailureKind.Uncertain)
        {
            var committed = await UploadOutcomeRecovery.ReadAsync(_serviceProvider, uploadItem, JobItemKind.Dat, CancellationToken.None);
            if (committed is not null)
                return FromOutcome(committed);
            if (ex is UploadOutcomeUncertainException)
                throw;
            _logger.LogError(ex, "Error processing DAT {File}", path);
            return new DatProcessingResult(false, ex.Message, sizeBytes);
        }
    }

    private async Task<DatProcessingResult> ProcessCoreAsync(
        string path,
        long sizeBytes,
        int? platformId,
        UploadItemContext? uploadItem,
        CancellationToken ct)
    {
        var prior = await UploadOutcomeRecovery.ReadAsync(_serviceProvider, uploadItem, JobItemKind.Dat, ct);
        if (prior is not null)
            return FromOutcome(prior);
        using var scope = _serviceProvider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<IngestDatCommand, DatIngestResult>>();

        await using var stream = File.OpenRead(path);
        var commandResult = IngestDatCommand.Create(stream, Path.GetFileName(path), platformId);

        if (commandResult.IsError)
        {
            _logger.LogWarning("DAT failed: {File} - {Error}", path, commandResult.FirstError.Description);
            return new DatProcessingResult(false, commandResult.FirstError.Description, sizeBytes);
        }

        var result = await handler.HandleAsync(commandResult.Value with { UploadItem = uploadItem }, ct);
        var committed = await UploadOutcomeRecovery.ReadAsync(_serviceProvider, uploadItem, JobItemKind.Dat, CancellationToken.None);
        if (committed is not null)
            return FromOutcome(committed);
        if (uploadItem is not null && !result.IsError)
            throw new UploadOutcomeUncertainException("Successful DAT ingestion did not publish an upload outcome.");

        if (result.IsError)
        {
            _logger.LogWarning("DAT failed: {File} - {Error}", path, result.FirstError.Description);
            return new DatProcessingResult(false, result.FirstError.Description, sizeBytes);
        }

        var dat = result.Value.DatFile;
        _logger.LogInformation("DAT imported: {File} ({Games} games)", path, dat.GameCount);
        return new DatProcessingResult(
            true,
            null,
            sizeBytes,
            dat.PlatformId.HasValue,
            dat.PlatformId,
            dat.Id,
            dat.GameCount);
    }

    private static DatProcessingResult FromOutcome(JobItem item) => new(
        item.Outcome is JobItemOutcome.DatRouted or JobItemOutcome.DatUnrouted, item.Error, item.SizeBytes,
        item.Outcome == JobItemOutcome.DatRouted, item.PlatformId, item.DatFileId, item.GameCount, item);

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

public readonly record struct DatProcessingResult(
    bool Success,
    string? Error,
    long SizeBytes,
    bool Routed = false,
    int? PlatformId = null,
    int? DatFileId = null,
    int? GameCount = null,
    JobItem? JournaledOutcome = null);
