using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.Enrichment;

public interface IMetadataRematerializer
{
    Task RematerializeAsync(Title title, CancellationToken ct = default);
}
