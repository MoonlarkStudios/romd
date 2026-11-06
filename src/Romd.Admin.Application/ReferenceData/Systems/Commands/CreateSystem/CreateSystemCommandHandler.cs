using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;

namespace Romd.Admin.Application.ReferenceData.Systems.Commands;

public sealed class CreateSystemCommandHandler : ICommandHandler<CreateSystemCommand, ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>>
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

    public Task<ErrorOr<ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>>> HandleAsync(CreateSystemCommand command, CancellationToken ct = default) =>
        session.RunAsync<ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>>(async token =>
        {
            var definition = command.Definition;
            if (!ReferenceEditRules.Key(command.Key))
                return ReferenceEditErrors.Invalid("Installation-owned keys must use the local- namespace.");
            if (!ReferenceEditRules.Label(definition.Name, 200) || definition.Description?.Length > 2000 || !ReferenceEditRules.Label(definition.CompactLabel, 40) || !ReferenceEditRules.Asset(definition.AssetHash) || definition.Retired)
                return ReferenceEditErrors.Invalid("Invalid system facts.");
            if (await repository.KeyExistsAsync(command.Key, token))
                return ReferenceEditErrors.Conflict("The key already exists.");
            var keys = (command.ManufacturerKeys ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (keys.Length > 16 || !await companies.AllExistAsync(keys, token))
                return ReferenceEditErrors.Invalid("Every manufacturer must reference a registered company key (maximum 16).");
            if (definition.AssetHash is { } hash && !await repository.AssetExistsAsync(hash, token))
                return ReferenceEditErrors.Invalid("Upload the icon first.");
            await repository.StageAsync(new SystemReference(0, command.Key, new(ReferenceOwnership.Installation), definition, new(), keys), token);
            await session.FlushAsync(token);
            return (await reader.GetAsync(command.Key, token))!;
        }, ct);
}
