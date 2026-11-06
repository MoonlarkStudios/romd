namespace Romd.Infrastructure.Jobs;

/// <summary>
///     Queue name constants for Hangfire.
///     Uses string constants since attributes require compile-time constants.
/// </summary>
internal static class JobQueues
{
    public const string Default = "default";
    public const string Upload = "upload";
    public const string Enrichment = "enrichment";
    public const string Materialization = "materialization";
}
