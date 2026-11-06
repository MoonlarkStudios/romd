using System.Text.Json;
using System.Text.Json.Serialization;

namespace Romd.Contracts.Common.ReferenceCatalog;

/// <summary>Setters preserve omitted versus explicitly null fields during deserialization.</summary>
[JsonConverter(typeof(ReferencePatchJsonConverter))]
public class ReferencePatchDto
{
    private static readonly HashSet<string> DescriptionClears = ["description"];
    private static readonly HashSet<string> NameResets = ["name"];
    private string? _name;
    private string? _description;
    [JsonIgnore] public Dictionary<string, JsonElement> Changes { get; } = new(StringComparer.Ordinal);

    /// <summary>Fields whose explicit null clears the value; the patch schema marks them nullable.</summary>
    [JsonIgnore] public virtual IReadOnlySet<string> ClearableFields => DescriptionClears;

    /// <summary>Fields only the override reset endpoint returns to inheritance, so an explicit null is rejected.</summary>
    [JsonIgnore] public virtual IReadOnlySet<string> ResetOnlyFields => NameResets;

    public string? Name { get => _name; set { _name = value; Set("name", value); } }
    public string? Description { get => _description; set { _description = value; Set("description", value); } }
    protected void Set<T>(string field, T value) => Changes[field] = JsonSerializer.SerializeToElement(value);
}

[JsonConverter(typeof(ReferencePatchJsonConverter))]
public class ArtworkReferencePatchDto : ReferencePatchDto
{
    private static readonly HashSet<string> ArtworkClears = ["description", "icon"];
    private static readonly HashSet<string> ArtworkResets = ["name", "monochrome"];
    private string? _icon;
    private bool? _monochrome;
    public override IReadOnlySet<string> ClearableFields => ArtworkClears;
    public override IReadOnlySet<string> ResetOnlyFields => ArtworkResets;
    /// <summary>Uploaded asset hash; null explicitly hides inherited artwork.</summary>
    public string? Icon { get => _icon; set { _icon = value; Set("icon", value); } }
    public bool? Monochrome { get => _monochrome; set { _monochrome = value; Set("monochrome", value); } }
}

[JsonConverter(typeof(ReferencePatchJsonConverter))]
public sealed class SystemPatchDto : ArtworkReferencePatchDto
{
    private static readonly HashSet<string> SystemResets = ["name", "monochrome", "compactLabel"];
    private string? _compactLabel;
    private IReadOnlyList<string>? _manufacturerKeys;
    public override IReadOnlySet<string> ResetOnlyFields => SystemResets;
    public IReadOnlyList<string>? ManufacturerKeys { get => _manufacturerKeys; set { _manufacturerKeys = value; Set("manufacturerKeys", value); } }
    public string? CompactLabel { get => _compactLabel; set { _compactLabel = value; Set("compactLabel", value); } }
}
