using System.Text.Json.Serialization;
using Romd.Contracts.Common.ReferenceCatalog;

namespace Romd.Contracts.Management.Models;

public sealed record ManagedSystem(string Key, string Name, string ShortName, string? Manufacturer,
    bool Enabled, string State, string? Message, int CatalogCount, int OwnedTitles, int TrackedTitles = 0,
    int KnownTitles = 0, DateTimeOffset? LastDatImportedAt = null);
public sealed record ManagedSystemSetupDetails(SystemResource System, ManagedSystemHardware? Hardware);
public sealed record ManagedSystemHardware(string? Category, int? Generation, int? ReleaseYear);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateManagedSystemRequest(string Key, string Name, string CompactLabel, string? Description = null,
    string? Icon = null, bool Monochrome = false, IReadOnlyList<string>? ManufacturerKeys = null,
    string? Category = null, int? Generation = null, int? ReleaseYear = null, IReadOnlyList<string>? NameAliases = null);

[JsonConverter(typeof(ReferencePatchJsonConverter))]
public sealed class ManagedSystemPatch : ReferencePatch
{
    private static readonly HashSet<string> Clearable = ["description", "category", "generation", "releaseYear"];
    private string? category;
    private int? generation;
    private int? releaseYear;
    private IReadOnlyList<string>? manufacturerKeys;
    private IReadOnlyList<string>? nameAliases;

    public override IReadOnlySet<string> ClearableFields => Clearable;
    public string? Category { get => category; set { category = value; Set("category", value); } }
    public int? Generation { get => generation; set { generation = value; Set("generation", value); } }
    public int? ReleaseYear { get => releaseYear; set { releaseYear = value; Set("releaseYear", value); } }
    public IReadOnlyList<string>? ManufacturerKeys { get => manufacturerKeys; set { manufacturerKeys = value; Set("manufacturerKeys", value); } }
    public IReadOnlyList<string>? NameAliases { get => nameAliases; set { nameAliases = value; Set("nameAliases", value); } }
}
public sealed record SetSystemEnabled(bool Enabled);
public sealed record SystemDatPreview(string Name, int Bytes, string? SuggestedSystemKey,
    IReadOnlyList<string> ExistingDatIds, DatReplacementPreview Preview);
