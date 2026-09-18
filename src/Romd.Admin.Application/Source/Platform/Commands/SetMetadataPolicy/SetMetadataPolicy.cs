using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Source.Platform.Commands.SetMetadataPolicy;

public sealed record SetMetadataPolicyCommand(int PlatformId, string Revision,
    IReadOnlyDictionary<string, string?> Defaults) : ICommand<Success>;

public sealed class SetMetadataPolicyCommandHandler(
    IPlatformRepository platforms,
    IPlatformFieldDefaultRepository defaults,
    IRematerializationScheduler scheduler,
    IOptions<EnrichmentOptions> options,
    IUnitOfWork unitOfWork,
    ILogger<SetMetadataPolicyCommandHandler> logger) : ICommandHandler<SetMetadataPolicyCommand, Success>
{
    public async Task<ErrorOr<Success>> HandleAsync(SetMetadataPolicyCommand command, CancellationToken ct = default)
    {
        if (command.Defaults.Any(pair => !MetadataPolicy.Fields.Contains(pair.Key) ||
            (!string.IsNullOrEmpty(pair.Value) && pair.Value != "user" &&
             !options.Value.GlobalSourcePriority.Contains(pair.Value))))
            return Error.Validation("MetadataPolicy.InvalidPreference", "Choose a supported metadata field and source.");

        try
        {
            return await unitOfWork.ExecuteInTransactionAsync<Success>(async token =>
            {
                if (await platforms.GetByIdAsync(command.PlatformId, token) is null)
                    return Error.NotFound("Platform.NotFound", "System not found.");
                await defaults.LockAsync(command.PlatformId, token);
                var saved = await defaults.GetByPlatformIdAsync(command.PlatformId, token);
                if (MetadataPolicy.Revision(saved) != command.Revision)
                    return Error.Conflict("MetadataPolicy.Conflict", "Metadata policy changed. Reload the saved policy before saving again.");
                foreach (var (field, source) in command.Defaults)
                {
                    if (string.IsNullOrEmpty(source)) await defaults.ClearAsync(command.PlatformId, field, token);
                    else await defaults.SetAsync(command.PlatformId, field, source, token);
                }
                await scheduler.EnqueuePlatformAsync(command.PlatformId, token);
                return Result.Success;
            }, logger, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update metadata policy for {PlatformId}", command.PlatformId);
            return Error.Failure("MetadataPolicy.SaveFailed", "Metadata policy could not be saved.");
        }
    }
}
