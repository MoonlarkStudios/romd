using System.Text;
using Romd.Domain.Paths;

namespace Romd.Consumer.Application.Delivery;

/// <summary>Bounded original descriptor path validation, not CD/media verification or descriptor rewriting.</summary>
public static class CueDependencies
{
    public static bool Validate(byte[] bytes, string cuePath, IReadOnlyList<string> files)
    {
        string text;
        try
        { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { return false; }
        if (text.Contains('\0'))
            return false;
        string directory = cuePath.Contains('/') ? cuePath[..(cuePath.LastIndexOf('/') + 1)] : "";
        int references = 0;
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("FILE", StringComparison.OrdinalIgnoreCase))
                continue;
            if (trimmed.Length < 6 || !char.IsWhiteSpace(trimmed[4]))
                return false;
            string value = trimmed[5..].TrimStart();
            string path;
            if (value.StartsWith('"'))
            {
                int end = value.IndexOf('"', 1);
                if (end <= 1 || string.IsNullOrWhiteSpace(value[(end + 1)..]))
                    return false;
                path = value[1..end];
            }
            else
            {
                int end = value.IndexOfAny([' ', '\t']);
                if (end <= 0)
                    return false;
                path = value[..end];
            }
            // Preserve exact authored spelling; no traversal normalization or case/Unicode repair.
            if (!PortableRelativePath.IsSafe(path) || !files.Contains(directory + path, StringComparer.Ordinal) || directory + path == cuePath)
                return false;
            references++;
        }
        return references > 0;
    }
}
