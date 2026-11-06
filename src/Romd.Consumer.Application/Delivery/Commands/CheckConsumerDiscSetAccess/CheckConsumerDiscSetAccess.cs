using ErrorOr;
using Romd.Application.Common.Content;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Delivery.Commands.IssueConsumerDiscSetManifest;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Consumer.Releases;

namespace Romd.Consumer.Application.Delivery.Commands.CheckConsumerDiscSetAccess;

public sealed record CheckConsumerDiscSetAccessCommand(int Id, ConsumerDiscSetAccessRequest Request) : ICommand<ConsumerDiscSetAccessDto>;

public sealed class CheckConsumerDiscSetAccessCommandHandler(ICurrentUser currentUser, IServerInstanceIdentity server,
    IConsumerDiscSetRepository repository, IConsumerDiscSetManifestBuilder manifests)
    : ICommandHandler<CheckConsumerDiscSetAccessCommand, ConsumerDiscSetAccessDto>
{
    public async Task<ErrorOr<ConsumerDiscSetAccessDto>> HandleAsync(CheckConsumerDiscSetAccessCommand command, CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
            return ConsumerErrors.CurrentUserRequired();
        var request = command.Request;
        if (request.ContractVersion != 1 || request.ServerInstanceId != server.InstanceId.ToString("D")
            || request.SelectedReleaseIds is not { Count: > 0 and <= 99 } || request.LayoutVersions is null
            || request.LayoutVersions.Any(x => x is null)
            || request.LayoutVersions.Count != request.SelectedReleaseIds.Count
            || !request.LayoutVersions.Select(x => x.ReleaseId).SequenceEqual(request.SelectedReleaseIds, StringComparer.Ordinal)
            || request.SelectedReleaseIds.Distinct(StringComparer.Ordinal).Count() != request.SelectedReleaseIds.Count
            || request.DeliverySnapshotId is not { Length: 64 })
            return Unavailable();
        var read = await repository.ReadStatusAsync(new(userId), command.Id, ct);
        if (read is ConsumerLibraryReadResult<ConsumerDiscSetPlan>.LibraryUnavailable or ConsumerLibraryReadResult<ConsumerDiscSetPlan>.ProjectionInconsistent)
            return Unavailable();
        if (read is not ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found found)
            return Echo(false, null, null);
        var plan = found.Value;
        if (plan.Revision != request.Revision)
            return Echo(false, null, null);
        var selected = plan.Discs.Where(x => request.SelectedReleaseIds.Contains(IdCoder.Encode(x.Release.ReleaseId), StringComparer.Ordinal)).ToArray();
        if (!selected.Select(x => IdCoder.Encode(x.Release.ReleaseId)).SequenceEqual(request.SelectedReleaseIds, StringComparer.Ordinal))
            return Echo(false, null, null);
        for (int index = 0; index < selected.Length; index++)
        {
            var disc = selected[index];
            var identity = disc.LayoutIdentity ?? (disc.OriginalLayout is { } layout
                ? new OriginalDiscLayoutIdentity(layout.Id, layout.Revision)
                : disc.Readiness == "ready" ? new OriginalDiscLayoutIdentity(null, null) : null);
            if (identity is null)
                continue;
            var current = new ConsumerDiscSetLayoutVersionDto(IdCoder.Encode(disc.Release.ReleaseId),
                identity.Id is { } layoutId ? IdCoder.Encode(layoutId) : null, identity.Revision?.ToString("D"));
            if (current != request.LayoutVersions[index])
                return Echo(false, null, null);
        }
        // Explicit detach/replacement above invalidates the installed identity even when
        // current bytes are unavailable. Unknown bindings or temporary missing media do not.
        if (selected.Any(x => x.Readiness != "ready"))
            return Unavailable();
        var status = DiscSetContracts.Status(plan, server.InstanceId);
        var result = await manifests.BuildAsync(new(command.Id, request.Revision, new(1, request.Revision,
            status.ObservationId, request.SelectedReleaseIds, request.AllowPartial, request.DeliverySnapshotId), IssueGrants: false),
            plan, found.LibraryId, userId, ct);
        if (result.IsError)
            return result.FirstError.Code is "DiscSet.Snapshot" or "DiscSet.Selection" or "DiscSet.PartialConsent"
                ? Echo(false, null, null) : Unavailable();
        var manifest = result.Value;
        var layouts = manifest.Discs.Select(x => new ConsumerDiscSetLayoutVersionDto(x.ReleaseId, x.LayoutId, x.LayoutRevision));
        if (!layouts.SequenceEqual(request.LayoutVersions))
            return Echo(false, null, null);
        return Echo(true, manifest.TitleId, manifest.SystemKey);

        ConsumerDiscSetAccessDto Echo(bool allowed, string? titleId, string? systemKey) => new(1, server.InstanceId.ToString("D"),
            IdCoder.Encode(command.Id), request.Revision, request.DeliverySnapshotId, request.SelectedReleaseIds,
            request.LayoutVersions, allowed, titleId, systemKey);
    }
    private static Error Unavailable() => Error.Conflict("DiscSet.AccessUnavailable", "The exact installed snapshot cannot currently be authorized. This does not revoke cached access.");
}
