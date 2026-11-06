using Romd.Application.Common.Content;
using Romd.Consumer.Application.Vfs;
using Romd.Storage;

namespace Romd.Infrastructure.Storage;

public sealed class ConsumerVfsContentResolver(IContentAddressableStore cas, IRomContentReader? roms = null) : IConsumerVfsContentResolver
{
    public async Task<Stream?> OpenAsync(ConsumerVfsFile file, CancellationToken ct = default)
    {
        if (file.Target is { } target && (!RomDeliveryTargets.IsValid(target)
                || target.Sha256 != file.Sha256 || target.SizeBytes != file.Length))
            throw new InvalidDataException("VFS metadata disagrees with its bound delivery target.");
        if (file.Target is not null && roms is null)
            return null;
        if (roms is not null)
        {
            var stream = file.Target is { } bound ? await roms.OpenSeekableAsync(bound, ct)
                : await roms.OpenAsync(file.Sha256, ct);
            if (stream is null)
                return null;
            if (stream.Length == file.Length)
                return stream;
            await stream.DisposeAsync();
            throw new InvalidDataException("VFS content length disagrees with delivered-byte metadata.");
        }
        var reader = await cas.OpenRandomAccessAsync(StorageKey.FromHash(file.Sha256), ct);
        if (reader is null)
            return null;
        if (reader.Length != file.Length)
        {
            await reader.DisposeAsync();
            throw new InvalidDataException("VFS content length disagrees with catalog metadata.");
        }

        return reader.AsStream();
    }
}
