using Romd.Application.Common.Systems;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Pagination;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Storage;
using Romd.Admin.Application.Source.Dat.Queries.GetUnroutedDats;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Dat;
using CommonModels = Romd.Contracts.Common.Models;
using Models = Romd.Contracts.Management.Models;
using Romd.Contracts.Common.Models;

namespace Romd.Admin.Application.Source;

/// <summary>
///     Extension methods for mapping domain entities to contract models with encoded IDs.
/// </summary>
public static class SourcesMapping
{
    /// <summary>
    ///     Maps a DatFile entity to its contract representation, carrying its catalog
    ///     source's lifecycle status and neutral catalog source id. The stored source file
    ///     is optional; callers that have resolved it from CAS pass it in.
    /// </summary>
    public static Models.Dat ToContract(
        this DatFile entity,
        SystemKeys systemKeys,
        CatalogSourceStatus sourceStatus,
        int catalogSourceId,
        Models.StoredFileRef? sourceFile = null) =>
        new()
        {
            Id = IdCoder.Encode(entity.Id),
            Name = entity.Name,
            Description = entity.Description,
            Type = entity.Type.ToString(),
            SystemKey = systemKeys.Optional(entity.PlatformId),
            Version = entity.Version,
            Author = entity.Author,
            Url = entity.Url,
            GameCount = entity.GameCount,
            RomCount = entity.RomCount,
            ImportedAt = entity.ImportedAt,
            SourceId = IdCoder.Encode(entity.DatSourceId),
            CatalogSourceId = IdCoder.Encode(catalogSourceId),
            Lifecycle = entity.Lifecycle.ToContract(),
            SourceStatus = sourceStatus.ToContract(),
            SourceFile = sourceFile
        };

    /// <summary>
    ///     Maps a domain DAT lifecycle to its contract representation.
    /// </summary>
    public static Models.DatLifecycle ToContract(this DatFileLifecycle lifecycle) => lifecycle switch
    {
        DatFileLifecycle.PendingActivation => Models.DatLifecycle.PendingActivation,
        DatFileLifecycle.Active => Models.DatLifecycle.Active,
        DatFileLifecycle.Superseded => Models.DatLifecycle.Superseded,
        _ => throw new ArgumentOutOfRangeException(nameof(lifecycle), lifecycle, "Unknown DAT lifecycle")
    };

    /// <summary>
    ///     Maps a domain catalog source status to its contract representation.
    /// </summary>
    public static Models.SourceLifecycleStatus ToContract(this CatalogSourceStatus status) => status switch
    {
        CatalogSourceStatus.Active => Models.SourceLifecycleStatus.Active,
        CatalogSourceStatus.Discontinued => Models.SourceLifecycleStatus.Discontinued,
        CatalogSourceStatus.Disabled => Models.SourceLifecycleStatus.Disabled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown catalog source status")
    };

    /// <summary>
    ///     Maps a domain catalog source kind to its contract representation.
    /// </summary>
    public static Models.SourceKind ToContract(this CatalogSourceKind kind) => kind switch
    {
        CatalogSourceKind.Dat => Models.SourceKind.Dat,
        CatalogSourceKind.Import => Models.SourceKind.Import,
        CatalogSourceKind.Manual => Models.SourceKind.Manual,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown catalog source kind")
    };

    /// <summary>
    ///     Maps a DAT-with-size read model to its contract representation.
    /// </summary>
    public static Models.Dat ToContract(this DatWithSize entity, SystemKeys systemKeys) =>
        entity.Dat.ToContract(
            systemKeys, entity.SourceStatus,
            entity.CatalogSourceId,
            StoredFileRefMapping.ToStoredFileRef(entity.SizeBytes, entity.SizeOnDiskBytes, entity.IsCompressed));

    /// <summary>
    ///     Maps a collection of DAT-with-size read models to contract models.
    /// </summary>
    public static IEnumerable<Models.Dat> ToContract(this IEnumerable<DatWithSize> entities, SystemKeys systemKeys) =>
        entities.Select(e => e.ToContract(systemKeys));

    /// <summary>
    ///     Maps a DAT-with-source-status read model to its contract representation.
    /// </summary>
    public static Models.Dat ToContract(this DatWithSourceStatus entity, SystemKeys systemKeys) =>
        entity.Dat.ToContract(systemKeys, entity.SourceStatus, entity.CatalogSourceId);

