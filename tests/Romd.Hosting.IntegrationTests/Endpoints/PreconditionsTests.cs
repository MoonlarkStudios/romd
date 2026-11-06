using ErrorOr;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Romd.Application.Common.Validation;
using Romd.Host.Endpoints;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class PreconditionsTests
{
    [Theory]
    [InlineData("\"opaque,tag\"", "opaque,tag", true)]
    [InlineData("\"opaque,tag\"", "opaque", false)]
    [InlineData(", \"opaque,tag\", ,", "opaque,tag", true)]
    [InlineData("\"*\"", "*", true)]
    [InlineData("\"AbC\"", "abc", false)]
    [InlineData("\"AbC\"", "AbC", true)]
    [InlineData("W/\"current\", \"stale\"", "current", false)]
    [InlineData("W/\"current\", \"current\"", "current", true)]
    public void IfMatch_ValidTagList_PreservesOpaqueStrongComparison(string header, string currentRevision, bool matches)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.IfMatch = header;

        var precondition = Preconditions.IfMatch(context, "Probe");

        precondition.IsError.ShouldBeFalse();
        precondition.Value.IsSatisfiedBy(currentRevision).ShouldBe(matches);
    }

    [Fact]
    public void IfMatch_MultipleHeaderLines_MatchesAnyStrongTag()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.IfMatch = new StringValues(["\"other\"", "W/\"current\", \"current\""]);

        var precondition = Preconditions.IfMatch(context, "Probe");

        precondition.IsError.ShouldBeFalse();
        precondition.Value.IsSatisfiedBy("current").ShouldBeTrue();
    }

    [Theory]
    [InlineData(",")]
    [InlineData(", ,")]
    [InlineData("*,")]
    [InlineData(", *")]
    [InlineData(" , * , ")]
    public void IfMatch_MalformedRawList_IsAttributedInvalidInput(string header)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.IfMatch = header;

        var precondition = Preconditions.IfMatch(context, "Probe");

        precondition.IsError.ShouldBeTrue();
        precondition.FirstError.Type.ShouldBe(ErrorType.Validation);
        precondition.FirstError.Code.ShouldBe(ProblemCodes.RequestInvalidParameter);
        FieldErrors.FieldOf(precondition.FirstError).ShouldBe("If-Match");
    }
}
