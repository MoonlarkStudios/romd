namespace Moonlark.Native.Engineering.Core;

internal static class RepositoryRoot
{
    internal const string Marker = "native/libchdr/libchdr.json";

    internal static Result<string> Find(string start)
    {
        for (DirectoryInfo? directory = new(Path.GetFullPath(start)); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, Marker)))
                return directory.FullName;
        return new Failure($"No repository root containing {Marker} above {start}");
    }
}
