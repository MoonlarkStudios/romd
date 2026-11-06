namespace Romd.Admin.Application.Source.Dat;

/// <summary>
///     The hash pins of a reviewed DAT document pair: a snapshot binding, not a precondition
///     (docs/decisions/api-contract-policy.md, "Idempotency And Concurrency"). The active pin is the empty
///     string when no document is installed yet. The patterns declare the same rules on query parameters.
/// </summary>
public static class DatReviewPins
{
    public const string CandidatePattern = "^[0-9a-fA-F]{64}$";
    public const string ActivePattern = "^(?:[0-9a-fA-F]{64})?$";
    public const int HashLength = 64;

    public static bool IsCandidate(string? value) => value is { Length: HashLength } && value.All(char.IsAsciiHexDigit);

    public static bool IsActive(string? value) => value is "" || IsCandidate(value);

    /// <summary>Every malformed pin with its wire name and message, in request order.</summary>
    public static IEnumerable<(string Field, string Message)> Failures(string? activeSha256, string? candidateSha256)
    {
        if (!IsActive(activeSha256))
            yield return ("activeSha256", "Use the reviewed active document's SHA-256, or an empty string when no document is installed.");
        if (!IsCandidate(candidateSha256))
            yield return ("candidateSha256", "Use the reviewed candidate document's SHA-256.");
    }
}
