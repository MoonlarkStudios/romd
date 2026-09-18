using System.Text.Json.Serialization;

namespace Romd.Domain.Catalog;

/// <summary>
///     Filter for BIOS entries in game queries.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BiosFilter
{
    /// <summary>
    ///     Exclude BIOS entries, show games only (default).
    /// </summary>
    Exclude = 0,

    /// <summary>
    ///     Include both games and BIOS entries.
    /// </summary>
    Include = 1,

    /// <summary>
    ///     Show BIOS entries only (for "Missing BIOS Report").
    /// </summary>
    Only = 2
}
