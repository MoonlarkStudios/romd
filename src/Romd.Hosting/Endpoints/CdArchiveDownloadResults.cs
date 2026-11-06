using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;

namespace Romd.Host.Endpoints;

internal static class CdArchiveDownloadResults
{
    internal static async Task<IResult> CreateAsync(RomCdArchiveDownload target, IRomCdArchiveDownloadReader reader,
        HttpContext context, bool cue, CancellationToken ct)
    {
        var member = cue ? ((RomReconstructionManifest.CdTracks)target.Representation.Manifest).Members.OfType<RomCdStoredMember>().Single(file => file.Role == RomCdStoredRole.Cue) : null;
        var stream = cue ? await reader.OpenCueAsync(target, ct) : await reader.OpenAsync(target, ct);
        if (stream is null)
            return ProblemResults.Problem(
                StatusCodes.Status404NotFound,
                ProblemCodes.DeliveryContentNotFound,
                "The CD archive content is unavailable.");
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers["X-Content-SHA256"] = (member?.Identity.Sha256 ?? target.Representation.ArtifactSha256).ToString();
        context.Response.ContentLength = member?.Identity.Size ?? target.Representation.ArtifactSize;
        return Results.File(stream, "application/octet-stream", member is null ? target.Filename : Path.GetFileName(member.Path));
    }
}
