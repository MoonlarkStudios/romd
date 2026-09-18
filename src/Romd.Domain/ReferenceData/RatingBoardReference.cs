namespace Romd.Domain.ReferenceData;

public sealed record RatingBoardDefinition(string Name, string? Description = null, bool Retired = false);
