using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Native;

/// <summary>The validated output directory layout of one local native build.</summary>
internal sealed record OutputLayout(string Output, string Rid)
{
    internal string Build => Path.Combine(Output, NativeOutput.BuildDirectory);

    internal string Native => Path.Combine(Output, NativeOutput.NativeDirectory);

    internal string Generated => Path.Combine(Output, NativeOutput.GeneratedDirectory);

    internal string BuildInfoHeader => Path.Combine(Generated, "moonlark_chdr_build_info_json.h");

    internal string Binary => Path.Combine(Native, NativeRids.LibraryFileName(Rid));

    internal string Manifest => Path.Combine(Output, NativeOutput.ManifestName);

    internal string ProbeReceipt => Path.Combine(Output, LayoutProbe.ReceiptName);
}

internal static class NativeOutput
{
    internal const string ManifestName = "build-manifest.json";
    internal const string BuildDirectory = "build";
    internal const string NativeDirectory = "native";
    internal const string GeneratedDirectory = "generated";

    /// <summary>Invalidates old receipts before any source or tool check, so a failed rebuild never leaves a stale manifest.</summary>
    internal static Failure? Invalidate(OutputLayout layout)
    {
        DeleteFile(layout.Manifest);
        return LayoutProbe.Invalidate(layout.Output);
    }

    /// <summary>Refuses redirected children, then recreates the CMake and generated-header caches so no unrecorded input survives.</summary>
    internal static Failure? Prepare(string output)
    {
        Directory.CreateDirectory(output);
        foreach (string name in (string[])[BuildDirectory, NativeDirectory, GeneratedDirectory])
        {
            string path = Path.Combine(output, name);
            if (ArtifactsPath.IsLink(path))
                return new Failure("Output directory must not be a symlink: " + path);
            if (File.Exists(path))
                return new Failure("Output must be a directory: " + path);
        }
        foreach (string name in (string[])[BuildDirectory, GeneratedDirectory])
            if (Directory.Exists(Path.Combine(output, name)))
                Directory.Delete(Path.Combine(output, name), recursive: true);
        Directory.CreateDirectory(Path.Combine(output, GeneratedDirectory));
        Directory.CreateDirectory(Path.Combine(output, NativeDirectory));
        return null;
    }

    /// <summary>Deletes a file or a link itself, never a link's target; absence is not an error.</summary>
    internal static void DeleteFile(string path)
    {
        if (File.Exists(path) || ArtifactsPath.IsLink(path))
            File.Delete(path);
    }
}
