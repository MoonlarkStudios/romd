namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>Commits one title's effective metadata using current layers and system defaults.</summary>
public interface IRematerializationService
{
    Task RematerializeTitleAsync(int titleId, CancellationToken ct = default);
}
