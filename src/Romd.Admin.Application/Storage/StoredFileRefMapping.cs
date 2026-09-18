using Models = Romd.Contracts.Management.Models;
using Romd.Contracts.Common.Models;

namespace Romd.Admin.Application.Storage;

/// <summary>
///     Composes the nested <see cref="Models.StoredFileRef" /> from the flat CAS fields carried by
///     read models. Returns null when the backing blob is absent (sizes unresolved).
/// </summary>
public static class StoredFileRefMapping
{
    public static Models.StoredFileRef? ToStoredFileRef(long? sizeBytes, long? sizeOnDiskBytes, bool? isCompressed) =>
        sizeBytes is { } size && sizeOnDiskBytes is { } sizeOnDisk
            ? new Models.StoredFileRef
            {
                SizeBytes = (ByteCount)size,
                SizeOnDiskBytes = (ByteCount)sizeOnDisk,
                IsCompressed = isCompressed ?? false
            }
            : null;
}
