using Romd.Admin.Application.Source.Dat;
using Romd.Application.Common.Validation;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Dat;

public sealed class DatChangePageRequestTests
{
    private static readonly string Active = new('a', 64);
    private static readonly string Candidate = new('b', 64);
    // Ordinal order, the order the change engine streams: non-ASCII names sort after ASCII ones.
    private static readonly string[] Names = ["Alpha", "Beta", "Gamma", "Zeta", "Ölund"];

    private static DatChangePageRequest Entries(string? cursor = null, int? limit = null, string? change = null, string? query = null,
        string? active = null) =>
        DatChangePageRequest.ForEntries(active ?? Active, Candidate, change, query, cursor, limit).Value;

    private static string Field(ErrorOr.Error error) => FieldErrors.FieldOf(error)!;

    [Theory]
    [InlineData("Added")]
    [InlineData("Removed")]
    [InlineData("Changed")]
    [InlineData("")]
    [InlineData("added")]
    [InlineData("Renamed")]
    [InlineData("Unchanged")]
    public void ForEntries_ChangePatternAgreesWithAcceptedKinds(string change)
    {
        bool declared = System.Text.RegularExpressions.Regex.IsMatch(change, DatChangePageRequest.ChangePattern);
        bool accepted = !DatChangePageRequest.ForEntries(Active, Candidate, change, null, null, null).IsError;
        declared.ShouldBe(accepted);
    }

