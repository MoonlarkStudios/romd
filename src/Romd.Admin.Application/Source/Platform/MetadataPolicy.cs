using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Romd.Admin.Application.Source.Platform;

public static class MetadataPolicy
{
    public static readonly IReadOnlySet<string> Fields = new HashSet<string>(StringComparer.Ordinal)
    {
        "Description", "Genre", "Publisher", "Developer", "ReleaseDate", "Players", "Rating"
    };

    public static string Revision(IReadOnlyDictionary<string, string> defaults) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(defaults.OrderBy(pair => pair.Key, StringComparer.Ordinal)))));
}
