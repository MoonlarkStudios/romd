using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;

namespace Romd.Admin.Application.ReferenceData.Systems.Commands;

public sealed class UpdateSystemCommandHandler : ICommandHandler<UpdateSystemCommand, ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>>
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

    public Task<ErrorOr<ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>>> HandleAsync(UpdateSystemCommand command, CancellationToken ct = default) =>
        session.RunAsync<ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>>(async token =>
        {
            var resource = await repository.GetAsync(command.Key, token);
            if (resource is null)
                return ReferenceEditErrors.Missing();
            var current = (await reader.GetAsync(command.Key, token))!;
            if (ReferenceEditRules.CheckVersion(current.ETag, command.IfMatch) is { } error)
                return error;
            var patch = command.Changes;
            if (patch.Name is not null && !ReferenceEditRules.Label(patch.Name, 200) || patch.Description?.Length > 2000)
                return ReferenceEditErrors.Invalid("Invalid presentation facts.");
            if (patch.CompactLabel is not null && !ReferenceEditRules.Label(patch.CompactLabel, 40)
                || patch.Icon is not null && !ReferenceEditRules.Asset(patch.Icon))
                return ReferenceEditErrors.Invalid("Invalid system presentation.");
            if (patch.Icon is { } hash && !await repository.AssetExistsAsync(hash, token))
                return ReferenceEditErrors.Invalid("Upload the icon first.");
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
                : resource with { Definition = patch.Apply(resource.Definition) };
            await repository.StageAsync(resource, token);
            await session.FlushAsync(token);
            return (await reader.GetAsync(command.Key, token))!;
        }, ct);
}
