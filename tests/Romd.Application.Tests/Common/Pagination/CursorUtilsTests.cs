using System.Text;
using Romd.Application.Common.Pagination;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Common.Pagination;

public sealed class CursorUtilsTests
{
    private sealed record Position(int Version, string Name);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    [Fact]
    public void TryReadCanonical_RoundTripsTheCanonicalEncoding()
    {
        string cursor = CursorUtils.ToCursor(new Position(1, "Équal \U0001F600"));
        CursorUtils.TryReadCanonical(cursor, 8192, out Position? value).ShouldBeTrue();
        value.ShouldBe(new Position(1, "Équal \U0001F600"));
    }

    [Fact]
    public void TryReadCanonical_RejectsAnEquivalentButNonCanonicalEncoding()
    {
        string spaced = Base64Url(Encoding.UTF8.GetBytes("{ \"Version\": 1, \"Name\": \"a\" }"));
        CursorUtils.TryReadCanonical<Position>(spaced, 8192, out _).ShouldBeFalse();
        CursorUtils.TryReadCanonical<Position>(CursorUtils.ToCursor(new Position(1, "a")) + "=", 8192, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("plus+slash/")]
    [InlineData("A")]
    public void TryReadCanonical_RejectsMalformedCursors(string? cursor) =>
        CursorUtils.TryReadCanonical<Position>(cursor, 8192, out _).ShouldBeFalse();

    [Fact]
    public void TryReadCanonical_RejectsCursorsLongerThanTheLimit()
    {
        string cursor = CursorUtils.ToCursor(new Position(1, "a"));
        CursorUtils.TryReadCanonical<Position>(cursor, cursor.Length, out _).ShouldBeTrue();
        CursorUtils.TryReadCanonical<Position>(cursor, cursor.Length - 1, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryReadCanonical_RejectsInvalidUtf8AndNonJsonPayloads()
    {
        CursorUtils.TryReadCanonical<Position>(Base64Url([0x7B, 0xFF, 0x7D]), 8192, out _).ShouldBeFalse();
        CursorUtils.TryReadCanonical<Position>(Base64Url(Encoding.UTF8.GetBytes("not json")), 8192, out _).ShouldBeFalse();
        CursorUtils.TryReadCanonical<Position>(Base64Url(Encoding.UTF8.GetBytes("null")), 8192, out _).ShouldBeFalse();
    }
}
