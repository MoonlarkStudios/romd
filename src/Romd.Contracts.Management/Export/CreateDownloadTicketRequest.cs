using System.Text.Json.Serialization;

namespace Romd.Contracts.Management.Export;

/// <summary>The kind of resource a download ticket downloads.</summary>
public enum DownloadTicketTarget
{
    Title,
    Release,
    Rom,
    ExportJob
}

/// <summary>
///     Issues a download ticket for one title, release, ROM file or completed export job. <see cref="Target" /> names the kind of
///     resource and <see cref="Id" /> its public id; <see cref="Format" /> selects the delivered format,
///     <c>original</c> when absent.
/// </summary>
public sealed record CreateDownloadTicketRequest(
    [property: JsonRequired] DownloadTicketTarget Target,
    [property: JsonRequired] string Id,
    string? Format = null);
