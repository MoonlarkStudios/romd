using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Native;

/// <summary>Writes the build-info header compiled into the shim and reads the export back from the actual library.</summary>
internal static class NativeBuildInfo
{
    /// <summary>The ABI limit, including the terminating NUL.</summary>
    internal const int MaximumBytes = 16384;

    private const string Placeholder = "#error \"Placeholder build-info: native build writes the real header after configure\"\n";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr BuildInfoExport();

    /// <summary>Configure needs the header to exist; compiling it before the recipe is known fails loudly.</summary>
    internal static Failure? WritePlaceholder(string path) => WriteHeader(path, Placeholder);

    internal static Failure? Write(string path, JsonNode info)
    {
        byte[] payload = CanonicalJson.Serialize(info);
        if (Check.That(payload.Length + 1 <= MaximumBytes, "Build-info exceeds 16 KiB including terminator") is { } size)
            return size;
        var literal = new StringBuilder("\"", payload.Length + 2);
        foreach (byte value in payload)
        {
            if (value is < 0x20 or > 0x7e)
                return new Failure("Build-info must be printable ASCII");
            // Canonical JSON is ASCII; escaping quote, backslash and '?' (trigraphs) yields the identical C string.
            if (value is (byte)'"' or (byte)'\\' or (byte)'?')
                literal.Append('\\');
            literal.Append((char)value);
        }
        return WriteHeader(path, "#define MOONLARK_CHDR_BUILD_INFO_JSON " + literal.Append('"') + "\n");
    }

    /// <summary>Loads the library in-process; call only after digest, filename, architecture and export checks.</summary>
    internal static Result<JsonObject> ReadFromLibrary(string binary)
    {
        IntPtr library;
        try
        {
            library = NativeLibrary.Load(Path.GetFullPath(binary));
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException)
        {
            return new Failure("Native binary could not be loaded: " + exception.Message);
        }
        try
        {
            if (!NativeLibrary.TryGetExport(library, ExportInventory.BuildInfoExport, out IntPtr export))
                return new Failure("Native binary has no build-info export");
            BuildInfoExport function = Marshal.GetDelegateForFunctionPointer<BuildInfoExport>(export);
            return Read(() => function());
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    /// <summary>Reads at most the ABI limit, never past the terminator, and requires the same pointer on a second call.</summary>
    internal static Result<JsonObject> Read(Func<IntPtr> export)
    {
        IntPtr address = export();
        if (address == IntPtr.Zero)
            return new Failure("Native build-info returned NULL");
        byte[] payload = new byte[MaximumBytes];
        for (int index = 0; index < MaximumBytes; index++)
        {
            payload[index] = Marshal.ReadByte(address, index);
            if (payload[index] != 0)
                continue;
            if (Check.That(export() == address, "Native build-info pointer is not immutable") is { } mutable)
                return mutable;
            return JsonFields.ParseObject(payload.AsSpan(0, index), "Native build-info");
        }
        return new Failure("Native build-info exceeds 16 KiB including terminator");
    }

    private static Failure? WriteHeader(string path, string content)
    {
        if (ArtifactsPath.IsLink(path))
            return new Failure("Generated build-info must not be a symlink");
        File.WriteAllText(path, content, Encoding.ASCII);
        return null;
    }
}
