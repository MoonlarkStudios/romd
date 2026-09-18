using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;

namespace Romd.Admin.Application.ReferenceData.Companies.Commands;

public sealed class UpdateCompanyCommandHandler : ICommandHandler<UpdateCompanyCommand, ReferenceResourceResult<CompanyResourceDto, CompanyOverridesDto>>
{
    private readonly IReferenceMutationSession session;
    private readonly ICompanyReferenceRepository repository;
    private readonly ICompanyReferenceReader reader;

    public UpdateCompanyCommandHandler(IReferenceMutationSession session, ICompanyReferenceRepository repository,
    ICompanyReferenceReader reader)
    {
        this.session = session;
        this.repository = repository;
        this.reader = reader;
    }

    public Task<ErrorOr<ReferenceResourceResult<CompanyResourceDto, CompanyOverridesDto>>> HandleAsync(UpdateCompanyCommand command, CancellationToken ct = default) =>
        session.RunAsync<ReferenceResourceResult<CompanyResourceDto, CompanyOverridesDto>>(async token =>
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
            resource = resource.Metadata.Ownership == ReferenceOwnership.Romd
                ? resource with { Overrides = resource.Overrides.Merge(patch) }
                : resource with { Definition = patch.Apply(resource.Definition) };
            await repository.StageAsync(resource, token);
            await session.FlushAsync(token);
            return (await reader.GetAsync(command.Key, token))!;
        }, ct);
}
