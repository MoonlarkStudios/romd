using Romd.Domain.Catalog;
using Romd.Domain.Source.Dat;

namespace Romd.Admin.Application.Source.Dat;

/// <summary>
///     A DAT file paired with the lifecycle status and id of its catalog source, read in a
///     single query snapshot so a concurrent DAT deletion can never tear the pairing apart.
/// </summary>
public sealed record DatWithSourceStatus(DatFile Dat, CatalogSourceStatus SourceStatus, int CatalogSourceId);
