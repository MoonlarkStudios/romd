namespace Romd.Infrastructure.Storage;

/// <summary>Absolute directories of the native CHD builds, bound from <c>Romd:Native</c>.</summary>
/// <remarks>CHD stays off unless <see cref="LibchdrDirectory"/> is configured; worker CHD encoding also requires
/// <see cref="ChdmanDirectory"/>. Each directory holds the <c>build-manifest.json</c> its own build wrote.</remarks>
public sealed class NativeToolOptions
{
    public const string SectionName = "Romd:Native";

    /// <summary>libchdr's <c>build-manifest.json</c> and <c>native/&lt;file&gt;</c>; enables reading CHD.</summary>
    public string? LibchdrDirectory { get; init; }

    /// <summary>chdman's <c>build-manifest.json</c> and <c>native/chdman</c>; enables worker CHD encoding.</summary>
    public string? ChdmanDirectory { get; init; }
}
