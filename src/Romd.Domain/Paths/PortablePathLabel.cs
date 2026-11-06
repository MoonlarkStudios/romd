using System.Text;

namespace Romd.Domain.Paths;

/// <summary>Bounded display labels for generated folders; original companion paths use validation instead.</summary>
public static class PortablePathLabel
{
    public static string Create(string sourceName, string fallback)
    {
        var label = new StringBuilder();
        int bytes = 0;
        foreach (Rune rune in sourceName.EnumerateRunes().Take(80))
        {
            if (bytes + rune.Utf8SequenceLength > 200)
                break;
            bytes += rune.Utf8SequenceLength;
            string text = rune.ToString();
            label.Append(text.Any(PortableRelativePath.IsUnsafeCharacter) ? "_" : text);
        }
        return label.ToString().TrimEnd(' ', '.') is { Length: > 0 } name ? name : fallback;
    }
}
