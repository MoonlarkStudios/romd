using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Export.Queries.ResolveExportScope;
using Romd.Application.Common.Content;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Contracts.Management.Export;
using Romd.Domain.Identity;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Export.Queries.GetArchiveFormats;

/// <summary>
///     The formats a title ZIP and release ZIPs can be requested in, for the current user. <paramref name="TitleId" /> is
///     null when only releases are listed; release ids are canonical catalog release ids.
/// </summary>
public sealed record GetArchiveFormatsQuery(int? TitleId, IReadOnlyCollection<int> ReleaseIds) : IQuery<ArchiveFormats>;

/// <summary>Each requested archive's formats; <see cref="Title" /> is null when no title was requested.</summary>
public sealed record ArchiveFormats(IReadOnlyList<ArchiveFormat>? Title,
    IReadOnlyDictionary<int, IReadOnlyList<ArchiveFormat>> Releases);

public sealed class GetArchiveFormatsQueryHandler(
    ICurrentUser user,
    IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope> scopes,
    IExportRepository exportRepository,
    IReferenceCatalogService referenceCatalog,
    IReleaseDownloadRepository releaseDownloads,
    IRomContentCatalog contentCatalog,
    RomDeliveryPolicy deliveryPolicy,
    IRomChdDeliveryAdmission chdAdmission,
    ILogger<GetArchiveFormatsQueryHandler> logger) : IQueryHandler<GetArchiveFormatsQuery, ArchiveFormats>
{
    /// <summary>
    ///     Plans each ZIP from the members its download ticket reads, so a listed format is one that ZIP holds. A ZIP the
    ///     user cannot download, or one too large for a ticket, offers only original: its request fails in every format.
    ///     Title reads and writes carry these lists, so member content that cannot be described lists only original for
    ///     every ZIP instead of failing the title; the ZIPs holding that member still fail when requested.
    /// </summary>
    public async Task<ErrorOr<ArchiveFormats>> HandleAsync(GetArchiveFormatsQuery query, CancellationToken ct = default)
    {
        IReadOnlyList<ExportArchiveEntry> title = query.TitleId is { } titleId
            && await TitleArchives.ReadAsync(titleId, scopes, exportRepository, referenceCatalog, deliveryPolicy, ct)
                is { IsError: false } archive
                ? archive.Value.Entries : [];
        int[] releaseIds = query.ReleaseIds.Distinct().ToArray();
        var releases = user.UserId is { } userId && releaseIds.Length > 0
            ? await releaseDownloads.GetManyAsync(releaseIds, userId, user.HasRole(RomdRoleType.Contributor), ct)
            : new Dictionary<int, ReleaseDownloadPlan>();
        IReadOnlyList<IReadOnlyList<ExportArchiveEntry>> archives =
        [
            title, .. releaseIds.Select(id => releases.TryGetValue(id, out var plan)
                && plan.Entries.Count <= ExportArchivePlanner.MaximumTicketEntries ? plan.Entries : [])
        ];
        IReadOnlyList<IReadOnlyList<RomDeliveryFormat>> formats;
        try
        {
            formats = await ExportArchivePlanner.FormatsAsync(archives, contentCatalog, deliveryPolicy, chdAdmission, ct);
        }
        catch (InvalidDataException exception)
        {
            logger.LogWarning(exception, "Archive formats list only original because a member's stored content is inconsistent");
            formats = archives.Select(_ => (IReadOnlyList<RomDeliveryFormat>)[RomDeliveryFormat.Original]).ToArray();
        }
        return new ArchiveFormats(query.TitleId is null ? null : ToContract(formats[0]),
            releaseIds.Select((id, index) => (Id: id, Formats: formats[index + 1]))
                .ToDictionary(release => release.Id, release => ToContract(release.Formats)));
    }

    private IReadOnlyList<ArchiveFormat> ToContract(IReadOnlyList<RomDeliveryFormat> formats) =>
        ArchiveFormatMapping.ToContract(formats, deliveryPolicy.Formats);
}
