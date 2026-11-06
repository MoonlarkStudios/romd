namespace Romd.Infrastructure.ReferenceData;

/// <summary>Complete raw HTTP representation bytes with immutable retrieval context.</summary>
/// <remarks>The profile is a policy label, not authentication proof. Construction does
/// not authenticate a source. The importer must trust the production HTTP composition,
/// retain the exact body and pass this same context to the separate parser/store.</remarks>
public sealed class RedumpDiscRawRetrieval
{
    private readonly byte[] _rawBody;

    internal RedumpDiscRawRetrieval(int discId, string canonicalSourceUrl,
        DateTimeOffset retrievedAtUtc, byte[] rawBody)
    {
        DiscId = discId;
        CanonicalSourceUrl = canonicalSourceUrl;
        RetrievedAtUtc = retrievedAtUtc;
        _rawBody = rawBody.ToArray();
    }

    public int DiscId { get; }
    public string CanonicalSourceUrl { get; }
    public DateTimeOffset RetrievedAtUtc { get; }
    public string AcceptedSourceProvenance => RedumpDiscReferenceFetcher.Profile;
    public int RawBodyBytes => _rawBody.Length;

    /// <summary>Returns a separate copy; callers cannot mutate the retained envelope body.</summary>
    public byte[] CopyRawBody() => _rawBody.ToArray();
}
