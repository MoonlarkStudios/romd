namespace Romd.Admin.Application.Titles.Enrichment;

public sealed record MetadataRematerializationRequest(Guid Id, int? TitleId, int? PlatformId, int Attempts);
