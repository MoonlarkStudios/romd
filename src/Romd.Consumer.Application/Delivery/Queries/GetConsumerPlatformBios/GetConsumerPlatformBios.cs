using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Delivery;
using Romd.Contracts.Consumer.Releases;

namespace Romd.Consumer.Application.Delivery.Queries.GetConsumerPlatformBios;

public sealed record GetConsumerPlatformBiosQuery(string PlatformShortName) : IQuery<ConsumerPlatformBiosDto>;

/// <summary>
///     Lists a platform's BIOS files with availability and BIOS download grants.
///     Exposure gate: the current library must contain at least one owned and exposed materialized
///     release of the platform (<c>MaterializedLibraryReleases.IsOwned &amp;&amp; IsExposed</c>) in a
///     library whose configuration state is Valid — the same access predicate the release-manifest
///     path uses. Unknown platforms and unexposed platforms both surface as
///     <see cref="ConsumerErrors.PlatformNotFound" />.
/// </summary>
public sealed class GetConsumerPlatformBiosQueryHandler(
    ICurrentUser currentUser,
    IConsumerBiosRepository biosRepository,
    IConsumerBiosGrantIssuer biosGrantIssuer)
    : IQueryHandler<GetConsumerPlatformBiosQuery, ConsumerPlatformBiosDto>
{
    public async Task<ErrorOr<ConsumerPlatformBiosDto>> HandleAsync(
        GetConsumerPlatformBiosQuery query,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        var request = new ConsumerPlatformBiosRequest(
            new ConsumerLibraryScope(userId),
            query.PlatformShortName);
        var platformBios = await biosRepository.GetPlatformBiosAsync(request, ct);

        return platformBios switch
        {
            ConsumerLibraryReadResult<ConsumerPlatformBios>.Found { Value.IsExposed: false } =>
                ConsumerErrors.PlatformNotFound(),
            ConsumerLibraryReadResult<ConsumerPlatformBios>.Found found =>
                ToContract(userId, found.LibraryId, found.Value),
            ConsumerLibraryReadResult<ConsumerPlatformBios>.ItemNotFound =>
                ConsumerErrors.PlatformNotFound(),
            ConsumerLibraryReadResult<ConsumerPlatformBios>.LibraryUnavailable =>
                ConsumerErrors.LibraryNotFound(),
            ConsumerLibraryReadResult<ConsumerPlatformBios>.ProjectionInconsistent =>
                ConsumerErrors.PlatformNotFound(),
            _ => throw new InvalidOperationException("Unknown Consumer Library BIOS result.")
        };
    }

    private ConsumerPlatformBiosDto ToContract(
        Guid userId,
        int libraryId,
        ConsumerPlatformBios platformBios) =>
        new()
        {
            SystemKey = platformBios.PlatformShortName,
            Items = platformBios.Files
                .Select(file => ToContract(userId, libraryId, file))
                .ToList()
        };

    private ConsumerPlatformBiosItemDto ToContract(Guid userId, int libraryId, ConsumerBiosFile file) =>
        new()
        {
            BiosId = IdCoder.Encode(file.BiosId),
            Name = file.BiosName,
            FileName = file.FileName,
            SizeBytes = (ByteCount)file.SizeBytes,
            Sha1 = file.Sha1?.ToString(),
            Md5 = file.Md5?.ToString(),
            Sha256 = file.Sha256?.ToString(),
            IsAvailable = file.IsAvailable,
            ContentGrant = CreateContentGrant(userId, libraryId, file)
        };

    private ContentGrantDto? CreateContentGrant(Guid userId, int libraryId, ConsumerBiosFile file)
    {
        if (!file.IsAvailable ||
            file.FileId is not { } fileId ||
            file.Sha256 is not { } sha256 ||
            file.ContentSizeBytes is not { } contentSizeBytes)
        {
            return null;
        }

        var contentGrant = biosGrantIssuer.IssueBiosDownloadGrant(new ConsumerBiosGrant(
            userId,
            libraryId,
            file.BiosId,
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
