namespace Romd.Domain.ReferenceData;

public sealed record LanguageDefinition(string Name, string Code, int SortOrder, string? Description = null, bool Retired = false);
