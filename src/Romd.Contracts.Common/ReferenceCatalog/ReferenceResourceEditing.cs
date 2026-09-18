using System.Text.Json;
using System.Text.Json.Serialization;

namespace Romd.Contracts.Common.ReferenceCatalog;

/// <summary>Setters preserve omitted versus explicitly null fields during deserialization.</summary>
[JsonConverter(typeof(ReferencePatchJsonConverter))]
public class ReferencePatchDto
{
    private string? _name;
    private string? _description;
    [JsonIgnore] public Dictionary<string, JsonElement> Changes { get; } = new(StringComparer.Ordinal);
    public string? Name { get => _name; set { _name = value; Set("name", value); } }
    public string? Description { get => _description; set { _description = value; Set("description", value); } }
    protected void Set<T>(string field, T value) => Changes[field] = JsonSerializer.SerializeToElement(value);
}

[JsonConverter(typeof(ReferencePatchJsonConverter))]
public class ArtworkReferencePatchDto : ReferencePatchDto
{
    private string? _icon;
    private bool? _monochrome;
    /// <summary>Uploaded asset hash; null explicitly hides inherited artwork.</summary>
    public string? Icon { get => _icon; set { _icon = value; Set("icon", value); } }
    public bool? Monochrome { get => _monochrome; set { _monochrome = value; Set("monochrome", value); } }
}

[JsonConverter(typeof(ReferencePatchJsonConverter))]
public sealed class SystemPatchDto : ArtworkReferencePatchDto
{
    private string? _compactLabel;
    private IReadOnlyList<string>? _manufacturerKeys;
    public IReadOnlyList<string>? ManufacturerKeys { get => _manufacturerKeys; set { _manufacturerKeys = value; Set("manufacturerKeys", value); } }
    public string? CompactLabel { get => _compactLabel; set { _compactLabel = value; Set("compactLabel", value); } }
}
