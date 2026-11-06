namespace Romd.Contracts.Common.ReferenceCatalog;

public sealed record LanguageResourceDto(string Key, string Ownership, int? BuiltInVersion, string Name, string? Description, int SortOrder, string Code, bool Retired);
