using Romd.Application.Common.Systems;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Collections;
using Romd.Domain.Collections;

namespace Romd.Admin.Application.Collections;

public static class CollectionMapping
{
    public static CollectionSummary ToSummary(this CollectionSummaryReadModel model, SystemKeys systemKeys) => new()
    {
        Id = IdCoder.Encode(model.Id),
        Name = model.Name,
        Description = model.Description,
        SystemKey = systemKeys.Optional(model.PlatformId),
        CoverUrl = model.CoverMediaId.HasValue ? $"/media/{IdCoder.Encode(model.CoverMediaId.Value)}" : null,
        ItemCount = model.ItemCount,
        IsSystem = model.IsSystem,
        SortOrder = model.SortOrder,
        CreatedAt = model.CreatedAt
    };

    public static CollectionSummary ToSummary(this Collection entity, int itemCount, SystemKeys systemKeys) => new()
    {
        Id = IdCoder.Encode(entity.Id),
        Name = entity.Name,
        Description = entity.Description,
        SystemKey = systemKeys.Optional(entity.PlatformId),
        CoverUrl = entity.CoverMediaId.HasValue ? $"/media/{IdCoder.Encode(entity.CoverMediaId.Value)}" : null,
        ItemCount = itemCount,
        IsSystem = entity.IsSystem,
        SortOrder = entity.SortOrder,
        CreatedAt = entity.CreatedAt
    };

    public static CollectionDetail ToDetail(
        this Collection entity,
        IReadOnlyList<CollectionItemDto> items, SystemKeys systemKeys) => new()
    {
        Id = IdCoder.Encode(entity.Id),
        Name = entity.Name,
        Description = entity.Description,
        SystemKey = systemKeys.Optional(entity.PlatformId),
        CoverUrl = entity.CoverMediaId.HasValue ? $"/media/{IdCoder.Encode(entity.CoverMediaId.Value)}" : null,
        ItemCount = items.Count,
        IsSystem = entity.IsSystem,
        SortOrder = entity.SortOrder,
        CreatedAt = entity.CreatedAt,
        Items = items
    };

    public static CollectionItemDto ToItemDto(this CollectionItemReadModel model, SystemKeys systemKeys) => new()
    {
        TitleId = IdCoder.Encode(model.TitleId),
        TitleName = model.TitleName,
        SystemKey = systemKeys.Required(model.PlatformId),
        CoverUrl = model.CoverMediaId.HasValue ? $"/media/{IdCoder.Encode(model.CoverMediaId.Value)}" : null,
        Note = model.Note,
        SortOrder = model.SortOrder,
        AddedAt = model.AddedAt
    };

    public static IReadOnlyList<CollectionItemDto> ToItemDtos(
        this IReadOnlyList<CollectionItemReadModel> models, SystemKeys systemKeys) =>
        models.Select(m => m.ToItemDto(systemKeys)).ToList();
}
