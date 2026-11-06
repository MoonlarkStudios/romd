using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Admin.Application.ReferenceData;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;
using Romd.Persistence.Entities;

namespace Romd.Persistence.ReferenceData;

internal static partial class TypedReferenceMapping
{
    internal static RegionDefinition Definition(RegionEntity row) => new(row.BaseName!, row.BaseSortOrder, row.BaseDescription, row.Retired);
    internal static RegionDefinition Effective(RegionEntity row) => Definition(row);
    internal static LanguageDefinition Definition(GameLanguageEntity row) => new(row.BaseName!, row.Code, row.BaseSortOrder, row.BaseDescription, row.Retired);
    internal static LanguageDefinition Effective(GameLanguageEntity row) => Definition(row);
    internal static RatingBoardDefinition Definition(RatingBoardEntity row) => new(row.BaseName, row.BaseDescription, row.Retired);
    internal static RatingBoardDefinition Effective(RatingBoardEntity row) => Definition(row);
    internal static RatingDefinition Definition(RatingEntity row) => new(row.BaseName, row.BoardKey, row.Code, row.BaseDescription, row.BaseAssetHash, row.BaseMonochrome, row.Designation, row.MinimumAge, row.Retired);
    internal static RatingDefinition Effective(RatingEntity row) => Definition(row);
}
