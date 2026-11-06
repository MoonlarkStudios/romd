using System.Text;

namespace Romd.Domain.Paths;

/// <summary>An original portable relative path. Validation never renames companion files.</summary>
public readonly record struct PortableRelativePath
{
    public const int MaximumDepth = 32;
    public const int MaximumPathBytes = 4096;
    public string Value { get; }
    private PortableRelativePath(string value) => Value = value;

    public static bool TryCreate(string value, out PortableRelativePath path)
    {
        path = default;
        if (!IsSafe(value))
            return false;
        path = new(value);
        return true;
    }

    public static bool IsSafe(string path)
    {
        if (string.IsNullOrEmpty(path) || Encoding.UTF8.GetByteCount(path) > MaximumPathBytes)
        {
            return false;
        }

        try
        {
            // Normalize also rejects malformed UTF-16. Do not change the exposed spelling.
            _ = path.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return false;
        }

        string[] segments = path.Split('/');
        return segments.Length <= MaximumDepth && segments.All(segment =>
            segment.Length != 0 && segment is not "." and not ".." &&
            segment[^1] is not ' ' and not '.' && Encoding.UTF8.GetByteCount(segment) <= 255 &&
            !segment.Any(IsUnsafeCharacter) && !IsDeviceName(segment));
    }

    public static bool IsUnsafeCharacter(char character) =>
        char.IsControl(character) || "/\\<>:\"|?*".Contains(character) || character is '\ufffe' or '\uffff';

    public static bool IsDeviceName(string segment)
    {
        string stem = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
               (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                                     stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                "123456789¹²³".Contains(stem[3]));
    }
}
