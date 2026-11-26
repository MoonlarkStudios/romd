using System.Text.Json.Serialization;

namespace Romd.Contracts.Common.ReferenceCatalog;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateSystem(string Key, string Name, string CompactLabel, string? Description = null,
    string? Icon = null, bool Monochrome = false, IReadOnlyList<string>? ManufacturerKeys = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateCompany(string Key, string Name, string? Description = null);
[JsonConverter(typeof(ReferencePatchJsonConverter))]
public sealed class CompanyPatch : ReferencePatch;
public sealed record CompanySummary(string Key, string Name);
public sealed record SystemResource(string Key, string Ownership, int? BuiltInVersion, string Name, string CompactLabel,
    string? Description, ReferenceAsset? Icon, bool Retired, IReadOnlyList<CompanySummary> Manufacturers)
{
    public ReferenceAsset? ConsoleArtwork { get; init; }
    public ReferenceAsset? ControllerArtwork { get; init; }
    public required string Revision { get; init; }
}
public sealed record CompanyResource(string Key, string Ownership, int? BuiltInVersion, string Name, string? Description, bool Retired)
{
    public required string Revision { get; init; }
}
public sealed record SystemOverrides(string? Name, string? CompactLabel, string? Description,
    string? Icon, bool? Monochrome, bool HasDescription, bool HasIcon)
{
    public string? ConsoleArtwork { get; init; }
    public string? ControllerArtwork { get; init; }
}
public sealed record CompanyOverrides(string? Name, string? Description, bool HasDescription);
