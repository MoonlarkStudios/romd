using System.Runtime.InteropServices;
using System.Text.Json;

namespace Moonlark.Libchdr.Internal;

internal static class NativeBuildInfoReader
{
    internal static unsafe LibchdrBuildInfo Read(nint library)
    {
        foreach (string name in NativeBuildContract.Exports)
            if (!NativeLibrary.TryGetExport(library, name, out _))
                throw new BadImageFormatException($"Native libchdr is missing the paired export '{name}'.");
        nint export;
        try
        { export = NativeLibrary.GetExport(library, "moonlark_chdr_build_info"); }
        catch (EntryPointNotFoundException error) { throw new BadImageFormatException("Native libchdr has no build-info export.", error); }
        byte* bytes = ((delegate* unmanaged[Cdecl]<byte*>)export)();
        if (bytes == null)
            throw new BadImageFormatException("Native libchdr returned null build information.");
        int length = 0;
        while (length < 16384 && bytes[length] != 0)
            length++;
        if (length == 16384)
            throw new BadImageFormatException("Native libchdr build information exceeds the 16 KiB ABI limit.");
        return Parse(new ReadOnlySpan<byte>(bytes, length));
    }

    internal static LibchdrBuildInfo Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray());
            JsonElement value = document.RootElement;
            RequireUniqueObject(value);
            if (value.GetProperty("schemaVersion").GetInt32() != 1 || value.GetProperty("abiVersion").GetInt32() != 1)
                throw new BadImageFormatException("Native libchdr build schema or ABI version is incompatible.");
            Require(value, "managedVersion", NativeBuildContract.FamilyVersion);
            Require(value, "nativeVersion", NativeBuildContract.FamilyVersion);
            Require(value, "upstreamVersion", NativeBuildContract.UpstreamVersion);
            Require(value, "upstreamCommit", NativeBuildContract.UpstreamCommit);
            JsonElement headers = value.GetProperty("headers");
            RequireUniqueObject(headers);
            Require(headers, "include/libchdr/chd.h", NativeBuildContract.ChdHeaderSha256);
            Require(headers, "include/libchdr/coretypes.h", NativeBuildContract.CoreTypesSha256);
            var features = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement feature in value.GetProperty("features").EnumerateArray())
                if (feature.GetString() is not { } name || !features.Add(name))
                    throw new BadImageFormatException("Native libchdr features are invalid or duplicated.");
            if (!features.SetEquals(["raw-sectors", "subcode", "block-crc"]))
                throw new BadImageFormatException("Native libchdr must enable raw sectors, subcode and block CRC.");
            string buildId = value.GetProperty("buildId").GetString() ?? "";
            if (buildId.Length != 64 || !IsLowerHex(buildId))
                throw new BadImageFormatException("Native libchdr build identity is not a SHA-256 value.");
            return new(NativeBuildContract.FamilyVersion, NativeBuildContract.UpstreamVersion, NativeBuildContract.UpstreamCommit, buildId);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new BadImageFormatException("Native libchdr build information is malformed or incomplete.", error); }
    }

    private static bool IsLowerHex(string value)
    {
        foreach (char character in value)
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        return true;
    }

    private static void Require(JsonElement value, string name, string expected)
    {
        if (value.GetProperty(name).GetString() != expected)
            throw new BadImageFormatException($"Native libchdr {name} differs from the paired managed package.");
    }

    private static void RequireUniqueObject(JsonElement value)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            if (!names.Add(property.Name))
                throw new BadImageFormatException($"Duplicate native build-info property '{property.Name}'.");
    }
}
