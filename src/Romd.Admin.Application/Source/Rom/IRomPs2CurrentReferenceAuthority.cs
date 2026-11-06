using ErrorOr;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>Rechecks PS2 DVD assertions under the caller's existing publication transaction.</summary>
/// <remarks>Acquire existing ROM/file locks before this port. The assertion guard is shared with trusted
/// imports; no immutable observation is staged and no transaction is flushed or committed here.</remarks>
public interface IRomPs2CurrentReferenceAuthority
{
    Task<ErrorOr<RomPs2DvdEligibilityResolution>> ResolveAsync(RomLogicalIdentity measurement,
        RomPs2BootLayoutFacts boot, DateTimeOffset inspectedAt, CancellationToken ct = default);
}
