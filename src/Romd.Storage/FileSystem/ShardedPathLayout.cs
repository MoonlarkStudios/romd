using Romd.Domain.Hashing;

namespace Romd.Storage.FileSystem;

internal static class ShardedPathLayout
{
    /// <summary>
    ///     Generates sharded path: ab/cd/abcd1234...
    ///     Two levels of sharding keeps directory sizes manageable.
    /// </summary>
    public static string GetRelativePath(StorageKey key)
    {
        string hex = key.Hash.ToString();
        return string.Create(hex.Length + 6, hex, static (span, h) =>
        {
            h.AsSpan(0, 2).CopyTo(span);
            span[2] = '/';
            h.AsSpan(2, 2).CopyTo(span[3..]);
            span[5] = '/';
            h.AsSpan().CopyTo(span[6..]);
        });
    }

    /// <summary>
    ///     Extracts storage key from a file path (for enumeration/recovery).
    /// </summary>
    public static bool TryParseFromPath(ReadOnlySpan<char> path, out StorageKey key)
    {
        key = default;

        int lastSlash = path.LastIndexOf('/');
        if (lastSlash < 0)
        {
            return false;
        }

        var filename = path[(lastSlash + 1)..];
        int dot = filename.LastIndexOf('.');
        var hashPart = dot > 0 ? filename[..dot] : filename;

        if (!HexConverter.TryParse<Sha256>(hashPart, out var hash))
        {
            return false;
        }

        key = StorageKey.FromHash(hash);
        return true;
    }
}
