namespace Romd.Infrastructure.Storage;

internal static class BoundedRuntimeFile
{
    internal static byte[] Read(string path, int maximum)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length is <= 0 || file.Length > maximum)
            throw new InvalidDataException("Runtime custody file is oversized or empty.");
        byte[] bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1)
            throw new InvalidDataException("Runtime custody file changed or exceeded its bound.");
        return bytes;
    }
}
