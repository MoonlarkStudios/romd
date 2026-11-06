using System.Collections.Immutable;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Chdman;

internal enum MemberKind
{
    File,
    Directory,
    SymbolicLink,
}

/// <summary>A validated archive member. <see cref="Path"/> and <see cref="LinkTarget"/> are normalized POSIX-relative paths.</summary>
internal sealed record ArchiveMember(string Name, string Path, MemberKind Kind, string LinkTarget, UnixFileMode Mode, DateTimeOffset ModificationTime);

internal sealed record ExtractedSource(string Source, long Epoch);

/// <summary>
/// Pinned-archive verification and Python <c>tarfile</c> <c>filter="data"</c> extraction semantics. Every member is
/// validated in a first pass before anything is written; the second pass extracts only the identical member sequence.
/// </summary>
internal static class SourceArchive
{
    private const UnixFileMode AnyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
    private const UnixFileMode DataModeMask = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    internal static Failure? Validate(string archive, MamePin pin)
    {
        if (RegularFile(archive) is { } kind)
            return kind;
        using FileStream stream = File.OpenRead(archive);
        return ValidateContent(stream, pin);
    }

    /// <summary>Extracts the pinned archive; the epoch is the first member's modification time.</summary>
    internal static Result<ExtractedSource> Extract(string archive, string destination, MamePin pin)
    {
        if (RegularFile(archive) is { } kind)
            return kind;
        if (Check.That(!Path.Exists(destination) && !ArtifactsPath.IsLink(destination), "Extraction destination must be absent") is { } present)
            return present;
        // One handle serves the digest and both passes, so a replaced path cannot substitute unverified bytes.
        using FileStream stream = File.OpenRead(archive);
        if (ValidateContent(stream, pin) is { } content)
            return content;
        stream.Position = 0;
        Result<ImmutableArray<ArchiveMember>> members;
        using (var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true))
        using (var reader = new TarReader(gzip))
            members = ValidateMembers(Entries(reader), pin.ArchivePrefix);
        if (!members.Succeeded)
            return members.Failure;
        if (Check.That(members.Value.Length > 0, "Archive has no members") is { } empty)
            return empty;
        stream.Position = 0;
        using (var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true))
        using (var reader = new TarReader(gzip))
            if (ExtractMembers(reader, destination, pin.ArchivePrefix, members.Value) is { } extraction)
                return extraction;
        return new ExtractedSource(Path.Combine(destination, pin.ArchivePrefix), members.Value[0].ModificationTime.ToUnixTimeSeconds());
    }

    /// <summary>Rejects unsafe paths, unsupported member types and escaping links before any extraction.</summary>
    internal static Result<ImmutableArray<ArchiveMember>> ValidateMembers(IEnumerable<TarEntry> entries, string prefix)
    {
        ImmutableArray<ArchiveMember>.Builder members = ImmutableArray.CreateBuilder<ArchiveMember>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (TarEntry entry in entries)
        {
            Result<ArchiveMember?> member = Describe(entry, prefix);
            if (!member.Succeeded)
                return member.Failure;
            if (member.Value is not { } described)
                continue;
            if (Check.That(paths.Add(described.Path), "Duplicate archive member: " + entry.Name) is { } duplicate)
                return duplicate;
            members.Add(described);
        }
        HashSet<string> links = members.Where(member => member.Kind == MemberKind.SymbolicLink).Select(member => member.Path).ToHashSet(StringComparer.Ordinal);
        ArchiveMember? beneath = members.FirstOrDefault(member => Ancestors(member.Path).Any(links.Contains));
        return beneath is null ? members.ToImmutable() : new Failure("Unsafe archive member: " + beneath.Name);
    }

    /// <summary>Python <c>filter="data"</c>: drop high and group/other write bits, keep execute only with user execute, ensure owner read/write.</summary>
    internal static UnixFileMode DataMode(UnixFileMode mode)
    {
        UnixFileMode filtered = mode & DataModeMask;
        if (!filtered.HasFlag(UnixFileMode.UserExecute))
            filtered &= ~AnyExecute;
        return filtered | UnixFileMode.UserRead | UnixFileMode.UserWrite;
    }

    private static Failure? RegularFile(string archive) =>
        Check.That(File.Exists(archive) && !ArtifactsPath.IsLink(archive), "Archive must be a regular file");

    private static Failure? ValidateContent(FileStream stream, MamePin pin)
    {
        if (Check.That(stream.Length == pin.SourceBytes, "Source archive size mismatch") is { } size)
            return size;
        return Check.That(Convert.ToHexStringLower(SHA256.HashData(stream)) == pin.SourceSha256, "Source archive digest mismatch");
    }

    private static IEnumerable<TarEntry> Entries(TarReader reader)
    {
        for (TarEntry? entry = reader.GetNextEntry(); entry is not null; entry = reader.GetNextEntry())
            yield return entry;
    }

    /// <summary>A pax global header is metadata, not a member, as in Python; only git's commit comment is accepted.</summary>
    private static Result<ArchiveMember?> Describe(TarEntry entry, string prefix)
    {
        if (entry is PaxGlobalExtendedAttributesTarEntry global)
        {
            if (Check.That(global.GlobalExtendedAttributes.Keys.All(key => key == "comment"), "Unsupported archive member: " + entry.Name) is { } attributes)
                return attributes;
            return (ArchiveMember?)null;
        }
        string[] parts = Parts(entry.Name);
        if (Check.That(!entry.Name.StartsWith('/') && !parts.Contains("..") && parts.Length > 0 && parts[0] == prefix,
            "Unsafe archive member: " + entry.Name) is { } unsafePath)
            return unsafePath;
        MemberKind? kind = entry.EntryType switch
        {
            TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile => MemberKind.File,
            TarEntryType.Directory => MemberKind.Directory,
            TarEntryType.SymbolicLink => MemberKind.SymbolicLink,
            _ => null,
        };
        if (kind is not { } supported)
            return new Failure("Unsupported archive member: " + entry.Name);
        string[] target = Parts(entry.LinkName);
        if (supported == MemberKind.SymbolicLink && Check.That(!entry.LinkName.StartsWith('/') && !target.Contains("..") && target.Length > 0,
            "Unsafe archive link: " + entry.Name) is { } unsafeLink)
            return unsafeLink;
        return new ArchiveMember(entry.Name, string.Join('/', parts), supported,
            supported == MemberKind.SymbolicLink ? string.Join('/', target) : "", entry.Mode, entry.ModificationTime);
    }

    private static Failure? ExtractMembers(TarReader reader, string destination, string prefix, ImmutableArray<ArchiveMember> members)
    {
        Directory.CreateDirectory(destination);
        int index = 0;
        foreach (TarEntry entry in Entries(reader))
        {
            Result<ArchiveMember?> member = Describe(entry, prefix);
            if (!member.Succeeded)
                return member.Failure;
            if (member.Value is not { } described)
                continue;
            if (Check.That(index < members.Length && described == members[index], "Archive changed between validation and extraction") is { } changed)
                return changed;
            Write(destination, described, entry.DataStream);
            index++;
        }
        if (Check.That(index == members.Length, "Archive changed between validation and extraction") is { } truncated)
            return truncated;
        // As in Python, directory times are applied last (deepest first) because creating children updates them.
        foreach (ArchiveMember directory in members.Where(member => member.Kind == MemberKind.Directory).OrderByDescending(member => member.Path, StringComparer.Ordinal))
            SetTimes(Target(destination, directory), directory.ModificationTime, isDirectory: true);
        return null;
    }

    private static void Write(string destination, ArchiveMember member, Stream? data)
    {
        string target = Target(destination, member);
        if (member.Kind == MemberKind.Directory)
        {
            Directory.CreateDirectory(target);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (member.Kind == MemberKind.SymbolicLink)
        {
            // Python sets neither mode nor times on symlinks.
            File.CreateSymbolicLink(target, member.LinkTarget);
            return;
        }
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
            data?.CopyTo(output);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(target, DataMode(member.Mode));
        SetTimes(target, member.ModificationTime, isDirectory: false);
    }

    private static void SetTimes(string path, DateTimeOffset time, bool isDirectory)
    {
        if (isDirectory)
        {
            Directory.SetLastWriteTimeUtc(path, time.UtcDateTime);
            Directory.SetLastAccessTimeUtc(path, time.UtcDateTime);
            return;
        }
        File.SetLastWriteTimeUtc(path, time.UtcDateTime);
        File.SetLastAccessTimeUtc(path, time.UtcDateTime);
    }

    private static string Target(string destination, ArchiveMember member) => Path.Combine([destination, .. member.Path.Split('/')]);

    /// <summary>Python <c>PurePosixPath.parts</c> for a relative path: empty and <c>.</c> components collapse.</summary>
    private static string[] Parts(string path) => path.Split('/').Where(part => part.Length > 0 && part != ".").ToArray();

    private static IEnumerable<string> Ancestors(string path)
    {
        for (int index = path.IndexOf('/', StringComparison.Ordinal); index >= 0; index = path.IndexOf('/', index + 1))
            yield return path[..index];
    }
}
