using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Generation;

/// <summary>Checks and fingerprints the complete builtin header tree exported from the pinned Clang image.</summary>
internal static class ClangResources
{
    internal static ImmutableArray<string> Arguments(string directory) => ["--resource-directory", directory];

    internal static Result<JsonObject> Verify(string root, string directory)
    {
        Result<string> validated = ArtifactsPath.ValidateDirectory(directory, root);
        if (!validated.Succeeded)
            return validated.Failure;
        string include = Path.Combine(validated.Value, "include");
        if (!Directory.Exists(include) || ArtifactsPath.IsLink(include))
            return new Failure("Missing or redirected Clang include directory");
        foreach (string name in (string[])["stddef.h", "stdarg.h", "stdint.h"])
        {
            string path = Path.Combine(include, name);
            if (ArtifactsPath.IsLink(path) || !ArtifactsPath.IsRegularFile(path) || new FileInfo(path).Length == 0)
                return new Failure("Missing, empty or nonregular Clang builtin header: " + name);
        }
        var inventory = new JsonObject();
        var pending = new Stack<string>();
        pending.Push(include);
        while (pending.TryPop(out string? current))
            foreach (string path in Directory.EnumerateFileSystemEntries(current).Order(StringComparer.Ordinal))
            {
                if (ArtifactsPath.IsLink(path))
                    return new Failure("Clang resource tree contains a symlink: " + path);
                if (Directory.Exists(path))
                    pending.Push(path);
                else
                {
                    if (!ArtifactsPath.IsRegularFile(path))
                        return new Failure("Nonregular Clang resource: " + path);
                    inventory[Path.GetRelativePath(validated.Value, path).Replace(Path.DirectorySeparatorChar, '/')] = Digest.Sha256File(path);
                }
            }
        return inventory;
    }
}
