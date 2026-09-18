namespace Romd.Admin.Application.Export;

public interface IExportScheduler
{
    Task<Guid> EnqueueLibraryExportAsync(
        ExportScope scope,
        Guid? createdByUserId,
        CancellationToken ct = default);
}
