using System.Security.Cryptography;
using System.Text;
using Romd.Domain.Paths;

namespace Romd.Domain.Catalog;

public sealed record DiscPlaylistLayout
{
    private DiscPlaylistLayout(IReadOnlyList<string> entryPoints)
    {
        EntryPoints = Array.AsReadOnly(entryPoints.ToArray());
        Text = string.Join('\n', EntryPoints) + "\n";
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)));
    }

    public string Text { get; }
    public string Sha256 { get; }
    public IReadOnlyList<string> EntryPoints { get; }

    internal static DiscPlaylistLayout Create(IReadOnlyList<string> entryPoints) => new(entryPoints);
}

public static class DiscPlaylist
{
    public static string DirectoryFor(int position) => $"disc-{position:D2}";

    public static bool TryCreate(IReadOnlyList<(IReadOnlyList<string> Files, string EntryPoint)> discs,
        out DiscPlaylistLayout? playlist, out string? error)
    {
        playlist = null;
        error = null;
        if (discs.Count is < 1 or > 99)
        { error = "A playlist requires 1–99 discs."; return false; }
        var paths = new List<string> { "playlist.m3u" };
        var entries = new List<string>();
        for (int i = 0; i < discs.Count; i++)
        {
            var disc = discs[i];
            if (PortablePathSet.Validate(disc.Files) is { } problem)
            { error = problem; return false; }
            if (!disc.Files.Contains(disc.EntryPoint, StringComparer.Ordinal))
            { error = "The disc entry point must be a delivered file."; return false; }
            string directory = DirectoryFor(i + 1);
            paths.AddRange(disc.Files.Select(path => directory + "/" + path));
            entries.Add(directory + "/" + disc.EntryPoint);
        }
        if (PortablePathSet.Validate(paths) is { } collision)
        { error = collision; return false; }
        playlist = DiscPlaylistLayout.Create(entries);
        return true;
    }
}
