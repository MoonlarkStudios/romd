using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Moonlark.Native.Engineering.Core;

internal static class Digest
{
    internal static string Sha256File(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Git object identity; supply-chain digests use SHA-256.</summary>
    internal static string GitBlobSha1(ReadOnlySpan<byte> data)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes("blob " + data.Length.ToString(CultureInfo.InvariantCulture) + "\0"));
        hash.AppendData(data);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