    /// <summary>
    ///     Maps a collection of DAT-with-source-status read models to contract models.
    /// </summary>
    public static IEnumerable<Models.Dat> ToContract(this IEnumerable<DatWithSourceStatus> entities, SystemKeys systemKeys) =>
        entities.Select(e => e.ToContract(systemKeys));

    /// <summary>
    ///     Maps a DatGame entity to its contract representation.
    /// </summary>
    public static Models.DatGame ToContract(this DatGame entity) => new()
    {
        Id = IdCoder.Encode(entity.Id),
        DatId = IdCoder.Encode(entity.DatFileId),
        Name = entity.Name,
        Description = entity.Description,
        Year = entity.Year,
        Manufacturer = entity.Manufacturer,
        CloneOf = entity.CloneOf,
        RomOf = entity.RomOf,
        IsBios = entity.IsBios,
        Roms = entity.Roms.Select(r => r.ToContract()).ToList(),
        Disks = entity.Disks.Select(d => d.ToContract()).ToList()
    };

    /// <summary>
    ///     Maps a DatRom entity to its contract representation.
    /// </summary>
    public static Models.DatRom ToContract(this DatRom entity) => new()
    {
        Id = IdCoder.Encode(entity.Id),
        GameId = IdCoder.Encode(entity.DatGameId),
        Name = entity.Name,
        Size = (ByteCount)entity.Size,
        Crc = entity.Crc?.ToString(),
        Md5 = entity.Md5?.ToString(),
        Sha1 = entity.Sha1?.ToString(),
        Status = entity.Status
    };

    /// <summary>
    ///     Maps a DatDisk entity to its contract representation.
    /// </summary>
    public static Models.DatDisk ToContract(this DatDisk entity) => new()
    {
        Id = IdCoder.Encode(entity.Id),
        GameId = IdCoder.Encode(entity.DatGameId),
        Name = entity.Name,
        Sha1 = entity.Sha1?.ToString(),
        Md5 = entity.Md5?.ToString(),
        Status = entity.Status
    };

    /// <summary>
    ///     Maps a collection of DatGame entities to contract models.
    /// </summary>
    public static IEnumerable<Models.DatGame> ToContract(this IEnumerable<DatGame> entities) =>
        entities.Select(e => e.ToContract());

    /// <summary>
    ///     Maps a PagedList of DatGame entities to a Page contract.
    /// </summary>
    public static CommonModels.Page<Models.DatGame> ToContract(this PagedList<DatGame> pagedList) => new()
    {
        Items = pagedList.Items.Select(g => g.ToContract()).ToList(),
        NextCursor = pagedList.NextCursor,
        HasNextPage = pagedList.HasNextPage
    };

    /// <summary>
    ///     Maps an UnroutedDatSummary to its contract representation.
    /// </summary>
    public static Models.UnroutedDat ToContract(this UnroutedDatSummary summary, SystemKeys systemKeys) => new()
    {
        Dat = summary.DatFile.ToContract(systemKeys, summary.SourceStatus, summary.CatalogSourceId),
        MatchedRomFileCount = summary.MatchedRomFileCount
    };

    /// <summary>
    ///     Maps a collection of UnroutedDatSummary read models to contract models.
    /// </summary>
    public static IEnumerable<Models.UnroutedDat> ToContract(this IEnumerable<UnroutedDatSummary> summaries, SystemKeys systemKeys) =>
        summaries.Select(s => s.ToContract(systemKeys));

    /// <summary>
    ///     Maps a PlatformAlias to its contract representation.
    /// </summary>
    public static Models.PlatformAlias ToContract(this Domain.Source.Platform.PlatformAlias entity) => new()
    {
        Id = IdCoder.Encode(entity.Id),
        Type = entity.Type == Domain.Source.Platform.PlatformAliasType.Name ? "name" : "provider",
        Provider = entity.Provider,
        Value = entity.Value
    };

    /// <summary>
    ///     Maps a collection of PlatformAlias entities to contract models.
    /// </summary>
    public static IEnumerable<Models.PlatformAlias> ToContract(
        this IEnumerable<Domain.Source.Platform.PlatformAlias> entities) =>
        entities.Select(e => e.ToContract());
}
