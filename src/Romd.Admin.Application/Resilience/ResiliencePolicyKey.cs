namespace Romd.Admin.Application.Resilience;

/// <summary>
///     Strongly-typed Polly resilience policy keys.
/// </summary>
public sealed record ResiliencePolicyKey(string Value)
{
    /// <summary>
    ///     Retry policy for transient I/O and database failures.
    /// </summary>
    public static readonly ResiliencePolicyKey TransientFault = new("transient-fault");

    /// <summary>
    ///     Retry policy for external HTTP API calls.
    /// </summary>
    public static readonly ResiliencePolicyKey ExternalApi = new("external-api");

    /// <summary>
    ///     Circuit breaker and retry policy for IGDB API.
    /// </summary>
    public static readonly ResiliencePolicyKey IgdbApi = new("igdb-api");

    /// <summary>
    ///     Implicit conversion to string for Polly registration.
    /// </summary>
    public static implicit operator string(ResiliencePolicyKey key) => key.Value;

    /// <inheritdoc />
    public override string ToString() => Value;
}
