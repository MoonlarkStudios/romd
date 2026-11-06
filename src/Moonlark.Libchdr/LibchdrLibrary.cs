using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Moonlark.Libchdr.Internal;
using Moonlark.Libchdr.Interop;

namespace Moonlark.Libchdr;

/// <summary>Loads and validates one process-resident native libchdr build.</summary>
/// <remarks>Native libraries are trusted executable code. Verify external binaries before loading.
/// A verified library is never freed: it stays resident for the life of the process and cannot be replaced or unloaded.
/// This package owns its assembly's DllImport resolver, through which every native call binds to the verified build:
/// do not register another resolver for it, and do not bind <c>moonlark_chdr</c> with NativeAOT <c>DirectPInvoke</c>,
/// which bypasses the resolver and its verification.</remarks>
public static class LibchdrLibrary
{
    private static readonly object Gate = new();
    private static ResidentLibrary? _resident;

    /// <summary>The validated identity, loading the packaged native asset on first use.</summary>
    public static LibchdrBuildInfo BuildInfo => EnsureLoaded().Info;

    /// <summary>Loads only the supplied absolute path and validates its exact family and ABI identity.</summary>
    /// <param name="absolutePath">A trusted native library file already verified by the host.</param>
    /// <exception cref="ArgumentException">The path is empty or not fully qualified.</exception>
    /// <exception cref="InvalidOperationException">A different path is already loaded.</exception>
    /// <exception cref="PlatformNotSupportedException">The process platform is unsupported.</exception>
    /// <exception cref="DllNotFoundException">The supplied file cannot be loaded.</exception>
    /// <exception cref="BadImageFormatException">The native build is incompatible.</exception>
    public static void Load(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (!Path.IsPathFullyQualified(absolutePath))
            throw new ArgumentException("The native library path must be fully qualified.", nameof(absolutePath));
        _ = NativePlatform.Current();
        string path = Path.GetFullPath(absolutePath);
        lock (Gate)
        {
            if (_resident is { } loaded)
            {
                if (!string.Equals(loaded.Path, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException($"libchdr is already loaded from '{loaded.Path}'; cannot load '{path}'.");
                return;
            }
            Volatile.Write(ref _resident, OpenLibrary(path));
        }
    }

    /// <summary>Registers the resolver, by running the NativeMethods type initializer, before loading the verified build;
    /// registration then never depends on when the runtime binds an import.</summary>
    internal static void EnsureInitialized()
    {
        RuntimeHelpers.RunClassConstructor(typeof(NativeMethods).TypeHandle);
        _ = EnsureLoaded();
    }

    /// <summary>Routes every raw import through the verified load; the NativeMethods type initializer calls this once.</summary>
    /// <exception cref="InvalidOperationException">The host already registered a resolver for this assembly.</exception>
    internal static void RegisterResolver()
    {
        try
        { NativeLibrary.SetDllImportResolver(typeof(LibchdrLibrary).Assembly, ResolveImport); }
        catch (InvalidOperationException error)
        {
            throw new InvalidOperationException(
                "Moonlark.Libchdr owns the DllImport resolver of its assembly, which binds native calls only to the verified " +
                "libchdr build, but another resolver is already registered for it. Native calls stay unavailable in this process; " +
                "remove the host's resolver, and supply a build with LibchdrLibrary.Load instead.", error);
        }
    }

    private static ResidentLibrary EnsureLoaded()
    {
        if (Volatile.Read(ref _resident) is { } loaded)
            return loaded;
        lock (Gate)
        {
            if (_resident is { } current)
                return current;
            (string rid, string filename) = NativePlatform.Current();
            foreach (string path in NativePlatform.AssetPaths(rid, filename))
            {
                if (!File.Exists(path))
                    continue;
                // An incompatible first asset fails; it never triggers a fallback search.
                ResidentLibrary resident = OpenLibrary(path);
                Volatile.Write(ref _resident, resident);
                return resident;
            }
            throw new DllNotFoundException($"Packaged libchdr asset '{filename}' for {rid} was not found in .NET native asset directories or '{AppContext.BaseDirectory}'.");
        }
    }

    private static ResidentLibrary OpenLibrary(string path)
    {
        nint handle = NativeLibrary.Load(path);
        try
        { return new(handle, NativeBuildInfoReader.Read(handle), path); }
        catch { NativeLibrary.Free(handle); throw; }
    }

    private static nint ResolveImport(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        name == "moonlark_chdr" ? EnsureLoaded().Handle : 0;

    private sealed record ResidentLibrary(nint Handle, LibchdrBuildInfo Info, string Path);
}
