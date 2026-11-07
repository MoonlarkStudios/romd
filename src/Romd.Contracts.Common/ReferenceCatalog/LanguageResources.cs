namespace Romd.Contracts.Common.ReferenceCatalog;

public sealed record LanguageResource(string Key, string Ownership, int? BuiltInVersion, string Name, string? Description, int SortOrder, string Code, bool Retired);
