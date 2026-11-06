using System.Text;

namespace Romd.Domain.Paths;

/// <summary>Portable release-wide invariants, including directory/file and normalized aliases.</summary>
public static class PortablePathSet
{
    public const int MaximumFiles = 10_000;
    public const int MaximumEntries = 20_000;

    public static string? Validate(IEnumerable<string> paths)
    {
        var entries = new Dictionary<string, bool>(StringComparer.Ordinal) { [""] = false };
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (string original in paths)
        {
            if (++count > MaximumFiles)
                return "The release exceeds the portable file-count limit.";
            if (!PortableRelativePath.TryCreate(original, out var portable))
                return "The release contains an unsafe or unsupported original path.";
            string[] segments = portable.Value.Split('/');
            string path = "";
            for (int index = 0; index < segments.Length; index++)
            {
                path = index == 0 ? segments[index] : path + "/" + segments[index];
                string alias = path.Normalize(NormalizationForm.FormC);
                if (aliases.TryGetValue(alias, out string? existing) && existing != path)
                    return "Original paths conflict on case-insensitive or Unicode-normalizing filesystems.";
                aliases[alias] = path;
                bool isFile = index == segments.Length - 1;
                if (entries.TryGetValue(path, out bool wasFile))
                {
                    if (isFile || wasFile)
                        return "Original paths contain duplicate files or file/directory conflicts.";
                }
                else
                {
                    if (entries.Count >= MaximumEntries)
                        return "The release exceeds the portable directory-entry limit.";
                    entries.Add(path, isFile);
                }
            }
        }
        return count == 0 ? "The release contains no files." : null;
    }
}
