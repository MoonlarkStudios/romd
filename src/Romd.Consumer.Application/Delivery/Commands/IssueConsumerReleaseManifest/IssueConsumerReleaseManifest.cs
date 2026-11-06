using ErrorOr;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Releases;
using Romd.Domain.Source.Rom;

namespace Romd.Consumer.Application.Delivery.Commands.IssueConsumerReleaseManifest;

public sealed record IssueConsumerReleaseManifestCommand(int ReleaseId) : ICommand<ConsumerReleaseManifestDto>;

public sealed class IssueConsumerReleaseManifestCommandHandler(
    ICurrentUser currentUser,
    IConsumerReleaseManifestRepository manifestRepository,
    IConsumerContentGrantIssuer contentGrantIssuer,
    IConsumerDerivedDescriptorGrantIssuer derivedGrantIssuer,
    IServerInstanceIdentity serverInstanceIdentity,
    RomDeliveryPolicy deliveryPolicy)
    : ICommandHandler<IssueConsumerReleaseManifestCommand, ConsumerReleaseManifestDto>
{
    public async Task<ErrorOr<ConsumerReleaseManifestDto>> HandleAsync(
        IssueConsumerReleaseManifestCommand command,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        var request = new ConsumerReleaseManifestRequest(
            new ConsumerLibraryScope(userId),
            command.ReleaseId);
        var result = await manifestRepository.GetManifestAsync(request, ct);

        if (result is ConsumerLibraryReadResult<ConsumerReleaseManifest>.Found scoped &&
            (scoped.Value.UserId != userId || scoped.Value.ReleaseId != command.ReleaseId ||
             scoped.Value.Items.Any(item => item.Source is ConsumerReleaseManifestSource.Derived derived && derived.Descriptor.ReleaseId != command.ReleaseId)))
            return ConsumerErrors.ContentNotFound();

        return result switch
        {
            ConsumerLibraryReadResult<ConsumerReleaseManifest>.Found found =>
                ToContract(found.LibraryId, found.Value),
            ConsumerLibraryReadResult<ConsumerReleaseManifest>.ItemNotFound =>
                ConsumerErrors.ContentNotFound(),
            ConsumerLibraryReadResult<ConsumerReleaseManifest>.LibraryUnavailable =>
                ConsumerErrors.LibraryNotFound(),
            ConsumerLibraryReadResult<ConsumerReleaseManifest>.ProjectionInconsistent =>
                ConsumerErrors.ContentNotFound(),
            _ => throw new InvalidOperationException("Unknown Consumer Library manifest result.")
        };
    }

    private ConsumerReleaseManifestDto ToContract(int libraryId, ConsumerReleaseManifest manifest) =>
        new()
        {
            ServerInstanceId = serverInstanceIdentity.InstanceId.ToString("D"),
            ReleaseId = IdCoder.Encode(manifest.ReleaseId),
            TitleId = IdCoder.Encode(manifest.TitleId),
            SystemKey = manifest.PlatformShortName,
            Name = manifest.ReleaseName,
            Revision = manifest.Revision,
            IsComplete = manifest.IsComplete,
            IsDeliveryComplete = manifest.IsDeliveryComplete,
            Runtime = ToContract(manifest.Runtime),
            Items = manifest.Items.Select(item => ToContract(libraryId, manifest, item)).ToList()
        };

    private static ConsumerReleaseRuntimeDto ToContract(ConsumerReleaseRuntime runtime) =>
        new()
        {
            ContentType = runtime.ContentType,
            Launch = runtime.Launch is null ? null : ToContract(runtime.Launch),
            Packaging = runtime.Packaging,
            MinimumInstallBytes = (ByteCount)runtime.MinimumInstallBytes
        };

    private static ConsumerLaunchTargetDto ToContract(ConsumerLaunchTarget launch) =>
        new()
        {
            Type = launch.Type,
            RelativePath = launch.RelativePath
        };

    private ConsumerReleaseManifestItemDto ToContract(
        int libraryId,
        ConsumerReleaseManifest manifest,
        ConsumerReleaseManifestItem item) =>
        new()
        {
            RelativePath = item.RelativePath,
            Role = item.Role,
            Provenance = item.Source is ConsumerReleaseManifestSource.Derived ? ConsumerReleaseFileProvenance.Derived : ConsumerReleaseFileProvenance.Original,
            SizeBytes = (ByteCount)item.SizeBytes,
            Sha256 = item.Sha256?.ToString(),
            IsAvailable = item.IsAvailable,
            ContentGrant = CreateContentGrant(libraryId, manifest, item),
            Formats = item.Formats.Select(format => ToContract(libraryId, manifest, format)).ToArray()
        };

    private ConsumerReleaseManifestFormatDto ToContract(int libraryId, ConsumerReleaseManifest manifest,
        ConsumerReleaseManifestFormat format)
    {
        bool available = format.Delivery.State == RomDeliveryState.Available
            && format.Delivery.Target is { } bound && RomDeliveryTargets.IsValid(bound)
            && bound.Format == format.Delivery.Format;
        var delivery = format.Delivery.State == RomDeliveryState.Available && !available
            ? new RomDeliveryOption(format.Delivery.Format, RomDeliveryState.Unsupported, null, "unknown", [])
            : format.Delivery;
        var metadata = delivery.ToContract(deliveryPolicy.Formats);
        return new()
        {
            Format = metadata.Format,
            Label = metadata.Label,
            FileExtension = metadata.FileExtension,
            State = metadata.State,
            Filename = metadata.Filename,
            SizeBytes = metadata.SizeBytes,
            Sha256 = metadata.Sha256,
            Md5 = metadata.Md5,
            Sha1 = metadata.Sha1,
            ProfileVersion = metadata.ProfileVersion,
            RuntimeApplicability = metadata.RuntimeApplicability,
            RelativePath = format.RelativePath,
            Runtime = available && format.Runtime is { } runtime ? ToContract(runtime) : null,
            ContentGrant = available && delivery.Target is { } target
                ? CreateContentGrant(libraryId, manifest, target) : null
        };
    }

    private ContentGrantDto CreateContentGrant(int libraryId, ConsumerReleaseManifest manifest, RomDeliveryTarget target)
    {
        var issued = contentGrantIssuer.IssueDownloadGrant(new(manifest.UserId, libraryId, manifest.TitleId,
            manifest.ReleaseId, target));
        return new() { DownloadUrl = issued.DownloadUrl, ExpiresAt = issued.ExpiresAt };
    }

    private ContentGrantDto? CreateContentGrant(
        int libraryId,
        ConsumerReleaseManifest manifest,
        ConsumerReleaseManifestItem item)
    {
        if (item.Source is ConsumerReleaseManifestSource.Original original)
            return original.AvailableTarget is { } target ? CreateContentGrant(libraryId, manifest, target) : null;
        if (item.Source is ConsumerReleaseManifestSource.Missing)
            return null;
        var descriptor = ((ConsumerReleaseManifestSource.Derived)item.Source).Descriptor;
        var issued = derivedGrantIssuer.Issue(new(manifest.UserId, libraryId, manifest.TitleId, descriptor));
        return new() { DownloadUrl = issued.DownloadUrl, ExpiresAt = issued.ExpiresAt };
    }
}
