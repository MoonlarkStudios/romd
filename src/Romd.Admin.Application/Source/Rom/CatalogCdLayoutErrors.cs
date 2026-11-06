using ErrorOr;

namespace Romd.Admin.Application.Source.Rom;

public static class CatalogCdLayoutErrors
{
    public static Error SourceChanged => Error.Conflict("CatalogCdLayout.SourceChanged", "The verified ordinary original for the generated descriptor changed.");
}
