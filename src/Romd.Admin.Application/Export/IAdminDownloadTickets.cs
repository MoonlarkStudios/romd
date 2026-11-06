namespace Romd.Admin.Application.Export;

public sealed record AdminDownloadPlan(string Filename, IReadOnlyList<ExportArchiveEntry> Entries, bool IsArchive,
    string? CompletedExportPath = null);
public sealed record AdminDownloadTicket(string DownloadUrl, DateTimeOffset ExpiresAt);

public interface IAdminDownloadTickets
{
    AdminDownloadTicket Issue(AdminDownloadPlan plan);
    AdminDownloadPlan? Resolve(string token);
}
