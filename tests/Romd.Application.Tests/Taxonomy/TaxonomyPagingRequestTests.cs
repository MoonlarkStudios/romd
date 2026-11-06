using Romd.Admin.Application.Taxonomy;
using Romd.Application.Common.Validation;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Taxonomy;

/// <summary>
///     Taxonomy cursors bind everything that shapes their traversal (docs/decisions/api-contract-policy.md,
///     "Pagination"): a row page binds its normalized search query, and an alias page binds its row.
/// </summary>
public sealed class TaxonomyPagingRequestTests
{
    [Fact]
    public void RowPage_Query_IsNormalizedAndBindsTheCursor()
    {
        var request = TaxonomyPageRequest.Create("regions", query: "  Ü.S-A ").Value;
        request.Query.ShouldBe("usa");
        string cursor = request.CursorFor(999, "Atlantis", "dat-atlantis");

        var same = TaxonomyPageRequest.Create("regions", cursor, query: "u s a").Value;
        (same.AfterSortOrder, same.AfterName, same.AfterKey).ShouldBe((999, "Atlantis", "dat-atlantis"));
        TaxonomyPageRequest.Create("regions", cursor, query: "europe").FirstError.Code.ShouldBe("Taxonomy.InvalidCursor");
        TaxonomyPageRequest.Create("regions", cursor).FirstError.Code.ShouldBe("Taxonomy.InvalidCursor");
        TaxonomyPageRequest.Create("regions", TaxonomyPageRequest.Create("regions").Value.CursorFor(1, "Asia", "asia"), query: "usa")
            .FirstError.Code.ShouldBe("Taxonomy.InvalidCursor");
    }

    [Theory]
    [InlineData("!!!")]
    [InlineData("bad\u0007query")]
    public void RowPage_QueryWithoutSearchableText_IsKeyedByQuery(string query)
    {
        var error = TaxonomyPageRequest.Create("regions", query: query).FirstError;

        error.Code.ShouldBe("Request.InvalidParameter");
        FieldErrors.FieldOf(error).ShouldBe("query");
    }

    [Fact]
    public void AliasPage_CursorContinuesOnlyItsOwnRow()
    {
        string cursor = TaxonomyAliasPageRequest.CursorFor("regions", "dat-atlantis", 42);

        var request = TaxonomyAliasPageRequest.Create("regions", "dat-atlantis", cursor, 7).Value;

        (request.AfterId, request.Limit).ShouldBe((42, 7));
        TaxonomyAliasPageRequest.Create("regions", "dat-lemuria", cursor).FirstError.Code.ShouldBe("Taxonomy.InvalidCursor");
        TaxonomyAliasPageRequest.Create("languages", "dat-atlantis", cursor).FirstError.Code.ShouldBe("Taxonomy.InvalidCursor");
        TaxonomyAliasPageRequest.Create("regions", "dat-atlantis", TaxonomyPageRequest.Create("regions").Value.CursorFor(1, "Asia", "asia"))
            .FirstError.Code.ShouldBe("Taxonomy.InvalidCursor");
        FieldErrors.FieldOf(TaxonomyAliasPageRequest.Create("regions", "dat-atlantis", "not a cursor").FirstError).ShouldBe("cursor");
        TaxonomyAliasPageRequest.Create("regions", "dat-atlantis").Value.AfterId.ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(TaxonomyAliasPageRequest.MaxLimit + 1)]
    public void AliasPage_LimitOutOfRange_IsKeyedByLimit(int limit) =>
        TaxonomyAliasPageRequest.Create("regions", "dat-atlantis", limit: limit).FirstError.Code.ShouldBe("Taxonomy.InvalidLimit");
}
