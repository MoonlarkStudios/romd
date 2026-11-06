using System.Collections.Frozen;
using ErrorOr;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Paths;

namespace Romd.Consumer.Application.Vfs;

public sealed record ConsumerVfsFile(string RelativePath, Sha256 Sha256, long Length,
    Md5 Md5 = default, Sha1 Sha1 = default, DateTimeOffset? ModifiedAt = null, RomDeliveryTarget? Target = null);

public sealed record ConsumerVfsEntry(string RelativePath, ConsumerVfsFile? File)
{
    public bool IsDirectory => File is null;
    public string Name => RelativePath[(RelativePath.LastIndexOf('/') + 1)..];
}

/// <summary>
/// An immutable, bounded release directory snapshot. Paths are decoded relative paths,
/// not URLs or host filesystem paths. Building a tree does not authorize content access.
/// </summary>
public sealed class ConsumerVfsReleaseTree
{
    public const int MaximumFiles = PortablePathSet.MaximumFiles;
    public const int MaximumEntries = PortablePathSet.MaximumEntries;
    public const int MaximumDepth = PortableRelativePath.MaximumDepth;
    public const int MaximumPathBytes = PortableRelativePath.MaximumPathBytes;

    private readonly FrozenDictionary<string, ConsumerVfsEntry> _entries;
    private readonly FrozenDictionary<string, IReadOnlyList<ConsumerVfsEntry>> _children;

    private ConsumerVfsReleaseTree(Dictionary<string, ConsumerVfsEntry> entries)
    {
        _entries = entries.ToFrozenDictionary(StringComparer.Ordinal);
        var children = entries.Values.Where(entry => entry.IsDirectory)
            .ToDictionary(entry => entry.RelativePath, _ => new List<ConsumerVfsEntry>(), StringComparer.Ordinal);
        foreach (ConsumerVfsEntry entry in entries.Values.Where(entry => entry.RelativePath.Length != 0))
        {
            int separator = entry.RelativePath.LastIndexOf('/');
            children[separator < 0 ? "" : entry.RelativePath[..separator]].Add(entry);
        }

        _children = children.ToFrozenDictionary(pair => pair.Key,
            pair => (IReadOnlyList<ConsumerVfsEntry>)Array.AsReadOnly(pair.Value
                .OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray()), StringComparer.Ordinal);
    }

    public ConsumerVfsEntry? Find(string relativePath) => _entries.GetValueOrDefault(relativePath);

    /// <summary>Returns null for a missing path or a file; an empty list denotes an empty directory.</summary>
    public IReadOnlyList<ConsumerVfsEntry>? List(string relativePath) => _children.GetValueOrDefault(relativePath);

    /// <summary>
    /// Preserves every accepted filename. Rejects unsafe paths and aliases on common
    /// case-insensitive or Unicode-normalizing clients instead of silently renaming companions.
    /// Callers must supply projected portable paths, not paths sanitized for downloads.
    /// </summary>
    public static ErrorOr<ConsumerVfsReleaseTree> Create(IEnumerable<ConsumerVfsFile> files)
    {
        var bounded = files.Take(MaximumFiles + 1).ToArray();
        if (bounded.Any(file => file.Length < 0 || file.Sha256.IsEmpty) ||
            (bounded.Length != 0 && PortablePathSet.Validate(bounded.Select(file => file.RelativePath)) is not null))
            return ConsumerVfsErrors.InvalidLayout();
        var entries = new Dictionary<string, ConsumerVfsEntry>(StringComparer.Ordinal) { [""] = new("", null) };
        foreach (ConsumerVfsFile file in bounded)
        {
            string[] segments = file.RelativePath.Split('/');
            string path = "";
            for (int index = 0; index < segments.Length; index++)
            {
                path = index == 0 ? segments[index] : path + "/" + segments[index];
                entries.TryAdd(path, new(path, index == segments.Length - 1 ? file : null));
            }
        }
        return new ConsumerVfsReleaseTree(entries);
    }
}
