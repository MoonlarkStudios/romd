using Microsoft.Extensions.Logging;

namespace Romd.Admin.Application.Titles.Commands.UploadTitleMedia;

/// <summary>Releases upload preparation without replacing its rejection, primary failure or acknowledged result.</summary>
internal static class MediaUploadCleanup
{
    public static async ValueTask DisposeAsync(IAsyncDisposable? resource, ILogger logger, int titleId)
    {
        if (resource is null)
            return;
        try
        {
            await resource.DisposeAsync();
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Title media preparation cleanup failed for title {TitleId}", titleId);
        }
    }
}
