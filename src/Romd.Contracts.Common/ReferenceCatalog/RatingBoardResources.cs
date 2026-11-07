namespace Romd.Contracts.Common.ReferenceCatalog;

public sealed record RatingBoardResource(string Key, string Ownership, int? BuiltInVersion, string Name, string? Description, bool Retired);
