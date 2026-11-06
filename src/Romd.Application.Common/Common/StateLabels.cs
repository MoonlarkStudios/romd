namespace Romd.Application.Common;

/// <summary>
///     Canonical vocabulary for state labels used across the UI.
///     These terms form a unified taxonomy for ROM state representation.
/// </summary>
public static class StateLabels
{
    // ========================================
    // Dimension 1: Identification
    // ========================================

    /// <summary>
    ///     ROM has at least one DatRom match binding.
    /// </summary>
    public const string Identified = "Identified";

    /// <summary>
    ///     ROM has no DatRom match binding (not in any loaded DAT).
    /// </summary>
    public const string Unidentified = "Unidentified";

    // ========================================
    // Dimension 2: Routing
    // ========================================

    /// <summary>
    ///     ROM has a resolved destination (platform + title binding).
    /// </summary>
    public const string Routed = "Routed";

    /// <summary>
    ///     ROM is identified but missing platform assignment or title binding.
    /// </summary>
    public const string Unrouted = "Unrouted";

    // ========================================
    // Dimension 3: Coverage (per-Title)
    // ========================================

    /// <summary>
    ///     All releases/versions owned for a title.
    /// </summary>
    public const string Complete = "Complete";

    /// <summary>
    ///     Some releases owned for a title.
    /// </summary>
    public const string Partial = "Partial";

    /// <summary>
    ///     No releases owned for a title.
    /// </summary>
    public const string Missing = "Missing";

    // ========================================
    // Dimension 4: Enrichment
    // ========================================

    /// <summary>
    ///     Enrichment not attempted.
    /// </summary>
    public const string Unenriched = "Unenriched";

    /// <summary>
    ///     Enrichment in progress.
    /// </summary>
    public const string Enriching = "Enriching";

    /// <summary>
    ///     Metadata populated successfully.
    /// </summary>
    public const string Enriched = "Enriched";

    /// <summary>
    ///     Enrichment requires manual action (failed or not found).
    /// </summary>
    public const string NeedsAttention = "Needs Attention";
}