    [Fact]
    public void ChangePattern_EnumeratesEveryAcceptedKind()
    {
        var kinds = typeof(DatChangePageRequest).GetField("ChangeKinds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .ShouldNotBeNull().GetValue(null).ShouldBeOfType<string[]>();
        DatChangePageRequest.ChangePattern.ShouldBe("^(?:" +
            string.Join('|', kinds.Select(System.Text.RegularExpressions.Regex.Escape)) + ")$");
    }

    [Fact]
    public void ForEntries_AbsentInputs_UseDefaultLimitAndFirstPage()
    {
        var page = Entries();
        page.Limit.ShouldBe(50);
        page.After.ShouldBeNull();
        page.Change.ShouldBeNull();
        page.Query.ShouldBeNull();
    }

    [Fact]
    public void ForEntries_EmptyActiveFingerprint_IsTheFirstImport()
    {
        DatChangePageRequest.ForEntries("", Candidate, null, null, null, null).Value.ActiveSha256.ShouldBeEmpty();
    }

    [Fact]
    public void Page_WalksEveryItemOnceInOrder_ThenEnds()
    {
        var seen = new List<string>();
        string? cursor = null;
        for (int pages = 0; ; pages++)
        {
            pages.ShouldBeLessThan(Names.Length);
            var page = Entries(cursor, limit: 2).Page(Names, name => name);
            seen.AddRange(page.Items);
            page.HasNextPage.ShouldBe(page.NextCursor is not null);
            if (!page.HasNextPage)
                break;
            cursor = page.NextCursor;
        }
        seen.ShouldBe(Names);
    }

    [Fact]
    public void Page_ExactlyFullLastPage_HasNoNextPage()
    {
        var page = Entries(limit: Names.Length).Page(Names, name => name);
        page.Items.ShouldBe(Names);
        page.HasNextPage.ShouldBeFalse();
        page.NextCursor.ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(51)]
    public void ForEntries_LimitOutOfRange_RejectsWithoutClamping(int limit)
    {
        var error = DatChangePageRequest.ForEntries(Active, Candidate, null, null, null, limit).Errors.Single();
        error.Code.ShouldBe("DatReview.InvalidLimit");
        Field(error).ShouldBe("limit");
    }

    [Theory]
    [InlineData("added")]
    [InlineData("")]
    [InlineData("Unchanged")]
    public void ForEntries_UnknownChange_Rejects(string change) =>
        Field(DatChangePageRequest.ForEntries(Active, Candidate, change, null, null, null).Errors.Single()).ShouldBe("change");

    [Fact]
    public void ForEntries_QueryOverTwoHundredCharacters_Rejects()
    {
        DatChangePageRequest.ForEntries(Active, Candidate, null, new string('q', 200), null, null).IsError.ShouldBeFalse();
        Field(DatChangePageRequest.ForEntries(Active, Candidate, null, new string('q', 201), null, null).Errors.Single()).ShouldBe("query");
    }

    [Fact]
    public void ForEntries_MalformedFingerprints_ReportEveryField()
    {
        var errors = DatChangePageRequest.ForEntries("short", null, "bad", null, null, 0).Errors;
        errors.Select(Field).ShouldBe(["change", "activeSha256", "candidateSha256", "limit"], ignoreOrder: true);
    }

    [Fact]
    public void ForEntries_BlankQuery_IsNoFilter()
    {
        var page = Entries(query: "   ");
        page.Query.ShouldBeNull();
        page.Matches("Anything", "Added").ShouldBeTrue();
    }

    [Fact]
    public void Matches_FiltersByExactChangeAndCaseInsensitiveTrimmedQuery()
    {
        var page = Entries(change: "Changed", query: "  disc 1 ");
        page.Matches("Game (Disc 1)", "Changed").ShouldBeTrue();
        page.Matches("Game (Disc 1)", "Added").ShouldBeFalse();
        page.Matches("Game (Disc 2)", "Changed").ShouldBeFalse();
    }

    [Theory]
    [InlineData("change")]
    [InlineData("query")]
    [InlineData("pair")]
    [InlineData("files")]
    public void ForEntries_CursorFromAnotherFilterSetOrPage_IsInvalid(string differs)
    {
        string cursor = Entries(limit: 1, change: "Added", query: "a").Page(Names, name => name).NextCursor!;
        var result = differs switch
        {
            "change" => DatChangePageRequest.ForEntries(Active, Candidate, "Removed", "a", cursor, null),
            "query" => DatChangePageRequest.ForEntries(Active, Candidate, "Added", "b", cursor, null),
            "pair" => DatChangePageRequest.ForEntries("", Candidate, "Added", "a", cursor, null),
            _ => DatChangePageRequest.ForFiles(Active, Candidate, "Alpha", cursor, null)
        };
        var error = result.Errors.Single();
        error.Code.ShouldBe("DatReview.InvalidCursor");
        Field(error).ShouldBe("cursor");
        DatChangePageRequest.ForEntries(Active, Candidate, "Added", "a", cursor, null).Value.After.ShouldBe("Alpha");
    }

    [Fact]
    public void ForFiles_CursorIsBoundToItsEntry()
    {
        string cursor = DatChangePageRequest.ForFiles(Active, Candidate, "Alpha", null, 1).Value
            .Page(["rom:a.bin", "rom:b.bin"], key => key).NextCursor!;
        DatChangePageRequest.ForFiles(Active, Candidate, "Alpha", cursor, null).Value.After.ShouldBe("rom:a.bin");
        DatChangePageRequest.ForFiles(Active, Candidate, "Beta", cursor, null).Errors.Single().Code.ShouldBe("DatReview.InvalidCursor");
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("has space")]
    [InlineData("")]
    public void ForEntries_MalformedCursor_IsInvalid(string cursor) =>
        DatChangePageRequest.ForEntries(Active, Candidate, null, null, cursor, null).Errors.Single().Code.ShouldBe("DatReview.InvalidCursor");

    [Fact]
    public void ForEntries_NonCanonicalOrOverlongCursor_IsInvalid()
    {
        string cursor = Entries(limit: 1).Page(Names, name => name).NextCursor!;
        DatChangePageRequest.ForEntries(Active, Candidate, null, null, cursor + "A", null).IsError.ShouldBeTrue();
        DatChangePageRequest.ForEntries(Active, Candidate, null, null, new string('A', 8193), null).IsError.ShouldBeTrue();
    }
}
