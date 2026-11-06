using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Chdman;

/// <summary>Captures the complete explicitly supplied SDL include tree without following links or adding library inputs.</summary>
internal static class ChdmanSdlHeaders
{
    internal static Result<JsonObject?> Capture(string? includeRoot, string rid)
    {
        if (includeRoot is null)
            return (JsonObject?)null;
        if (rid is not ("linux-x64" or "linux-arm64"))
            return new Failure("Explicit SDL header inputs are supported only on Linux");
        Result<JsonObject> captured = CaptureTree(includeRoot);
        return captured.Succeeded ? captured.Value : captured.Failure;
    }

    internal static Failure? Verify(JsonObject captured)
    {
        if (captured["includeRoot"] is not JsonValue rootNode || !rootNode.TryGetValue(out string? includeRoot) || includeRoot is null
            || captured["directories"] is not JsonArray directories || captured["inputs"] is not JsonArray inputs)
            return new Failure("Malformed SDL header input capture");
        Result<JsonObject> current = CaptureTree(includeRoot);
        if (!current.Succeeded)
            return new Failure("SDL header inputs changed before receipt: " + current.Failure.Message);
        JsonArray currentInputs = current.Value["inputs"]!.AsArray();
        if (!JsonNode.DeepEquals(directories, current.Value["directories"])
            || !inputs.Select(node => node!["path"]!.GetValue<string>()).SequenceEqual(
                currentInputs.Select(node => node!["path"]!.GetValue<string>()), StringComparer.Ordinal))
            return new Failure("SDL header source set changed before receipt");
        return ChdmanBuild.VerifyInputs(inputs) is { } changed
            ? new Failure("SDL header inputs changed before receipt: " + changed.Message) : null;
    }

    private static Result<JsonObject> CaptureTree(string includeRoot)
    {
        try
        {
            if (!IsSafeRoot(includeRoot) || Path.GetFullPath(includeRoot) != includeRoot || !Directory.Exists(includeRoot))
                return new Failure("SDL include root must be a canonical absolute safe directory");
            string current = Path.GetPathRoot(includeRoot)!;
            foreach (string component in includeRoot[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                if (ArtifactsPath.IsLink(current))
                    return new Failure("SDL include path cannot contain a linked directory: " + current);
            }
            foreach (string required in new[] { "SDL.h", "SDL_config.h" })
                if (!File.Exists(Path.Combine(includeRoot, "SDL2", required)))
                    return new Failure("SDL include tree is missing required header: " + required);

            var files = new List<string>();
            var directories = new List<string>();
            var pending = new Stack<string>();
            pending.Push(includeRoot);
            while (pending.TryPop(out string? directory))
            {
                if (ArtifactsPath.IsLink(directory))
                    return new Failure("SDL include tree cannot contain a linked directory: " + directory);
                directories.Add(directory);
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
                {
                    if (ArtifactsPath.IsLink(entry))
                        return new Failure("SDL include tree cannot contain a linked input: " + entry);
                    if (Directory.Exists(entry))
                        pending.Push(entry);
                    else if (ArtifactsPath.IsRegularFile(entry))
                        files.Add(entry);
                    else
                        return new Failure("SDL include tree requires regular inputs: " + entry);
                }
            }
            Result<JsonArray> inputs = ChdmanBuild.CaptureInputs(files);
            if (!inputs.Succeeded)
                return inputs.Failure;
            return new JsonObject
            {
                ["includeRoot"] = includeRoot,
                ["directories"] = new JsonArray([.. directories.Order(StringComparer.Ordinal).Select(path => JsonValue.Create(path))]),
                ["inputs"] = inputs.Value,
            };
        }
        catch (IOException error)
        {
            return new Failure("SDL header input capture failed: " + error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            return new Failure("SDL header input capture failed: " + error.Message);
        }
    }

    // Filesystem probes and pure managed tests use canonical paths native to their host.
    // Actual builds select a native Linux RID first, so Windows syntax cannot enter Linux Make.
    private static bool IsSafeRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            return false;
        string volume = Path.GetPathRoot(path)!;
        return volume.All(character => IsPathCharacter(character) || OperatingSystem.IsWindows() && character == ':')
            && path[volume.Length..].All(IsPathCharacter);
    }

    private static bool IsPathCharacter(char character) => char.IsAsciiLetterOrDigit(character)
        || character is '_' or '-' or '.' or '~' || character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar;
}
