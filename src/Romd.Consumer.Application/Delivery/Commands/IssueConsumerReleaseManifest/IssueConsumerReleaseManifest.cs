using ErrorOr;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Releases;

namespace Romd.Consumer.Application.Delivery.Commands.IssueConsumerReleaseManifest;

public sealed record IssueConsumerReleaseManifestCommand(int ReleaseId) : ICommand<ConsumerReleaseManifestDto>;

public sealed class IssueConsumerReleaseManifestCommandHandler(
    ICurrentUser currentUser,
    IConsumerReleaseManifestRepository manifestRepository,
    IConsumerContentGrantIssuer contentGrantIssuer,
    IServerInstanceIdentity serverInstanceIdentity)
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
            SizeBytes = (ByteCount)item.SizeBytes,
            Sha256 = item.Sha256?.ToString(),
            IsAvailable = item.IsAvailable,
            ContentGrant = CreateContentGrant(libraryId, manifest, item)
        };

    private ContentGrantDto? CreateContentGrant(
        int libraryId,
        ConsumerReleaseManifest manifest,
        ConsumerReleaseManifestItem item)
    {
        if (!item.IsAvailable ||
            item.RomId is not { } romId ||
            item.FileId is not { } fileId ||
            item.Sha256 is not { } sha256 ||
            item.ContentSizeBytes is not { } contentSizeBytes)
        {
            return null;
        }

        var contentGrant = contentGrantIssuer.IssueDownloadGrant(new ConsumerContentGrant(
            manifest.UserId,
            libraryId,
            manifest.TitleId,
            manifest.ReleaseId,
            romId,
            fileId,
            sha256,
            contentSizeBytes));

        return new ContentGrantDto
        {
            DownloadUrl = contentGrant.DownloadUrl,
            ExpiresAt = contentGrant.ExpiresAt
        };
    }
}
