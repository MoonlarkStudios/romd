using Romd.Consumer.Application.Vfs;
using Romd.Domain.Vfs;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Consumer.Vfs;

public sealed class VfsFormatsTests
{
    [Theory]
    [InlineData("stored", VfsFormat.Stored)]
    [InlineData("original", VfsFormat.Original)]
    public void Parse_ExactPublicId_RoundTrips(string value, VfsFormat expected)
    {
        var result = VfsFormats.Parse(value);
        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(expected);
        VfsFormats.ToWire(result.Value).ShouldBe(value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Stored")]
    [InlineData(" original")]
    [InlineData("original ")]
    [InlineData("rvz")]
    [InlineData("0")]
    [InlineData("unknown")]
    public void Parse_InvalidPublicId_ReturnsValidation(string? value) =>
        VfsFormats.Parse(value).FirstError.ShouldBe(VfsConnectionErrors.InvalidFormat());
}
