using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;

namespace Romd.Admin.Application.ReferenceData.Systems.Commands;

public sealed class DeleteSystemCommandHandler : ICommandHandler<DeleteSystemCommand, Deleted>
{
    private readonly IReferenceMutationSession session;
    private readonly ISystemReferenceRepository repository;
    private readonly ISystemReferenceReader reader;

    public DeleteSystemCommandHandler(IReferenceMutationSession session, ISystemReferenceRepository repository,
    ISystemReferenceReader reader)
    {
        this.session = session;
        this.repository = repository;
        this.reader = reader;
    }

    public Task<ErrorOr<Deleted>> HandleAsync(DeleteSystemCommand command, CancellationToken ct = default) =>
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
            if (await repository.HasDependentsAsync(resource.Id, token))
                return ReferenceEditErrors.Conflict("The system still has dependents.");
            await repository.DeleteAsync(resource.Id, token);
            return Result.Deleted;
        }, ct, protectDependencies: true);
}
