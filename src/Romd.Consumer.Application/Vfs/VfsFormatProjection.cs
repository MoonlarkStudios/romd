using System.Text;
using ErrorOr;
using Romd.Application.Common.Content;
using Romd.Domain.Paths;
using Romd.Domain.Source.Rom;
using Romd.Domain.Vfs;

namespace Romd.Consumer.Application.Vfs;

/// <summary>Only recorded availability and complete release paths participate in namespace selection.</summary>
public sealed record VfsReleaseMember(string SourcePath, bool OriginalIsRvz, bool RvzAvailable,
    bool OriginalIsChd = false, bool ChdAvailable = false);

public sealed record VfsProjectedMember(string SourcePath, string RelativePath, RomDeliveryFormat Format);

public static class VfsFormatProjection
{
    public static ErrorOr<IReadOnlyList<VfsProjectedMember>> Project(IReadOnlyList<VfsReleaseMember> members,
        VfsFormat format, RomDeliveryPolicy policy, string? systemKey)
    {
        if (!VfsFormats.IsValid(format))
            return VfsConnectionErrors.InvalidFormat();
        var formats = policy.Formats;
        var stored = policy.For(systemKey);
        var originals = members.Select(member => new VfsProjectedMember(member.SourcePath,
            member.OriginalIsRvz && policy.OriginalExtension(RomRepresentationEncoding.Rvz) is { } rvzOriginal &&
            Extension(member.SourcePath).Equals(formats.FileExtension(RomDeliveryFormat.Rvz), StringComparison.OrdinalIgnoreCase)
                ? ReplaceExtension(member.SourcePath, rvzOriginal)
                : member.OriginalIsChd && policy.OriginalExtension(RomRepresentationEncoding.Chd) is { } chdOriginal &&
                  Extension(member.SourcePath).Equals(formats.FileExtension(RomDeliveryFormat.Chd), StringComparison.OrdinalIgnoreCase)
                    ? ReplaceExtension(member.SourcePath, chdOriginal)
                    : member.SourcePath,
            RomDeliveryFormat.Original)).ToArray();
        // Invalid original sets remain visible to the layout's final rejection, never repaired by renaming.
        // Stored projects a member only when the platform's runtimes read its stored format natively.
        if (format == VfsFormat.Original || stored is not { NativeRuntimes.Count: > 0 } ||
            members.Any(member => IsDescriptor(member.SourcePath)) ||
            PortablePathSet.Validate(originals.Select(member => member.RelativePath)) is not null)
            return originals;

        string extension = formats.FileExtension(stored.Format);
        var projected = originals.Select((original, index) => IsAvailable(members[index], stored.Format)
            ? original with { RelativePath = ReplaceExtension(original.RelativePath, extension), Format = stored.Format }
            : original).ToArray();
        while (true)
        {
            var aliases = projected.Select((member, index) => (Path: member.RelativePath.Normalize(NormalizationForm.FormC), Index: index))
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Select(item => item.Index).ToArray(), StringComparer.OrdinalIgnoreCase);
            var collisions = new HashSet<int>();
            foreach (int[] duplicate in aliases.Values.Where(indices => indices.Length > 1))
                collisions.UnionWith(duplicate);
            for (int index = 0; index < projected.Length; index++)
            {
                string path = projected[index].RelativePath.Normalize(NormalizationForm.FormC);
                int separator = path.LastIndexOf('/');
                while (separator >= 0)
                {
                    path = path[..separator];
                    if (aliases.TryGetValue(path, out var directoryFile))
                    {
                        collisions.Add(index);
                        collisions.UnionWith(directoryFile);
                    }
                    separator = path.LastIndexOf('/');
                }
            }

            bool changed = false;
            foreach (int index in collisions)
            {
                if (projected[index].Format == RomDeliveryFormat.Original)
                    continue;
                projected[index] = originals[index];
                changed = true;
            }
            if (!changed)
                return projected;
        }
    }

    private static bool IsAvailable(VfsReleaseMember member, RomDeliveryFormat format) => format switch
    {
        RomDeliveryFormat.Rvz => member.RvzAvailable,
        RomDeliveryFormat.Chd => member.ChdAvailable,
        _ => false
    };

    private static bool IsDescriptor(string path) => Extension(path).ToLowerInvariant() is
        ".cue" or ".gdi" or ".toc" or ".ccd" or ".m3u" or ".m3u8" or ".mds" or ".pls";

    private static string Extension(string path)
    {
        int dot = path.LastIndexOf('.');
        return dot > path.LastIndexOf('/') ? path[dot..] : "";
    }

    private static string ReplaceExtension(string path, string extension) =>
        path[..(path.Length - Extension(path).Length)] + extension;
}
