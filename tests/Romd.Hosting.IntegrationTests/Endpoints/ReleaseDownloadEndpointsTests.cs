using Romd.Application.Common.Content;
using System.IO.Compression;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Storage.Files;
using Romd.Host.Endpoints;
using Romd.Infrastructure.Export;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class ReleaseDownloadEndpointsTests
{
    [Fact]
    public async Task RedeemDownload_ValidTicket_StreamsZipWithSafeHeaders()
    {
        var storage = Substitute.For<IFileStorageService>();
        storage.ExistsAsync(1, Arg.Any<CancellationToken>()).Returns(true);
        storage.RetrieveByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new MemoryStream([1, 2, 3]));
        using var tickets = new AdminDownloadTickets(TimeProvider.System);
        var ticket = tickets.Issue(new("release.zip", [new(1, "disc/file.bin")], true));
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        var result = await Invoke(ticket.DownloadUrl.Split('/').Last(), context, tickets, storage);
        await result.ExecuteAsync(context);
        context.Response.ContentType.ShouldBe("application/zip");
        context.Response.Headers.ContentDisposition.ToString().ShouldContain("release.zip");
        context.Response.Headers.CacheControl.ToString().ShouldContain("no-store");
        context.Response.Body.Position = 0;
        using var archive = new ZipArchive(context.Response.Body);
        archive.Entries.Single().FullName.ShouldBe("disc/file.bin");
    }

    [Fact]
    public async Task RedeemDownload_InvalidTicket_DoesNotReadStorage()
    {
        using var tickets = new AdminDownloadTickets(TimeProvider.System);
        var storage = Substitute.For<IFileStorageService>();
        var result = await Invoke(new string('0', 64), new DefaultHttpContext(), tickets, storage);
        result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(404);
        storage.ReceivedCalls().ShouldBeEmpty();
    }

    private static async Task<IResult> Invoke(string token, HttpContext context, IAdminDownloadTickets tickets, IFileStorageService storage)
    {
        var method = typeof(ExportEndpoints).GetMethod("RedeemDownload", BindingFlags.Static | BindingFlags.NonPublic)!;
        return await (Task<IResult>)method.Invoke(null, [token, context, tickets, new ExportArchiveWriter(storage, Substitute.For<Romd.Application.Common.Content.IRomContentReader>()), storage, Substitute.For<IRomContentReader>(), CancellationToken.None])!;
    }
}
