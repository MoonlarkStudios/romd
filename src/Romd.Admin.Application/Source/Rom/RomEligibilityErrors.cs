using ErrorOr;

namespace Romd.Admin.Application.Source.Rom;

public static class RomEligibilityErrors
{
    public static Error ConflictingObservation() => Error.Conflict("Rom.EligibilityConflict",
        "The inspector returned a different observation for an immutable content/profile/build identity.");
    public static Error IdentityMismatch() => Error.Validation("Rom.EligibilityIdentity", "Eligibility does not describe the retained logical ROM.");
    public static Error MeasurementNotRetained() => Error.Validation("Rom.EvidenceMeasurement", "Additional facts require an exact complete retained output measurement.");
    public static Error SourceEquivalenceNotRetained() => Error.Validation("Rom.SourceEquivalence", "Creation requires an independently measured retained source and output.");
}
