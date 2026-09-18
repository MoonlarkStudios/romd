using Romd.Domain.Catalog;
using Romd.Domain.Source.Dat;

namespace Romd.Admin.Application.Source.Dat;

/// <summary>
///     A DAT file paired with the size and compression of its stored source file in CAS,
///     and the lifecycle status and id of its catalog source. The file fields are null when
///     the source file is no longer present in storage.
/// </summary>
public sealed record DatWithSize(
    DatFile Dat,
    long? SizeBytes,
    long? SizeOnDiskBytes,
    bool? IsCompressed,
    CatalogSourceStatus SourceStatus,
    int CatalogSourceId);
