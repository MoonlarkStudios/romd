using ErrorOr;
using Romd.Admin.Application.Export.Queries.ResolveExportScope;
using Romd.Application.Common.Content;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;

namespace Romd.Admin.Application.Export;

/// <summary>The files of one title's ZIP, before any format is bound.</summary>
public sealed record TitleArchive(string TitleName, IReadOnlyList<ExportArchiveEntry> Entries);

public static class TitleArchives
{
    /// <summary>
    ///     The title's ZIP members under the current user's export scope, each reached through its title's platform. The title
    ///     ZIP and the title's archive formats both read it, so they plan the same members.
    /// </summary>
    public static async Task<ErrorOr<TitleArchive>> ReadAsync(int titleId,
        IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope> scopes, IExportRepository exportRepository,
        IReferenceCatalogService referenceCatalog, RomDeliveryPolicy deliveryPolicy, CancellationToken ct)
    {
        var scope = await scopes.HandleAsync(new ResolveExportScopeQuery(null), ct);
        if (scope.IsError)
            return scope.Errors;
        var files = await exportRepository.GetExportFilesForTitleAsync(titleId, scope.Value, ct);
        if (files.Count == 0)
            return ExportErrors.NotFound();
        if (files.Count > ExportArchivePlanner.MaximumTicketEntries)
            return Error.Validation("Export.TooManyFiles", "This title requires a background export.");
        var deliverySystems = await RomDeliverySystems.ResolveAsync(deliveryPolicy, referenceCatalog, ct);
        return new TitleArchive(files[0].TitleName, files.Select(file => new ExportArchiveEntry(file.FileId,
            ExportArchivePaths.SafeFilename(file.OriginalFilename), file.RomFileId,
            PlatformKeys: [deliverySystems.KeyFor(file.PlatformId)])).ToArray());
    }
}
