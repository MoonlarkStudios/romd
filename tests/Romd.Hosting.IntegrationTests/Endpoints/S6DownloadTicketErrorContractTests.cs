using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Host.Endpoints;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class S6DownloadTicketErrorContractTests(IntegrationTestFixture fixture)
{
    [Theory]
    [InlineData("unknown-ticket")]
    [InlineData("invalid-completed-plan")]
    [InlineData("missing-completed-file")]
    [InlineData("missing-completed-directory")]
    [InlineData("missing-file-stream")]
    [InlineData("missing-archive-entry")]
    public async Task Redemption_UnavailableReason_ReturnsTheSameContextFreeProblem(string reason)
    {
        const string token = "private-capability-do-not-reflect";
        var nonexistent = Guid.NewGuid().ToString("N");
        AdminDownloadPlan? plan = reason switch
        {
            "unknown-ticket" => null,
            "invalid-completed-plan" => new("private.zip", [], true, "private/relative/path.zip"),
            "missing-completed-file" => new("private.zip", [], true, Path.Combine(Path.GetTempPath(), nonexistent + ".zip")),
            "missing-completed-directory" => new("private.zip", [], true, Path.Combine(Path.GetTempPath(), nonexistent, "private.zip")),
            "missing-file-stream" => new("private.bin", [new(1, "private.bin")], false),
            "missing-archive-entry" => new("private.zip", [new(1, "private.bin")], true),
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };
        var tickets = Substitute.For<IAdminDownloadTickets>();
        tickets.Resolve(token).Returns(plan);
        var storage = Substitute.For<IFileStorageService>();
        var reader = Substitute.For<IRomContentReader>();
        var writer = Substitute.For<IExportArchiveWriter>();
        var context = new DefaultHttpContext();
        var method = typeof(ExportEndpoints).GetMethod("RedeemDownload", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = await (Task<IResult>)method.Invoke(null,
            [token, context, tickets, writer, storage, reader, CancellationToken.None])!;

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(404);
        var problem = result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>().Value.ShouldNotBeNull();
        problem.Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe("Delivery.ContentNotFound");
        problem.Detail.ShouldBe("The download is unavailable.");
        context.Response.Headers.CacheControl.ToString().ShouldBe("private, no-store");
        context.Response.Headers["Referrer-Policy"].ToString().ShouldBe("no-referrer");
        writer.ReceivedCalls().ShouldBeEmpty();
        if (reason is "unknown-ticket" or "invalid-completed-plan" or "missing-completed-file" or "missing-completed-directory")
            storage.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Redemption_AnonymousUnknownTicket_WritesPrivateEnvelopeWithoutReflectingCapability()
    {
        const string token = "private-capability-do-not-reflect";
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync("/api/download-tickets/" + token);
        using var body = await ProblemEnvelope.ReadAsync(response, 404, "Delivery.ContentNotFound");
        body.RootElement.GetProperty("detail").GetString().ShouldBe("The download is unavailable.");
        body.RootElement.GetRawText().ShouldNotContain(token);
        response.Headers.CacheControl.ShouldNotBeNull().Private.ShouldBeTrue();
        response.Headers.CacheControl.NoStore.ShouldBeTrue();
    }
}
