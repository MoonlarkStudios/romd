using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;

using ContractSystemOverrides = Romd.Contracts.Common.ReferenceCatalog.SystemOverrides;

namespace Romd.Admin.Application.ReferenceData.Systems.Commands;

public sealed class UpdateSystemCommandHandler : ICommandHandler<UpdateSystemCommand, ReferenceResourceResult<SystemResource, ContractSystemOverrides>>
{
    private readonly IReferenceMutationSession session;
    private readonly ISystemReferenceRepository repository;
    private readonly ISystemReferenceReader reader;
    private readonly ICompanyReferenceRepository companies;

    public UpdateSystemCommandHandler(IReferenceMutationSession session, ISystemReferenceRepository repository,
    ISystemReferenceReader reader, ICompanyReferenceRepository companies)
    {
        this.session = session;
        this.repository = repository;
        this.reader = reader;
        this.companies = companies;
    }

    public Task<ErrorOr<ReferenceResourceResult<SystemResource, ContractSystemOverrides>>> HandleAsync(UpdateSystemCommand command, CancellationToken ct = default) =>
        session.RunAsync<ReferenceResourceResult<SystemResource, ContractSystemOverrides>>(async token =>
        {
            var resource = await repository.GetAsync(command.Key, token);
            if (resource is null)
                return ReferenceEditErrors.Missing();
            var current = (await reader.GetAsync(command.Key, token))!;
            if (ReferenceEditRules.CheckVersion(current.Revision, command.IfMatch) is { } error)
                return error;
            if (!ReferenceEditRules.SetupAliases(command.NameAliases))
                return ReferenceEditErrors.Invalid("Enter up to 16 name aliases of 200 characters or fewer.");
            if (command.Hardware is { } changes)
            {
                if (resource.Metadata.Ownership == ReferenceOwnership.Romd)
                    return ReferenceEditErrors.Invalid("Built-in hardware facts are owned by ROMD.");
                var hardware = changes.Apply(resource.Hardware);
                if (!hardware.IsValid)
                    return ReferenceEditErrors.Invalid("Choose a system category, a generation from 1 to 99, and a four-digit release year, or leave those fields blank.");
                resource = resource with { Hardware = hardware };
            }
            var patch = command.Changes;
            if (patch.Name is not null && !ReferenceEditRules.Label(patch.Name, 200) || patch.Description?.Length > 2000)
                return ReferenceEditErrors.Invalid("Invalid presentation facts.");
            if (patch.CompactLabel is not null && !ReferenceEditRules.Label(patch.CompactLabel, 40)
                || patch.Icon is not null && !ReferenceEditRules.Asset(patch.Icon))
                return ReferenceEditErrors.Invalid("Invalid system presentation.");
            if (patch.Icon is { } hash && !await repository.AssetExistsAsync(hash, token))
                return ReferenceEditErrors.Invalid("Upload the icon first.");
            if (patch.ConsoleArtwork is { } artwork &&
                (!ReferenceEditRules.Asset(artwork) || !await repository.RasterAssetExistsAsync(artwork, token)))
                return ReferenceEditErrors.Invalid("Upload a PNG or WebP console image first.");
            if (command.ManufacturerKeys is { } manufacturers)
            {
                if (resource.Metadata.Ownership == ReferenceOwnership.Romd)
                    return ReferenceEditErrors.Invalid("Built-in manufacturer relationships are owned by ROMD.");
                var keys = manufacturers.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                if (keys.Length > 16 || !await companies.AllExistAsync(keys, token))
                    return ReferenceEditErrors.Invalid("Every manufacturer must reference a registered company key (maximum 16).");
                resource = resource with { ManufacturerKeys = keys };
            }
            resource = resource.Metadata.Ownership == ReferenceOwnership.Romd
                ? resource with { Overrides = resource.Overrides.Merge(patch) }
                : resource with
                {
                    Definition = (patch with { ConsoleArtwork = null }).Apply(resource.Definition),
                    Overrides = resource.Overrides.Merge(new(ConsoleArtwork: patch.ConsoleArtwork))
                };
            if (command.NameAliases is { Count: > 0 } names)
            {
                var aliases = await repository.StageSetupAliasesAsync(resource.Id, names, token);
                if (aliases.IsError)
                    return aliases.Errors;
            }
            await repository.StageAsync(resource, token, command.EnableForSetup);
            await session.FlushAsync(token);
            return (await reader.GetAsync(command.Key, token))!;
        }, ct);
}
