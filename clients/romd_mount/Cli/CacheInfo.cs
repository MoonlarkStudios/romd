namespace Romd.Mount.Cli;

internal sealed record CacheInfo(string Path, long LogicalBytes, long Files, long Directories, long? AllocatedFileBytes)
{
    // Called with both profile/credential leases held and no mount reservation.
    internal static CacheInfo Read(string stateDirectory, string key, CancellationToken ct,
        int maximumEntries = 100_000, int maximumDepth = 64, string? generation = null)
    {
        if (key.Length != 64 || key.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Invalid cache key.");
        if (generation is not null && (generation.Length != 64 || generation.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f'))))
            throw new ArgumentException("Invalid cache generation.");
        string parent = System.IO.Path.Combine(stateDirectory, "cache");
        string profileRoot = System.IO.Path.Combine(parent, key);
        string root = generation is null ? profileRoot : System.IO.Path.Combine(profileRoot, generation);
        ct.ThrowIfCancellationRequested();
        PrivateStateDirectory.Ensure(stateDirectory);
        if (!ExistsPrivate(parent) || !ExistsPrivate(profileRoot) ||
            (generation is not null && !ExistsPrivate(root))) return new(root, 0, 0, 0, OperatingSystem.IsMacOS() ? 0 : null);
        long bytes = 0, files = 0, directories = 0;
        long? allocated = OperatingSystem.IsMacOS() ? 0 : null;
        int entries = 0;
        Visit(root, 0);
        return new(root, bytes, files, directories, allocated);

        void Visit(string directory, int depth)
        {
            if (depth > maximumDepth) throw new IOException("Cache nesting exceeds the inspection limit.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > maximumEntries) throw new IOException("Cache entries exceed the inspection limit.");
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Cache inspection cannot follow symbolic links.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories++;
                    Visit(entry, depth + 1);
                }
                else
                {
                    files++;
                    if (OperatingSystem.IsMacOS())
                    {
                        var size = MacFileAllocation.Read(entry);
                        bytes = checked(bytes + size.Logical);
                        allocated = checked(allocated + size.Allocated);
                    }
                    else bytes = checked(bytes + new FileInfo(entry).Length);
                }
            }
        }
    }

    private static bool ExistsPrivate(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
            throw new IOException("Cache root must be an ordinary directory.");
        PrivateStateDirectory.Ensure(path);
        return true;
    }
}
