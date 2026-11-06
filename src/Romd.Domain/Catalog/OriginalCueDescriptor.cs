using System.Text;
using Romd.Domain.Paths;

namespace Romd.Domain.Catalog;

/// <summary>Dependency syntax only. Never rewrites original bytes or asserts CD integrity.</summary>
public sealed record OriginalCueDescriptor(IReadOnlyList<string> Dependencies, string? Issue)
{
    public const int MaximumBytes = 1024 * 1024;
    public const int MaximumDependencies = 100;

    public static OriginalCueDescriptor Parse(byte[] bytes, string entryPoint)
    {
        if (bytes.Length is 0 or > MaximumBytes || !PortableRelativePath.IsSafe(entryPoint)
            || !entryPoint.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
            return new([], "UnsupportedDescriptor");
        string text;
        try
        { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { return new([], "UnsupportedEncoding"); }
        if (text.Contains('\0'))
            return new([], "InvalidDescriptor");
        string directory = entryPoint.Contains('/') ? entryPoint[..(entryPoint.LastIndexOf('/') + 1)] : "";
        List<string> references = [];
        foreach (string line in text.Split('\n'))
        {
            string value = line.Trim();
            if (!value.StartsWith("FILE", StringComparison.OrdinalIgnoreCase))
                continue;
            if (value.Length < 6 || !char.IsWhiteSpace(value[4]))
                return new([], "InvalidFileReference");
            value = value[5..].TrimStart();
            string path;
            if (value.StartsWith('"'))
            {
                int end = value.IndexOf('"', 1);
                if (end <= 1 || string.IsNullOrWhiteSpace(value[(end + 1)..]))
                    return new([], "InvalidFileReference");
                path = value[1..end];
            }
            else
            {
                int end = value.IndexOfAny([' ', '\t']);
                if (end <= 0 || string.IsNullOrWhiteSpace(value[end..]))
                    return new([], "InvalidFileReference");
                path = value[..end];
            }
            if (!PortableRelativePath.IsSafe(path))
                return new([], "UnsafeDependencyPath");
            string resolved = directory + path;
            if (!references.Contains(resolved, StringComparer.Ordinal))
                references.Add(resolved);
            if (references.Count > MaximumDependencies)
                return new([], "TooManyDependencies");
        }
        string? issue = references.Count == 0 ? "MissingFileReferences" : PortablePathSet.Validate(references.Prepend(entryPoint));
        return new(references, issue);
    }
}
