using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;

using ContractCompanyOverrides = Romd.Contracts.Common.ReferenceCatalog.CompanyOverrides;

namespace Romd.Admin.Application.ReferenceData.Companies.Commands;

public sealed class CreateCompanyCommandHandler : ICommandHandler<CreateCompanyCommand, ReferenceResourceResult<CompanyResource, ContractCompanyOverrides>>
{
    private readonly IReferenceMutationSession session;
    private readonly ICompanyReferenceRepository repository;
    private readonly ICompanyReferenceReader reader;

    public CreateCompanyCommandHandler(IReferenceMutationSession session, ICompanyReferenceRepository repository,
    ICompanyReferenceReader reader)
    {
        this.session = session;
        this.repository = repository;
        this.reader = reader;
    }

    public Task<ErrorOr<ReferenceResourceResult<CompanyResource, ContractCompanyOverrides>>> HandleAsync(CreateCompanyCommand command, CancellationToken ct = default) =>
        session.RunAsync<ReferenceResourceResult<CompanyResource, ContractCompanyOverrides>>(async token =>
        {
            var definition = command.Definition;
            if (!ReferenceEditRules.Key(command.Key))
                return ReferenceEditErrors.Invalid("Installation-owned keys must use the local- namespace.");
            if (!ReferenceEditRules.Label(definition.Name, 200) || definition.Description?.Length > 2000 || definition.Retired)
                return ReferenceEditErrors.Invalid("Invalid company facts.");
            if (await repository.GetAsync(command.Key, token) is not null)
                return ReferenceEditErrors.Conflict("The key already exists.");
            await repository.StageAsync(new CompanyReference(command.Key, new(ReferenceOwnership.Installation), definition, new()), token);
            await session.FlushAsync(token);
            return (await reader.GetAsync(command.Key, token))!;
        }, ct);
}
