using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;

using ContractSystemOverrides = Romd.Contracts.Common.ReferenceCatalog.SystemOverrides;

namespace Romd.Admin.Application.ReferenceData.Systems.Commands;

public sealed class CreateSystemCommandHandler : ICommandHandler<CreateSystemCommand, ReferenceResourceResult<SystemResource, ContractSystemOverrides>>
{
    private readonly IReferenceMutationSession session;
    private readonly ISystemReferenceRepository repository;
    private readonly ISystemReferenceReader reader;
    private readonly ICompanyReferenceRepository companies;

    public CreateSystemCommandHandler(IReferenceMutationSession session, ISystemReferenceRepository repository,
    ISystemReferenceReader reader, ICompanyReferenceRepository companies)
    {
        this.session = session;
        this.repository = repository;
        this.reader = reader;
        this.companies = companies;
    }

    public Task<ErrorOr<ReferenceResourceResult<SystemResource, ContractSystemOverrides>>> HandleAsync(CreateSystemCommand command, CancellationToken ct = default) =>
        session.RunAsync<ReferenceResourceResult<SystemResource, ContractSystemOverrides>>(async token =>
        {
            var definition = command.Definition;
            if (!ReferenceEditRules.Key(command.Key))
                return ReferenceEditErrors.Invalid("Installation-owned keys must use the local- namespace.");
            if (!ReferenceEditRules.Label(definition.Name, 200) || definition.Description?.Length > 2000 || !ReferenceEditRules.Label(definition.CompactLabel, 40) || !ReferenceEditRules.Asset(definition.AssetHash) || definition.Retired)
                return ReferenceEditErrors.Invalid("Invalid system facts.");
            if (command.Hardware is { IsValid: false })
                return ReferenceEditErrors.Invalid("Choose a system category, a generation from 1 to 99, and a four-digit release year, or leave those fields blank.");
            if (!ReferenceEditRules.SetupAliases(command.NameAliases))
                return ReferenceEditErrors.Invalid("Enter up to 16 name aliases of 200 characters or fewer.");
            if (await repository.KeyExistsAsync(command.Key, token))
                return ReferenceEditErrors.Conflict("The key already exists.");
            var keys = (command.ManufacturerKeys ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (keys.Length > 16 || !await companies.AllExistAsync(keys, token))
                return ReferenceEditErrors.Invalid("Every manufacturer must reference a registered company key (maximum 16).");
            if (definition.AssetHash is { } hash && !await repository.AssetExistsAsync(hash, token))
                return ReferenceEditErrors.Invalid("Upload the icon first.");
            await repository.StageAsync(new SystemReference(0, command.Key, new(ReferenceOwnership.Installation), definition, new(), keys, command.Hardware), token, command.EnableForSetup);
            await session.FlushAsync(token);
            if (command.NameAliases is { Count: > 0 } names)
            {
                var created = (await repository.GetAsync(command.Key, token))!;
                var aliases = await repository.StageSetupAliasesAsync(created.Id, names, token);
                if (aliases.IsError)
                    return aliases.Errors;
                await session.FlushAsync(token);
            }
            return (await reader.GetAsync(command.Key, token))!;
        }, ct);
}
