using ErrorOr;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Preparation;

namespace Romd.Infrastructure.Jobs;

/// <summary>Shared byte operations; legacy creators retain their existing database and cleanup ownership.</summary>
internal static class WorkspaceInputCopies
{
    internal static string DirectoryFor(string dataDirectory, Guid jobId) =>
        Path.Combine(dataDirectory, "temp", "jobs", jobId.ToString("N"));

    internal static async Task<ErrorOr<Success>> CopyUploadAsync(
        string dataDirectory, JobWorkspaceInput input, Stream source, CancellationToken ct)
    {
        string workDirectory = DirectoryFor(dataDirectory, input.JobId);
        // Only the owner that checked authoritative replay under the workspace lock may replace these bytes.
        if (Directory.Exists(workDirectory))
            Directory.Delete(workDirectory, recursive: true);
        Directory.CreateDirectory(workDirectory);
        string filePath = Path.Combine(workDirectory, input.FileName);
        try
        {
            long available = new DriveInfo(workDirectory).AvailableFreeSpace;
            if (source.CanSeek && source.Length > available - (1L << 30))
            {
                Directory.Delete(workDirectory, recursive: true);
                return UploadErrors.InsufficientStorage();
            }

            await CopyToOwnedAsync(source, File.Create(filePath), ct);
        }
        catch
        {
            try
            {
                Directory.Delete(workDirectory, recursive: true);
            }
            catch
            {
                // Cleanup cannot replace the transfer failure or its exact cancellation token.
            }
            throw;
        }
        return Result.Success;
    }

    internal static async Task CopyReplacementAsync(
        string dataDirectory, JobWorkspaceInput input, Stream source, CancellationToken ct)
    {
        string workDirectory = DirectoryFor(dataDirectory, input.JobId);
        Directory.CreateDirectory(workDirectory);
        string filePath = Path.Combine(workDirectory, input.FileName);
        await CopyToOwnedAsync(source, File.Create(filePath), ct);
    }

    /// <summary>Owns only the destination; a transfer failure remains primary through destination disposal.</summary>
    internal static async Task CopyToOwnedAsync(Stream source, Stream target, CancellationToken ct)
    {
        Exception? primary = null;
        try
        {
            await source.CopyToAsync(target, ct);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            try
            {
                await target.DisposeAsync();
            }
            catch when (primary is not null)
            {
                // The destination's cleanup failure cannot mask the transfer cause or cancellation token.
            }
        }
    }

    internal static void ValidateInput(JobWorkspaceInput input)
    {
        if (string.IsNullOrWhiteSpace(input.FileName) || input.FileName is "." or ".."
            || input.FileName != Path.GetFileName(input.FileName.Replace('\\', '/')))
            throw new ArgumentException("A valid filename is required.", nameof(input));
    }
}
