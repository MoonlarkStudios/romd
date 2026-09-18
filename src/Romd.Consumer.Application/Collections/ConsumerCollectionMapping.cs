using Romd.Application.Common.Systems;
using System.Text;
using Romd.Application.Common.Artwork;
using Romd.Application.Common.Ids;
using Romd.Contracts.Consumer.Collections;

namespace Romd.Consumer.Application.Collections;

internal static class ConsumerCollectionMapping
{
    public static ConsumerCollectionDto ToDto(this ConsumerCollectionReadModel model) =>
        new()
        {
            Id = IdCoder.Encode(model.Id),
            Name = model.Name,
            Description = model.Description,
            System = model.System?.ToContract(),
            CoverUrl = model.CoverMediaId is null ? null : $"/media/{IdCoder.Encode(model.CoverMediaId.Value)}",
            HeroUrl = null,
            ItemCount = model.ItemCount,
            IsFeatured = model.IsFeatured
        };

    public static ConsumerCollectionTitleDto ToDto(this ConsumerCollectionTitleReadModel model)
    {
        return new ConsumerCollectionTitleDto
        {
            Id = IdCoder.Encode(model.TitleId),
            Artwork = model.Artwork.Select(ArtworkContractMapping.ToContract).ToArray(),
            Name = model.TitleName,
            System = model.System.ToContract(),
            CoverUrl = model.CoverMediaId is null ? null : $"/media/{IdCoder.Encode(model.CoverMediaId.Value)}",
            Genre = model.Genre,
            ReleaseDate = model.ReleaseDate,
            Rating = model.Rating,
            ReleaseCount = model.ReleaseCount,
            DefaultReleaseId = model.DefaultReleaseId is null ? null : IdCoder.Encode(model.DefaultReleaseId.Value)
        };
    }

    public static string EncodeCursor(ConsumerCollectionTitleReadModel model)
    {
        string value = $"{model.SortOrder}:{model.TitleId}";
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecodeCursor(string cursor, out ConsumerCollectionTitleCursor value)
    {
        value = default!;

        string base64 = cursor.Replace('-', '+').Replace('_', '/');
        int padding = (4 - base64.Length % 4) % 4;
        base64 = base64.PadRight(base64.Length + padding, '=');

        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            string[] parts = decoded.Split(':', 2);

            if (parts.Length != 2
                || !int.TryParse(parts[0], out int sortOrder)
                || !int.TryParse(parts[1], out int titleId))
            {
                return false;
            }

            value = new ConsumerCollectionTitleCursor(sortOrder, titleId);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
