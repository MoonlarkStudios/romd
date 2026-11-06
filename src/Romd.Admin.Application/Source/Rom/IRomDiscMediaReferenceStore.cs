using ErrorOr;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>Local lookup of immutable records admitted by a trusted reference importer.</summary>
/// <remarks>No network or media processing belongs in this lookup. Return at most maximumResults rows;
/// the extra requested row lets the resolver reject an overflowing result rather than silently truncate it.
/// Missing references return an empty list; temporary store unavailability returns Unavailable().
/// Neither a record constructor nor its raw checksum authenticates its published assertions.</remarks>
public interface IRomDiscMediaReferenceStore
{
    Task<ErrorOr<IReadOnlyList<RomDiscMediaReferenceRecord>>> FindAsync(RomDiscMediaReferenceQuery query,
        int maximumResults, CancellationToken ct = default);
}

public static class RomDiscMediaReferenceErrors
{
    public const string UnavailableCode = "Rom.DiscMediaReferenceUnavailable";
    public static Error Unavailable() => Error.Failure(UnavailableCode, "The local disc media reference store is unavailable.");
    public static Error InvalidMeasurement() => Error.Validation("Rom.DiscMediaMeasurement",
        "DVD resolution requires complete bounded logical hashes and matching boot facts.");
    public static Error InvalidStoreResult() => Error.Validation("Rom.DiscMediaReferenceResult",
        "The reference store returned absent rows or inconsistent immutable record identities.");
    public static Error TupleMismatch() => Error.Validation("Rom.DiscMediaReferenceTuple",
        "A returned reference does not match every requested published hash and length.");
    public static Error ReferenceLimit() => Error.Failure("Rom.DiscMediaReferenceLimit",
        "The exact disc reference lookup exceeds its bounded record limit.");
}
