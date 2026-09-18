using Romd.Application.Common.Systems;
using Romd.Application.Common.Ids;
using Romd.Domain.Source.Rom;
using Models = Romd.Contracts.Management.Models;
using Romd.Contracts.Common.Models;

namespace Romd.Admin.Application.Source.Rom;

public static class RomMapping
{
    public static Models.Rom ToContract(
        this RomFile entity,
        RomCatalogStatus status,
        IReadOnlyList<Models.RomMatch>? matches = null) => new()
    {
        Id = IdCoder.Encode(entity.Id),
        OriginalFilename = entity.OriginalFilename,
        Size = (ByteCount)entity.Size,
        Sha1 = entity.Sha1.ToString(),
        Md5 = entity.Md5.ToString(),
        Crc32 = entity.Crc32.ToString(),
        ImportedAt = entity.ImportedAt,
        Status = status.ToString().ToLowerInvariant(),
        Matches = matches
    };

    public static IEnumerable<Models.Rom> ToContract(
        this IEnumerable<RomFile> entities,
        RomCatalogStatus status) =>
        entities.Select(e => e.ToContract(status));

    public static Models.RomIngestResult ToContract(this RomIngestResult result, RomCatalogStatus status) => new()
    {
        Rom = result.RomFile.ToContract(status), IsNew = result.IsNew
    };

    public static Models.LibraryStats ToContract(this CollectionStats stats, SystemKeys systemKeys) => new()
    {
        TotalRomFiles = stats.TotalRomFiles,
        UnidentifiedCount = stats.UnidentifiedCount,
        UnroutedCount = stats.UnroutedCount,
        CatalogedCount = stats.CatalogedCount,
        TotalSizeBytes = (ByteCount)stats.TotalSizeBytes,
        TotalSizeOnDiskBytes = (ByteCount)stats.TotalSizeOnDiskBytes,
        CompressionRatio = stats.CompressionRatio,
        BytesSaved = (ByteCount)stats.BytesSaved,
        PlatformBreakdown = stats.PlatformBreakdown.Select(p => p.ToContract(systemKeys)).ToList()
    };

    public static Models.PlatformBreakdown ToContract(this PlatformBreakdown breakdown, SystemKeys systemKeys) => new()
    {
        SystemKey = systemKeys.Required(breakdown.PlatformId),
        PlatformName = breakdown.PlatformName,
        LocalPayloadCount = breakdown.LocalPayloadCount,
        TotalCount = breakdown.TotalCount
    };
}
