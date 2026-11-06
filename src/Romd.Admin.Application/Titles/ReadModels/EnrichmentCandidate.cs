namespace Romd.Admin.Application.Titles.ReadModels;

/// <summary>A title selected for bulk enrichment, with its projection-owned local-payload fact.</summary>
public sealed record EnrichmentCandidate(int TitleId, string Name, bool HasLocalPayload);
