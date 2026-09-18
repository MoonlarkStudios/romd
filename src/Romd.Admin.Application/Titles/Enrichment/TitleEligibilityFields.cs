using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.Enrichment;

public readonly record struct TitleEligibilityFields(
    string? Genre,
    int? ConservativeMinimumAge,
    string ContentRatingsFingerprint)
{
    public static TitleEligibilityFields From(Title title) =>
        new(
            title.Genre,
            title.ConservativeMinimumAge,
            string.Join(
                "|",
                title.ContentRatings
                    .OrderBy(r => r.Board)
                    .Select(r => string.Join(
                        ":",
                        r.Board,
                        r.Code,
                        r.Designation,
                        r.MinimumAge,
                        r.SourceId))));
}
