using ErrorOr;
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

    public AddPlatformAliasCommandHandler(
        IPlatformRepository platformRepository,
        IPlatformAliasRepository aliasRepository)
    {
        _platformRepository = platformRepository;
        _aliasRepository = aliasRepository;
    }

    public async Task<ErrorOr<PlatformAlias>> HandleAsync(AddPlatformAliasCommand command, CancellationToken ct = default)
    {
        var platform = await _platformRepository.GetByIdAsync(command.PlatformId, ct);
        if (platform is null)
        {
            return CatalogErrors.PlatformNotFound(command.PlatformId);
        }

        if (command.Type == PlatformAliasType.Name)
        {
            var alias = PlatformAlias.CreateName(command.PlatformId, command.Value);

            if (await _aliasRepository.NameAliasExistsAsync(alias.NormalizedValue, ct))
            {
                return PlatformErrors.AliasAlreadyExists(command.Value);
            }

            return await _aliasRepository.AddAsync(alias, ct);
        }

        if (await _aliasRepository.ProviderMappingExistsAsync(command.PlatformId, command.Provider!, ct))
        {
            return PlatformErrors.ProviderMappingAlreadyExists(command.Provider!);
        }

        var mapping = PlatformAlias.CreateProviderMapping(command.PlatformId, command.Provider!, command.Value);
        return await _aliasRepository.AddAsync(mapping, ct);
    }
}
