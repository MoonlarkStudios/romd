using ErrorOr;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Domain.Identity;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Export.Queries.PrepareReleaseDownload;

public sealed record PrepareReleaseDownloadQuery(int ReleaseId,
    RomDeliveryFormat Format = RomDeliveryFormat.Original) : IQuery<AdminDownloadTicket>;

public sealed class PrepareReleaseDownloadQueryHandler(
    ICurrentUser user, IReleaseDownloadRepository repository, IFileStorageService storage,
    IAdminDownloadTickets tickets, IRomContentCatalog contentCatalog, RomDeliveryPolicy deliveryPolicy,
    IRomChdDeliveryAdmission? chdAdmission = null) : IQueryHandler<PrepareReleaseDownloadQuery, AdminDownloadTicket>
{
    public async Task<ErrorOr<AdminDownloadTicket>> HandleAsync(PrepareReleaseDownloadQuery query, CancellationToken ct = default)
    {
        if (user.UserId is not { } userId)
            return Error.Unauthorized();
        var plan = await repository.GetAsync(query.ReleaseId, userId, user.HasRole(RomdRoleType.Contributor), ct);
        if (plan is null || plan.Entries.Count == 0)
            return Error.NotFound("Export.ReleaseUnavailable", "No downloadable files in this release.");
        if (plan.Entries.Count > ExportArchivePlanner.MaximumTicketEntries)
            return Error.Validation("Export.TooManyFiles", "This release requires a background export.");
        var bound = await ExportArchivePlanner.BindAsync(plan.Entries, query.Format, contentCatalog, deliveryPolicy, ct: ct,
            chdAdmission: chdAdmission);
        if (bound.IsError)
            return bound.Errors;
        foreach (var file in bound.Value.Entries)
            if (!await storage.ExistsAsync(file.FileId, ct))
                return Error.Conflict("Export.FileUnavailable", "A release file is no longer in storage. Refresh and try again.");
        var filename = ExportArchivePaths.SafeFilename(plan.Name) + (plan.MissingFiles > 0 ? "-available-files" : "") + ".zip";
        return tickets.Issue(new(filename, bound.Value.Entries, IsArchive: true));
    }
}
