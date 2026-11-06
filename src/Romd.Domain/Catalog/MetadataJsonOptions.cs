using System.Text.Json;
using System.Text.Json.Serialization;

namespace Romd.Domain.Catalog;

/// <summary>
///     JSON serialization options for metadata layer storage.
/// </summary>
public static class MetadataJsonOptions
{
    /// <summary>
    ///     Options for serializing metadata payloads.
    ///     Ignores null values to keep storage compact - nulls mean "no data from this source".
    /// </summary>
    public static readonly JsonSerializerOptions Default = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
