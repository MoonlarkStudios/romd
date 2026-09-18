using System.Text.Json.Serialization;

namespace Romd.Contracts.Common.ReferenceCatalog;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateSystemDto(string Key, string Name, string CompactLabel, string? Description = null,
    string? Icon = null, bool Monochrome = false, IReadOnlyList<string>? ManufacturerKeys = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateCompanyDto(string Key, string Name, string? Description = null);
[JsonConverter(typeof(ReferencePatchJsonConverter))]
public sealed class CompanyPatchDto : ReferencePatchDto;
public sealed record CompanySummaryDto(string Key, string Name);
public sealed record SystemResourceDto(string Key, string Ownership, int? BuiltInVersion, string Name, string CompactLabel,
    string? Description, ReferenceAssetDto? Icon, bool Retired, IReadOnlyList<CompanySummaryDto> Manufacturers);
public sealed record CompanyResourceDto(string Key, string Ownership, int? BuiltInVersion, string Name, string? Description, bool Retired);
public sealed record SystemOverridesDto(string? Name, string? CompactLabel, string? Description,
    string? Icon, bool? Monochrome, bool HasDescription, bool HasIcon);
public sealed record CompanyOverridesDto(string? Name, string? Description, bool HasDescription);
