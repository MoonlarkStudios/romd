using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Source.Platform;

namespace Romd.Admin.Application.Source.Platform.Commands.AddPlatformAlias;

/// <summary>
///     Command to add an alias (name or provider mapping) to a platform.
/// </summary>
public sealed record AddPlatformAliasCommand : ICommand<PlatformAlias>
{
    public const string NameType = "name";
    public const string ProviderType = "provider";

    private AddPlatformAliasCommand() { }

    public required int PlatformId { get; init; }
    public required PlatformAliasType Type { get; init; }
    public required string Value { get; init; }

    /// <summary>
    ///     Provider identifier, required when Type is ProviderMapping.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    ///     Creates a validated command. Type is "name" or "provider".
    /// </summary>
    public static ErrorOr<AddPlatformAliasCommand> Create(
        int platformId,
        string type,
        string value,
        string? provider = null)
    {
        var errors = new List<Error>();

        if (platformId <= 0)
        {
            errors.Add(Error.Validation("Command.InvalidPlatformId", "Platform ID must be positive"));
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(Error.Validation("Command.InvalidAliasValue", "Alias value cannot be null or empty"));
        }

        PlatformAliasType? aliasType = type?.Trim().ToLowerInvariant() switch
        {
            NameType => PlatformAliasType.Name,
            ProviderType => PlatformAliasType.ProviderMapping,
            _ => null
        };

        if (aliasType is null)
        {
            errors.Add(PlatformErrors.InvalidAliasType(type ?? string.Empty));
        }
        else if (aliasType == PlatformAliasType.ProviderMapping && string.IsNullOrWhiteSpace(provider))
        {
            errors.Add(Error.Validation("Command.MissingProvider", "Provider is required for provider mappings"));
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        return new AddPlatformAliasCommand
        {
            PlatformId = platformId,
            Type = aliasType!.Value,
            Value = value!.Trim(),
            Provider = aliasType == PlatformAliasType.ProviderMapping
                ? provider!.Trim().ToLowerInvariant()
                : null
        };
    }
}

public sealed class AddPlatformAliasCommandHandler : ICommandHandler<AddPlatformAliasCommand, PlatformAlias>
{
    private readonly IPlatformAliasRepository _aliasRepository;
    private readonly IPlatformRepository _platformRepository;
    private readonly TimeProvider _clock;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<AddPlatformAliasCommandHandler> _logger;

    public AddPlatformAliasCommandHandler(
        IPlatformRepository platformRepository,
        IPlatformAliasRepository aliasRepository,
        IUnitOfWork unitOfWork,
        ILogger<AddPlatformAliasCommandHandler> logger,
        TimeProvider clock)
    {
        _platformRepository = platformRepository;
        _aliasRepository = aliasRepository;
        _clock = clock;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public Task<ErrorOr<PlatformAlias>> HandleAsync(AddPlatformAliasCommand command, CancellationToken ct = default) =>
        _unitOfWork.ExecuteInTransactionAsync<PlatformAlias>(async token =>
        {
            var platform = await _platformRepository.GetByIdAsync(command.PlatformId, token);
            if (platform is null)
            {
                return CatalogErrors.PlatformNotFound();
            }

            PlatformAlias alias;
            if (command.Type == PlatformAliasType.Name)
            {
                alias = PlatformAlias.CreateName(command.PlatformId, command.Value, _clock.GetUtcNow());
                if (await _aliasRepository.NameAliasExistsAsync(alias.NormalizedValue, token))
                {
                    return PlatformErrors.AliasAlreadyExists(command.Value);
                }
            }
            else
            {
                if (await _aliasRepository.ProviderMappingExistsAsync(command.PlatformId, command.Provider!, token))
                {
                    return PlatformErrors.ProviderMappingAlreadyExists(command.Provider!);
                }
                alias = PlatformAlias.CreateProviderMapping(command.PlatformId, command.Provider!, command.Value, _clock.GetUtcNow());
            }

            var creation = await _aliasRepository.AddStagedAsync(alias, token);
            await _unitOfWork.FlushAsync(token);
            return creation.ReadAfterFlush();
        }, _logger, ct);
}
