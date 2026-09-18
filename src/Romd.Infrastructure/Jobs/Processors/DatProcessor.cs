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
        CancellationToken ct)
    {
        var policy = _resilience.GetPipeline(ResiliencePolicyKey.TransientFault);
        long sizeBytes = TryGetSize(path);

        try
        {
            return await policy.ExecuteAsync(
                async token => await ProcessCoreAsync(path, sizeBytes, platformId, token),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error processing DAT {File}", path);
            return new DatProcessingResult(false, ex.Message, sizeBytes);
        }
    }

    private async Task<DatProcessingResult> ProcessCoreAsync(
        string path,
        long sizeBytes,
        int? platformId,
        CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<IngestDatCommand, DatIngestResult>>();

        await using var stream = File.OpenRead(path);
        var commandResult = IngestDatCommand.Create(stream, Path.GetFileName(path), platformId);

        if (commandResult.IsError)
        {
            _logger.LogWarning("DAT failed: {File} - {Error}", path, commandResult.FirstError.Description);
            return new DatProcessingResult(false, commandResult.FirstError.Description, sizeBytes);
        }

        var result = await handler.HandleAsync(commandResult.Value, ct);

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
    int? GameCount = null);
