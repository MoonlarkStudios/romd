using System.Text;
using System.Text.Json.Nodes;
using Romd.Admin.Application.Titles;
using Romd.Application.Common.Pagination;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed class PlatformTitlePageRequestTests
{
    [Fact]
    public void Create_AbsentCursor_UsesBoundedDefault()
    {
        var request = PlatformTitlePageRequest.Create(1, null, false).Value;
        request.Limit.ShouldBe(50);
        request.AfterName.ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Create_InvalidLimit_RejectsWithoutClamping(int limit) =>
        PlatformTitlePageRequest.Create(1, null, false, limit: limit).Errors.Single().Code.ShouldBe("PlatformTitles.InvalidLimit");

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void Create_ValidLimitAndCursor_RoundTripsSeekAndScope(int limit)
    {
        var first = PlatformTitlePageRequest.Create(1, 7, true).Value;
        var next = PlatformTitlePageRequest.Create(1, 7, true, first.CursorFor("Équal Title", 42), limit).Value;
        next.AfterName.ShouldBe("Équal Title");
        next.AfterId.ShouldBe(42);
        next.Limit.ShouldBe(limit);
    }

    [Fact]
    public void Create_MaxLengthSupplementaryName_RoundTripsStorageCharacterLimit()
    {
        string name = string.Concat(Enumerable.Repeat("\U0001F600", 500));
        var first = PlatformTitlePageRequest.Create(1, 7, true).Value;
        string cursor = first.CursorFor(name, 42);
        cursor.Length.ShouldBeLessThan(4096);
        var next = PlatformTitlePageRequest.Create(1, 7, true, cursor).Value;
        next.AfterName.ShouldBe(name);
        next.AfterId.ShouldBe(42);
        PlatformTitlePageRequest.Create(1, 7, true, first.CursorFor(name + "A", 42))
            .Errors.Single().Code.ShouldBe("PlatformTitles.InvalidCursor");
    }

    [Theory]
    [InlineData("\0")]
    [InlineData("before\0after")]
    public void Create_CursorNameContainingNul_Rejects(string name)
    {
        string cursor = PlatformTitlePageRequest.Create(1, null, false).Value.CursorFor(name, 42);
        PlatformTitlePageRequest.Create(1, null, false, cursor).Errors.Single().Code.ShouldBe("PlatformTitles.InvalidCursor");
    }

    [Theory]
    [InlineData(2, 7, true)]
    [InlineData(1, 8, true)]
    [InlineData(1, null, true)]
    [InlineData(1, 7, false)]
    public void Create_CursorFromDifferentScope_Rejects(int platform, int? library, bool payload)
    {
        string cursor = PlatformTitlePageRequest.Create(1, 7, true).Value.CursorFor("Title", 42);
        PlatformTitlePageRequest.Create(platform, library, payload, cursor).Errors.Single().Code.ShouldBe("PlatformTitles.InvalidCursor");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("broken")]
    [InlineData("e30")]
    public void Create_MalformedOrBlankCursor_Rejects(string cursor) =>
        PlatformTitlePageRequest.Create(1, null, false, cursor).IsError.ShouldBeTrue();

    [Theory]
    [InlineData("Version", "2")]
    [InlineData("Sort", "\"name:desc\"")]
    [InlineData("Id", "0")]
    [InlineData("Id", "-1")]
    [InlineData("NameUtf8", "null")]
    [InlineData("NameUtf8", "\"*\"")]
    [InlineData("NameUtf8", "\"wA==\"")]
    [InlineData("Extra", "true")]
    public void Create_InvalidCursorEnvelope_Rejects(string field, string value)
    {
        string cursor = PlatformTitlePageRequest.Create(1, null, false).Value.CursorFor("Title", 42);
        string json = Encoding.UTF8.GetString(Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/') + new string('=', (4 - cursor.Length % 4) % 4)));
        var data = JsonNode.Parse(json)!.AsObject();
        data[field] = JsonNode.Parse(value);
        string invalid = Convert.ToBase64String(Encoding.UTF8.GetBytes(data.ToJsonString())).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        PlatformTitlePageRequest.Create(1, null, false, invalid).IsError.ShouldBeTrue();
    }

    [Fact]
    public void Create_MissingCursorFieldOrOversizeCursor_Rejects()
    {
        string missing = CursorUtils.ToCursor(new { Version = 1, PlatformId = 1, LocalPayloadOnly = false, Sort = "name:asc", Name = "Title", Id = 42 });
        PlatformTitlePageRequest.Create(1, null, false, missing).IsError.ShouldBeTrue();
        PlatformTitlePageRequest.Create(1, null, false, new string('a', 8193)).IsError.ShouldBeTrue();
    }
}
