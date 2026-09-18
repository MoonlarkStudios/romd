using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>
///     A ROM file with its pre-computed catalog status.
///     Used to avoid N+1 queries when listing ROMs.
/// </summary>
public sealed record RomWithStatus(RomFile Rom, RomCatalogStatus Status);
