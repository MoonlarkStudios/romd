using Microsoft.EntityFrameworkCore;
using Romd.Persistence.Entities;

namespace Romd.Persistence.MediaFacts;

/// <summary>Logical outputs, archives, original-layout dependencies and derived descriptors retain complete reconstruction facts.</summary>
internal static class MediaRepresentationOwnership
{
    internal static IQueryable<RomRepresentationEntity> Owned(RomdDbContext db) => db.RomRepresentations.FromSqlRaw("""
        SELECT representation.* FROM romd."Representations" representation WHERE
            EXISTS (SELECT 1 FROM romd."RepresentationOutputs" WHERE "RepresentationId" = representation."Id" AND "RomId" IS NOT NULL) OR
            EXISTS (SELECT 1 FROM romd."CdArchiveOwners" WHERE "RepresentationId" = representation."Id") OR
            EXISTS (SELECT 1 FROM romd."OriginalDiscLayoutFiles" WHERE "FileId" = representation."FileId") OR
            EXISTS (SELECT 1 FROM romd."CatalogCdLayoutBindings" WHERE "RepresentationId" = representation."Id")
        """);
}
