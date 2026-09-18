using Microsoft.Extensions.Options;
using Romd.Admin.Application.Diagnostics;
using Romd.Application.Common.Configuration;
using Romd.Storage;

namespace Romd.Infrastructure.Diagnostics;

public sealed class StorageDiagnosticsReader(
    IRomdOptions romdOptions,
    IOptions<ContentStoreOptions> contentStoreOptions,
    IStorageDiagnosticsProbe probe) : IStorageDiagnosticsReader
{
    public async Task<StorageDiagnosticsData> ReadAsync(CancellationToken ct = default)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();

        bool dataAvailable = false;
        bool casAvailable = false;
        long? freeBytes = null;
        var errors = new List<string>(2);

        try
        {
            freeBytes = probe.GetAvailableFreeSpace(romdOptions.DataDirectory);
            dataAvailable = probe.DirectoryExists(romdOptions.DataDirectory);
            if (!dataAvailable)
            {
                errors.Add("The data directory does not exist.");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Data volume: {ex.Message}");
        }

        try
        {
            string casRoot = contentStoreOptions.Value.RootPath;
            casAvailable = probe.DirectoryExists(casRoot);
            if (casAvailable)
            {
                probe.AssertDirectoryReadable(casRoot);
            }
            else
            {
                errors.Add("The content-addressable storage root does not exist.");
            }
        }
        catch (Exception ex)
        {
            casAvailable = false;
            errors.Add($"Content storage: {ex.Message}");
        }

        return new StorageDiagnosticsData(
            dataAvailable,
            freeBytes,
            casAvailable,
            errors.Count == 0 ? null : string.Join(" ", errors));
    }
}
