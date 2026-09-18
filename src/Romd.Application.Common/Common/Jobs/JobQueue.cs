namespace Romd.Application.Common.Jobs;

/// <summary>
///     Strongly-typed Hangfire queue names.
/// </summary>
public sealed record JobQueue(string Value)
{
    /// <summary>
    ///     Default queue for general jobs.
    /// </summary>
    public static readonly JobQueue Default = new("default");

    /// <summary>
    ///     Queue for upload processing jobs.
    /// </summary>
    public static readonly JobQueue Upload = new("upload");

    /// <summary>
    ///     Queue for enrichment jobs (rate-limited, single worker).
    /// </summary>
    public static readonly JobQueue Enrichment = new("enrichment");

    /// <summary>
    ///     Queue for library materialization jobs (single worker until #193 proves commutativity).
    /// </summary>
    public static readonly JobQueue Materialization = new("materialization");

    /// <summary>
    ///     Implicit conversion to string for Hangfire attributes.
    /// </summary>
    public static implicit operator string(JobQueue queue) => queue.Value;

    /// <inheritdoc />
    public override string ToString() => Value;
}
