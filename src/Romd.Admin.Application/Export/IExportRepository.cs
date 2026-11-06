namespace Romd.Admin.Application.Export;

public interface IExportRepository
{
    Task<IReadOnlyList<ExportFileData>> GetExportFilesAsync(
        ExportScope scope,
        CancellationToken ct = default);

    Task<IReadOnlyList<ExportFileData>> GetExportFilesForTitleAsync(
        int titleId,
        AuthorizedExportScope authorization,
        CancellationToken ct = default);
}
