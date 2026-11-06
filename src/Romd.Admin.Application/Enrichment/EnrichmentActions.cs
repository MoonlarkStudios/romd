namespace Romd.Admin.Application.Enrichment;

/// <summary>Action vocabulary shared with the admin SPA dispatchers.</summary>
public static class EnrichmentActions
{
    public const string ReviewProvider = nameof(ReviewProvider);
    public const string ContinueWithoutProvider = nameof(ContinueWithoutProvider);
    public const string ConfigureProvider = nameof(ConfigureProvider);
    public const string Retry = nameof(Retry);
    public const string Enrich = nameof(Enrich);
    public const string ReviewMetadata = nameof(ReviewMetadata);
    public const string AcceptOmission = nameof(AcceptOmission);
    public const string ReviewArtwork = nameof(ReviewArtwork);
    public const string LeaveEmpty = nameof(LeaveEmpty);
}
