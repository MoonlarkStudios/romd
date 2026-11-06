using Romd.Media.PlayStation;

namespace Romd.Infrastructure.Storage;

/// <summary>The configured native build directories and their recorded identities; creation neither verifies nor loads.</summary>
/// <remarks>The caller owns immutable directory custody. Candidate verification remains mandatory before use.</remarks>
public sealed class ChdDeploymentAssets
{
    private ChdDeploymentAssets(ChdNativeCandidate native, ChdmanCandidate? tool)
    {
        Native = native;
        Tool = tool;
    }

    public ChdNativeCandidate Native { get; }
    public ChdmanCandidate? Tool { get; }

    /// <summary>Null keeps CHD off. Any partial, relative or blank configuration fails closed.</summary>
    public static ChdDeploymentAssets? Create(NativeToolOptions options, bool requireTool)
    {
        ArgumentNullException.ThrowIfNull(options);
        string? libchdr = Directory(options.LibchdrDirectory, nameof(NativeToolOptions.LibchdrDirectory));
        string? chdman = Directory(options.ChdmanDirectory, nameof(NativeToolOptions.ChdmanDirectory));
        if (libchdr is null)
        {
            if (chdman is not null)
                throw new InvalidOperationException(
                    $"{Key(nameof(NativeToolOptions.ChdmanDirectory))} requires {Key(nameof(NativeToolOptions.LibchdrDirectory))}.");
            return null;
        }
        if (requireTool && chdman is null)
            throw new InvalidOperationException($"Worker CHD encoding requires {Key(nameof(NativeToolOptions.ChdmanDirectory))}.");
        return new(ChdNativeCandidate.FromBuildRecord(libchdr), requireTool ? ChdmanCandidate.FromBuildRecord(chdman!) : null);
    }

    private static string? Directory(string? value, string name)
    {
        if (value is null)
            return null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Any(char.IsControl) || !Path.IsPathFullyQualified(value))
            throw new InvalidOperationException($"{Key(name)} must be an absolute directory path.");
        return Path.GetFullPath(value);
    }

    private static string Key(string name) => NativeToolOptions.SectionName + ":" + name;
}
