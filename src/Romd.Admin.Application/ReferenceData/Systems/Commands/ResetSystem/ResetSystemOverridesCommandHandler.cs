using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;

namespace Romd.Admin.Application.ReferenceData.Systems.Commands;

public sealed class ResetSystemOverridesCommandHandler : ICommandHandler<ResetSystemOverridesCommand, ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>>
{
    private readonly IReferenceMutationSession session;
    private readonly ISystemReferenceRepository repository;
    private readonly ISystemReferenceReader reader;

    public ResetSystemOverridesCommandHandler(IReferenceMutationSession session, ISystemReferenceRepository repository,
    ISystemReferenceReader reader)
    {
        this.session = session;
        this.repository = repository;
        this.reader = reader;
    }

    public Task<ErrorOr<ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>>> HandleAsync(ResetSystemOverridesCommand command, CancellationToken ct = default) =>
        session.RunAsync<ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>>(async token =>
        {
            var resource = await repository.GetAsync(command.Key, token);
            if (resource is null)
                return ReferenceEditErrors.Missing();
            var current = (await reader.GetAsync(command.Key, token))!;
            if (ReferenceEditRules.CheckVersion(current.ETag, command.IfMatch) is { } error)
                return error;
            if (resource.Metadata.Ownership != ReferenceOwnership.Romd)
                return ReferenceEditErrors.Conflict("Installation-owned definitions have no inherited defaults.");
            await repository.StageAsync(resource with { Overrides = resource.Overrides.Reset(command.Field) }, token);
            await session.FlushAsync(token);
            return (await reader.GetAsync(command.Key, token))!;
        }, ct);
}
