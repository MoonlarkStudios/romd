using Romd.Application.Common.Pagination;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Jobs;

public sealed class S5cKeysetCursorTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not-base64-json")]
    [InlineData("e30")]
    [InlineData("bnVsbA")]
    public void Decode_InvalidOrMissingFields_IsAnError(string cursor) =>
        KeysetCursor.Decode<DateIdPosition>(cursor, "filter", position => position.IsValid).IsError.ShouldBeTrue();

    [Fact]
    public void Decode_CanonicalPosition_IsBoundToEndpointSortAndVisibility()
    {
        var position = new DateIdPosition(DateTimeOffset.Parse("2026-10-01T00:00:00Z"), Guid.NewGuid());
        var fingerprint = KeysetCursor.Fingerprint("jobs", "created-desc", "owner-1");
        var cursor = KeysetCursor.Encode(position, fingerprint);
        KeysetCursor.Decode<DateIdPosition>(cursor, fingerprint, value => value.IsValid).Value.ShouldBe(position);
        foreach (var incompatible in new[] { KeysetCursor.Fingerprint("items", "created-desc", "owner-1"),
            KeysetCursor.Fingerprint("jobs", "created-asc", "owner-1"), KeysetCursor.Fingerprint("jobs", "created-desc", "owner-2") })
            KeysetCursor.Decode<DateIdPosition>(cursor, incompatible, value => value.IsValid).IsError.ShouldBeTrue();
        foreach (string invalid in new[] { cursor + "=", cursor + "AA", new string('a', KeysetCursor.MaxLength + 1) })
            KeysetCursor.Decode<DateIdPosition>(invalid, fingerprint, value => value.IsValid).IsError.ShouldBeTrue();
    }

    [Fact]
    public void Decode_DefaultOrInvalidPosition_IsRejected()
    {
        foreach (var position in new[] { new DateIdPosition(default, Guid.NewGuid()), new DateIdPosition(DateTimeOffset.UtcNow, Guid.Empty) })
            KeysetCursor.Decode<DateIdPosition>(KeysetCursor.Encode(position, "filter"), "filter", value => value.IsValid).IsError.ShouldBeTrue();
        KeysetCursor.Decode<IntegerPosition>(KeysetCursor.Encode(new IntegerPosition(0), "filter"), "filter", value => value.IsValid).IsError.ShouldBeTrue();
    }
}
