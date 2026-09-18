using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;

namespace Romd.Admin.Application.ReferenceData.Companies.Commands;

public sealed class DeleteCompanyCommandHandler : ICommandHandler<DeleteCompanyCommand, Deleted>
{
    private readonly IReferenceMutationSession session;
    private readonly ICompanyReferenceRepository repository;
    private readonly ICompanyReferenceReader reader;

    public DeleteCompanyCommandHandler(IReferenceMutationSession session, ICompanyReferenceRepository repository,
    ICompanyReferenceReader reader)
    {
        this.session = session;
        this.repository = repository;
        this.reader = reader;
    }

    public Task<ErrorOr<Deleted>> HandleAsync(DeleteCompanyCommand command, CancellationToken ct = default) =>
        session.RunAsync<Deleted>(async token =>
        {
            var resource = await repository.GetAsync(command.Key, token);
            if (resource is null)
                return ReferenceEditErrors.Missing();
            var current = (await reader.GetAsync(command.Key, token))!;
            if (ReferenceEditRules.CheckVersion(current.ETag, command.IfMatch) is { } error)
                return error;
            if (resource.Metadata.Ownership == ReferenceOwnership.Romd)
                return ReferenceEditErrors.Managed();
            if (await repository.HasDependentsAsync(resource.Key, token))
                return ReferenceEditErrors.Conflict("The company still has dependents.");
            await repository.DeleteAsync(resource.Key, token);
            return Result.Deleted;
        }, ct, protectDependencies: true);
}
