using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly.Registry;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Resilience;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Commands.IngestRom;
using Romd.Domain.Jobs;

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
        CancellationToken ct)
    {
        var policy = _resilience.GetPipeline(ResiliencePolicyKey.TransientFault);
        long sizeBytes = TryGetSize(path);

        try
        {
            return await policy.ExecuteAsync(
                async token => await ProcessCoreAsync(
                    path, sizeBytes, allowUnidentified, archiveOnly, token),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error processing ROM {File}", path);
            return new RomProcessingResult(RomIngestOutcome.Failed, ex.Message, sizeBytes);
        }
    }

    private async Task<RomProcessingResult> ProcessCoreAsync(
        string path,
        long sizeBytes,
        bool allowUnidentified,
        bool archiveOnly,
        CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<IngestRomCommand, RomIngestResult>>();

        await using var stream = File.OpenRead(path);
        var command = new IngestRomCommand(
            stream, Path.GetFileName(path), allowUnidentified, archiveOnly);
        var result = await handler.HandleAsync(command, ct);

        if (result.IsError)
        {
            var error = result.FirstError;
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
            result.Value.RomFile.Id,
            result.Value.MatchedTitleIds,
            result.Value.PlatformId);
    }

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
    int? PlatformId = null);
