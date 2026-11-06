namespace Romd.Contracts.Common.ReferenceCatalog;

public sealed record RatingBoardResourceDto(string Key, string Ownership, int? BuiltInVersion, string Name, string? Description, bool Retired);
