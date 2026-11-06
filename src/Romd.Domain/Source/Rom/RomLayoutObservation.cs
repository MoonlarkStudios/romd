namespace Romd.Domain.Source.Rom;

/// <summary>
///     One platform family's measured structural layout. An inspection carries at most one, so families are exclusive by
///     construction. The hierarchy is closed to this assembly; media kinds match on their own family type alone.
/// </summary>
/// <remarks>
///     A non-sealed record must expose its copy constructor to derived types, so access modifiers alone cannot close the
///     hierarchy. Both constructors therefore refuse a subtype declared outside this assembly; persistence and policies may
///     rely on every layout being one of the domain's families.
/// </remarks>
public abstract record RomLayoutObservation
{
    private protected RomLayoutObservation() => RequireDomainFamily();

    protected RomLayoutObservation(RomLayoutObservation original)
    {
        ArgumentNullException.ThrowIfNull(original);
        RequireDomainFamily();
    }

    private void RequireDomainFamily()
    {
        if (GetType().Assembly != typeof(RomLayoutObservation).Assembly)
            throw new InvalidOperationException($"Layout family '{GetType().FullName}' is not a domain layout family.");
    }

    /// <summary>Whether this layout is consistent with content of exactly this logical identity.</summary>
    public abstract bool Describes(RomLogicalIdentity identity);

    /// <summary>The one layout among separately reported families; observing more than one contradicts an inspection.</summary>
    public static RomLayoutObservation? Exclusive(params ReadOnlySpan<RomLayoutObservation?> observed)
    {
        RomLayoutObservation? single = null;
        foreach (var layout in observed)
        {
            if (layout is null)
                continue;
            if (single is not null)
                throw new ArgumentException("An inspection observes at most one layout family.", nameof(observed));
            single = layout;
        }
        return single;
    }
}
