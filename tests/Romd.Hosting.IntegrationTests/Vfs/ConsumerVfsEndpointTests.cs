using Romd.PostgreSql.TestSupport;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Content;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Libraries;
using Romd.Consumer.Application.Vfs;
using Romd.Domain.Hashing;
using Romd.Domain.Vfs;
using Romd.Domain.Source.Rom;
using Romd.Host.Authorization;
using Romd.Host.Endpoints;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Hosting.Vfs;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Storage;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Vfs;

public sealed class ConsumerVfsEndpointTests
{
    private static readonly XNamespace Dav = "DAV:";

    [Fact]
    public async Task Propfind_ContentCapacityExhausted_UsesReservedMetadataCapacity()
    {
        await using var host = await Harness.Start();
        var admission = host.App.Services.GetRequiredService<ConsumerVfsAdmission>();
        using var occupied = await admission.EnterAsync(Guid.NewGuid(), Guid.NewGuid(), default);
        using var listing = await host.Propfind(host.Folder, "1");
        listing.StatusCode.ShouldBe(HttpStatusCode.MultiStatus);
        using var content = await host.Client.GetAsync(host.FileUrl);
        content.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Get_NativeReadAdmissionFull_Returns503AndClearsFileHeaders_HeadRemainsAvailable()
    {
        var content = Substitute.For<IConsumerVfsContentResolver>();
        content.OpenAsync(Arg.Any<ConsumerVfsFile>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Stream?>(new Romd.Application.Common.Content.ResourceCapacityException("native capacity")));
        await using var host = await Harness.Start(content: content);
        using var response = await host.Client.GetAsync(host.FileUrl);
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(15));
        response.Headers.ETag.ShouldBeNull();
        response.Content.Headers.ContentRange.ShouldBeNull();
        using var head = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, host.FileUrl));
        head.StatusCode.ShouldBe(HttpStatusCode.OK);
        content.ReceivedCalls().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Get_Range_RecordsActualResponseBodyBytes()
    {
        await using var host = await Harness.Start();
        using var metrics = new VfsProbeMetrics();
        using var request = new HttpRequestMessage(HttpMethod.Get, host.FileUrl);
        request.Headers.Range = new RangeHeaderValue(10, 19);
        using var response = await host.Client.SendAsync(request);
        (await response.Content.ReadAsByteArrayAsync()).Length.ShouldBe(10);
        metrics.ResponseBytes.ShouldBe(10);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Get_LayoutSuppliesUnauthorizedContent_DoesNotOpenCas(bool withdrawAccess)
    {
        var layout = Substitute.For<IVfsLayout>();
        layout.Key.Returns("untrusted");
        var file = new ConsumerVfsFile("100% # game.bin", Sha256.Parse(new string(withdrawAccess ? 'a' : 'b', 64)), 256);
        layout.ReadAsync(Arg.Any<ConsumerLibraryScope>(), "file", false, 20_000, Arg.Any<CancellationToken>())
            .Returns(new VfsLayoutRead(new("file", "file", file), []));
        var cas = Substitute.For<IContentAddressableStore>();
        await using var host = await Harness.Start(layout: layout, cas: cas);
        if (withdrawAccess)
            host.Repository.GetReleaseAsync(Arg.Any<ConsumerLibraryScope>(), 1, Arg.Any<CancellationToken>())
                .Returns(new ConsumerLibraryReadResult<ConsumerVfsRelease>.ItemNotFound(1));
        using var response = await host.Client.GetAsync("/vfs/untrusted/file");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        cas.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Get_File_ResolvesCatalogAccessOnce()
    {
        byte[] bytes = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        var cas = Substitute.For<IContentAddressableStore>();
        cas.OpenRandomAccessAsync(Arg.Any<StorageKey>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IRandomAccessContent?>(new BytesContent(bytes)));
        await using var host = await Harness.Start(cas: cas);
        using var response = await host.Client.GetAsync(host.FileUrl);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(bytes);
        await host.Repository.Received(1).GetFileAsync(
            Arg.Any<ConsumerLibraryScope>(), 1, "100% # game.bin", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Startup_VfsDisabled_DoesNotConstructDatabaseBackedLayouts()
    {
        // Hosts without an application database (OpenAPI generation, anonymous-only
        // hosts) must start while VFS is disabled; layouts depend on the database.
        await using var host = await Harness.Start(enabled: false, configureServices: services =>
            services.AddScoped<IVfsLayout>(_ => throw new InvalidOperationException("ConnectionStrings:Romd must be configured.")));
        using var response = await host.Client.GetAsync(host.FileUrl);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_DevelopmentAuthenticationDisabled_DeniesBeforeCatalogAccess()
    {
        await using var host = await Harness.Start();
        host.App.Services.GetRequiredService<ConsumerVfsOptions>().AllowDevelopmentBearer = false;
        using var response = await host.Client.GetAsync(host.FileUrl);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Single().Scheme.ShouldBe("Basic");
        host.Repository.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Get_StorageIntegrityFailure_ReturnsServerError()
    {
        var resolver = Substitute.For<IConsumerVfsContentResolver>();
        resolver.OpenAsync(Arg.Any<ConsumerVfsFile>(), Arg.Any<CancellationToken>())
            .Returns<Stream?>(_ => throw new InvalidDataException("Stored length mismatch"));
        await using var host = await Harness.Start(content: resolver);
        using var response = await host.Client.GetAsync(host.FileUrl);
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Headers.ETag.ShouldBeNull();
        response.Content.Headers.ContentRange.ShouldBeNull();
        (await response.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("GET", "If-None-Match", "match", 304)]
    [InlineData("GET", "If-None-Match", "weak", 304)]
    [InlineData("GET", "If-None-Match", "*", 304)]
    [InlineData("HEAD", "If-None-Match", "match", 304)]
    [InlineData("GET", "If-Match", "different", 412)]
    [InlineData("GET", "If-Match", "weak", 412)]
    [InlineData("HEAD", "If-Match", "different", 412)]
    public async Task Conditional_NoBodyRequired_DoesNotOpenContent(string method, string header, string validator, int status)
    {
        var resolver = Substitute.For<IConsumerVfsContentResolver>();
        resolver.OpenAsync(Arg.Any<ConsumerVfsFile>(), Arg.Any<CancellationToken>())
            .Returns<Stream?>(_ => throw new InvalidOperationException("Native reader must not open"));
        await using var host = await Harness.Start(content: resolver);
        string etag = "\"" + new string('a', 64) + "\"";
        using var request = new HttpRequestMessage(new HttpMethod(method), host.FileUrl);
        request.Headers.TryAddWithoutValidation(header, validator switch
        {
            "match" => etag,
            "weak" => "W/" + etag,
            "different" => "\"different\"",
            _ => validator
        });
        using var response = await host.Client.SendAsync(request);
        ((int)response.StatusCode).ShouldBe(status);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();
        resolver.ReceivedCalls().ShouldBeEmpty();
        await host.Repository.Received(1).GetFileAsync(
            Arg.Any<ConsumerLibraryScope>(), 1, "100% # game.bin", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true, 206, 10)]
    [InlineData(false, 200, 256)]
    public async Task Get_IfRange_UsesValidatorToSelectRangeOrFullBody(bool matches, int status, int length)
    {
        await using var host = await Harness.Start();
        using var request = new HttpRequestMessage(HttpMethod.Get, host.FileUrl);
        request.Headers.TryAddWithoutValidation("If-Range", "\"" + new string(matches ? 'a' : 'b', 64) + "\"");
        request.Headers.Range = new RangeHeaderValue(10, 19);
        using var response = await host.Client.SendAsync(request);
        ((int)response.StatusCode).ShouldBe(status);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(matches ? host.Resolver.Bytes[10..20] : host.Resolver.Bytes);
        response.Content.Headers.ContentLength.ShouldBe(length);
        host.Resolver.Opened.ShouldBe(1);
        host.Resolver.Last!.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Head_LargeLogicalDisc_UsesMetadataEvenWhenContentCapacityIsExhausted()
    {
        const long length = 4_699_979_776;
        await using var host = await Harness.Start(file: new("100% # game.bin", Sha256.Parse(new string('a', 64)), length));
        using var occupied = await host.App.Services.GetRequiredService<ConsumerVfsAdmission>()
            .EnterAsync(Guid.NewGuid(), Guid.NewGuid(), default);
        using var request = new HttpRequestMessage(HttpMethod.Head, host.FileUrl);
        request.Headers.Range = new RangeHeaderValue(10, 19);
        // ResponseHeadersRead avoids HttpClient trying to buffer a multi-gigabyte HEAD response.
        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentLength.ShouldBe(length);
        response.Headers.AcceptRanges.ShouldContain("bytes");
        await using var body = await response.Content.ReadAsStreamAsync();
        (await body.ReadAsync(new byte[1])).ShouldBe(0);
        host.Resolver.Opened.ShouldBe(0);
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("GET")]
    public async Task MetadataResponse_RevokedAccess_DeniesBeforeReturningValidator(string method)
    {
        await using var host = await Harness.Start();
        host.Repository.GetReleaseAsync(Arg.Any<ConsumerLibraryScope>(), 1, Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsRelease>.ItemNotFound(1));
        using var request = new HttpRequestMessage(new HttpMethod(method), host.FileUrl);
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var response = await host.Client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Headers.ETag.ShouldBeNull();
        host.Resolver.Opened.ShouldBe(0);
    }

    [Fact]
    public async Task Get_MissingArtifact_ClearsSuccessHeaders()
    {
        var resolver = Substitute.For<IConsumerVfsContentResolver>();
        resolver.OpenAsync(Arg.Any<ConsumerVfsFile>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<Stream?>(null));
        await using var host = await Harness.Start(content: resolver);
        using var request = new HttpRequestMessage(HttpMethod.Get, host.FileUrl);
        request.Headers.Range = new RangeHeaderValue(10, 19);
        using var response = await host.Client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentRange.ShouldBeNull();
        response.Headers.ETag.ShouldBeNull();
        (await response.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Propfind_ContentMetadata_ExposesBothChecksumsAndStableModificationTime()
    {
        var file = new ConsumerVfsFile("100% # game.bin", Sha256.Parse(new string('a', 64)), 256,
            Md5.Parse(new string('b', 32)), Sha1.Parse(new string('c', 40)),
            new DateTimeOffset(2026, 9, 28, 12, 30, 0, TimeSpan.Zero));
        await using var host = await Harness.Start(file: file);
        using var response = await host.Propfind(host.FileUrl, "0",
            "<D:propfind xmlns:D=\"DAV:\" xmlns:oc=\"http://owncloud.org/ns\"><D:prop><D:getlastmodified/><oc:checksums/><D:getetag/></D:prop></D:propfind>");
        response.StatusCode.ShouldBe(HttpStatusCode.MultiStatus);
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        xml.Descendants(Dav + "getlastmodified").Single().Value.ShouldBe("Mon, 28 Sep 2026 12:30:00 GMT");
        xml.Descendants(XName.Get("checksum", "http://owncloud.org/ns")).Single().Value
            .ShouldBe($"MD5:{file.Md5} SHA1:{file.Sha1}");
        xml.Descendants(Dav + "getetag").Single().Value.ShouldBe($"\"{file.Sha256}\"");
        host.Resolver.Opened.ShouldBe(0);
    }

    [Fact]
    public async Task Propfind_RegisteredLayout_DispatchesWithDecodedPathAndPreservesUrlPrefix()
    {
        var layout = Substitute.For<IVfsLayout>();
        layout.Key.Returns("example");
        layout.ReadAsync(Arg.Any<ConsumerLibraryScope>(), "100% games", true, 20_000, Arg.Any<CancellationToken>())
            .Returns(new VfsLayoutRead(new("100% games", "Games"), [new("100% games/discs", "Discs")]));
        await using var host = await Harness.Start(layout: layout);

        using var response = await host.Propfind("/vfs/example/100%25%20games/", "1");

        response.StatusCode.ShouldBe(HttpStatusCode.MultiStatus);
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        xml.Descendants(Dav + "href").Select(element => element.Value).ShouldBe([
            "/vfs/example/100%25%20games/", "/vfs/example/100%25%20games/discs/"]);
        host.Repository.ReceivedCalls().ShouldBeEmpty();
        host.Resolver.Opened.ShouldBe(0);
    }

    [Theory]
    [InlineData("/vfs/unknown/")]
    [InlineData("/vfs/LIBRARY/")]
    [InlineData("/vfs/legacy/")]
    [InlineData("/vfs/")]
    public async Task Propfind_UnknownLayout_Returns404WithoutRepositoryOrContentAccess(string path)
    {
        await using var host = await Harness.Start();

        using var response = await host.Propfind(path, "1");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        host.Repository.ReceivedCalls().ShouldBeEmpty();
        host.Resolver.Opened.ShouldBe(0);
    }

    [Fact]
    public async Task Get_UnkeyedReleasePath_Returns404WithoutRepositoryOrContentAccess()
    {
        await using var host = await Harness.Start();
        string oldFileUrl = host.FileUrl.Replace("/vfs/library/", "/vfs/", StringComparison.Ordinal);

        using var response = await host.Client.GetAsync(oldFileUrl);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        host.Repository.ReceivedCalls().ShouldBeEmpty();
        host.Resolver.Opened.ShouldBe(0);
    }

    [Fact]
    public async Task Propfind_RootAndRelease_ListsEncodedCompanionsWithoutOpeningContent()
    {
        await using var host = await Harness.Start();
        using var response = await host.Propfind("/vfs/library/", "1");
        response.StatusCode.ShouldBe(HttpStatusCode.MultiStatus);
        var rootXml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        rootXml.Descendants(Dav + "href").Select(element => element.Value).ShouldBe(["/vfs/library/", host.PlatformFolder]);
        using var platform = await host.Propfind(host.PlatformFolder, "1");
        var platformXml = XDocument.Parse(await platform.Content.ReadAsStringAsync());
        platformXml.Descendants(Dav + "href").Select(element => element.Value)
            .ShouldBe([host.PlatformFolder, host.Folder]);
        using var release = await host.Propfind(host.Folder, "1");
        var xml = XDocument.Parse(await release.Content.ReadAsStringAsync());
        xml.Descendants(Dav + "href").Select(element => element.Value)
            .ShouldContain(host.FileUrl);
        host.Resolver.Opened.ShouldBe(0);
    }

    [Theory]
    [InlineData("bytes=10-19", 10, 10)]
    [InlineData("bytes=-10", 246, 10)]
    [InlineData("bytes=250-", 250, 6)]
    public async Task Get_Range_ReturnsExactBytesAndDisposesReader(string range, int offset, int length)
    {
        await using var host = await Harness.Start();
        using var request = new HttpRequestMessage(HttpMethod.Get, host.FileUrl);
        request.Headers.TryAddWithoutValidation("Range", range);
        using var response = await host.Client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange!.Length.ShouldBe(256);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(host.Resolver.Bytes[offset..(offset + length)]);
        host.Resolver.Last!.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Get_UnsatisfiableRange_Returns416()
    {
        await using var host = await Harness.Start();
        using var request = new HttpRequestMessage(HttpMethod.Get, host.FileUrl);
        request.Headers.Range = new RangeHeaderValue(300, 400);
        using var response = await host.Client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.RequestedRangeNotSatisfiable);
        response.Content.Headers.ContentRange!.Length.ShouldBe(256);
        host.Resolver.Opened.ShouldBe(0);
    }

    [Fact]
    public async Task Head_ReportsSizeAndEtagWithoutBody()
    {
        await using var host = await Harness.Start();
        using var response = await host.Client.SendAsync(new(HttpMethod.Head, host.FileUrl));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentLength.ShouldBe(256);
        response.Headers.ETag.ShouldNotBeNull();
        (await response.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();
        host.Resolver.Opened.ShouldBe(0);
    }

    [Fact]
    public async Task Get_ConditionalStillChecksCurrentAccess()
    {
        await using var host = await Harness.Start();
        using var first = await host.Client.GetAsync(host.FileUrl);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, host.FileUrl);
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var unchanged = await host.Client.SendAsync(conditional);
        unchanged.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        host.Resolver.Opened.ShouldBe(1);
        int opened = host.Resolver.Opened;
        host.Repository.GetReleaseAsync(Arg.Any<ConsumerLibraryScope>(), 1, Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsRelease>.ItemNotFound(1));
        using var revoked = new HttpRequestMessage(HttpMethod.Get, host.FileUrl);
        revoked.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var denied = await host.Client.SendAsync(revoked);
        denied.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        host.Resolver.Opened.ShouldBe(opened);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("MKCOL")]
    [InlineData("MOVE")]
    [InlineData("COPY")]
    [InlineData("LOCK")]
    [InlineData("PROPPATCH")]
    public async Task Mutation_Returns405WithoutOpeningContent(string method)
    {
        await using var host = await Harness.Start();
        using var response = await host.Client.SendAsync(new(new(method), host.FileUrl));
        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        host.Resolver.Opened.ShouldBe(0);
        host.Repository.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task AnonymousRequest_IsChallengedBeforeLibraryLookup()
    {
        await using var host = await Harness.Start();
        host.Client.DefaultRequestHeaders.Remove(TestAuthHandler.UserIdHeader);
        using var response = await host.Client.GetAsync(host.FileUrl);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        host.Repository.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("infinity", 403)]
    [InlineData("", 403)]
    [InlineData("2", 400)]
    public async Task Propfind_UnsupportedDepth_FailsBoundedly(string depth, int status)
    {
        await using var host = await Harness.Start();
        using var response = await host.Propfind("/vfs/library/", depth);
        ((int)response.StatusCode).ShouldBe(status);
        host.Repository.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("<bad>", 400)]
    [InlineData("<!DOCTYPE a [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><D:propfind xmlns:D='DAV:'><D:allprop>&e;</D:allprop></D:propfind>", 400)]
    [InlineData("large", 413)]
    public async Task Propfind_InvalidXml_IsRejectedBeforeLookup(string body, int status)
    {
        await using var host = await Harness.Start();
        using var response = await host.Propfind("/vfs/library/", "1", body == "large" ? new string('x', 17000) : body);
        ((int)response.StatusCode).ShouldBe(status);
        host.Repository.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Propfind_UnknownProperties_ReturnsPerProperty404()
    {
        await using var host = await Harness.Start();
        using var response = await host.Propfind(host.FileUrl, "0",
            "<D:propfind xmlns:D='DAV:'><D:prop><D:getcontentlength/><D:unknown/></D:prop></D:propfind>");
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        xml.Descendants(Dav + "status").Select(element => element.Value)
            .ShouldBe(["HTTP/1.1 200 OK", "HTTP/1.1 404 Not Found"]);
    }

    [Fact]
    public async Task Admission_SameConnectionFormatChangesWhileQueued_UsesFreshOriginalMetadataAndBody()
    {
        var files = DeliveryFiles();
        var connections = Substitute.For<IVfsConnections>();
        var userId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        string username = $"romd-{connectionId:N}";
        int selection = (int)VfsFormat.Stored;
        int checks = 0;
        var authenticated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connections.AuthenticateAsync(username, "secret", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref checks);
            authenticated.TrySetResult();
            return Task.FromResult<AuthenticatedVfsConnection?>(new(userId, connectionId, (VfsFormat)Volatile.Read(ref selection)));
        });
        var resolver = Substitute.For<IConsumerVfsContentResolver>();
        resolver.OpenAsync(files.Original, Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(files.OriginalBytes));
        await using var host = await Harness.Start(connections: connections, content: resolver);
        ConfigureFormatRepository(host, files.Original, files.Stored);
        UseBasic(host, username);
        var admission = host.App.Services.GetRequiredService<ConsumerVfsAdmission>();
        using var blocker = await admission.EnterAsync(Guid.NewGuid(), null, default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = host.Client.GetAsync(FormatUrl(host, files.Original), deadline.Token);
        await authenticated.Task.WaitAsync(deadline.Token);
        while (admission.QueuedRequestCount == 0)
            await Task.Delay(1, deadline.Token);
        host.Repository.ReceivedCalls().ShouldBeEmpty();
        resolver.ReceivedCalls().ShouldBeEmpty();
        Volatile.Write(ref selection, (int)VfsFormat.Original);
        blocker!.Dispose();
        using var response = await pending;
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentLength.ShouldBe(files.Original.Length);
        response.Headers.ETag!.Tag.ShouldBe($"\"{files.Original.Sha256}\"");
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(files.OriginalBytes);
        checks.ShouldBe(2);
        await host.Repository.Received(1).GetFileAsync(Arg.Any<ConsumerLibraryScope>(), 1, files.Original.RelativePath,
            Arg.Any<CancellationToken>(), VfsFormat.Original);
        await resolver.Received(1).OpenAsync(files.Original, Arg.Any<CancellationToken>());
        using var oldStored = await host.Client.GetAsync(FormatUrl(host, files.Stored));
        oldStored.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        resolver.ReceivedCalls().Count().ShouldBe(1);
        using var validator = new HttpRequestMessage(HttpMethod.Head, FormatUrl(host, files.Original));
        validator.Headers.IfNoneMatch.Add(new($"\"{files.Stored.Sha256}\""));
        using var fresh = await host.Client.SendAsync(validator);
        fresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        fresh.Headers.ETag!.Tag.ShouldBe($"\"{files.Original.Sha256}\"");
        fresh.Content.Headers.ContentLength.ShouldBe(files.Original.Length);
        resolver.ReceivedCalls().Count().ShouldBe(1);
    }

    [Theory]
    [InlineData(VfsFormat.Stored, "HEAD", 200)]
    [InlineData(VfsFormat.Original, "HEAD", 200)]
    [InlineData(VfsFormat.Stored, "PROPFIND", 207)]
    [InlineData(VfsFormat.Original, "PROPFIND", 207)]
    [InlineData(VfsFormat.Stored, "304", 304)]
    [InlineData(VfsFormat.Original, "304", 304)]
    [InlineData(VfsFormat.Stored, "412", 412)]
    [InlineData(VfsFormat.Original, "412", 412)]
    [InlineData(VfsFormat.Stored, "416", 416)]
    [InlineData(VfsFormat.Original, "416", 416)]
    public async Task Format_MetadataOnlyResponses_UseDeliveredFactsWithoutResolverCasOrDecoder(VfsFormat format, string method, int status)
    {
        var files = DeliveryFiles();
        var file = format == VfsFormat.Stored ? files.Stored : files.Original;
        var connections = Substitute.For<IVfsConnections>();
        var connectionId = Guid.NewGuid();
        string username = $"romd-{connectionId:N}";
        connections.AuthenticateAsync(username, "secret", Arg.Any<CancellationToken>())
            .Returns(new AuthenticatedVfsConnection(Guid.NewGuid(), connectionId, format));
        var cas = Substitute.For<IContentAddressableStore>();
        var roms = Substitute.For<IRomContentReader>();
        var native = new ConsumerVfsContentResolver(cas, roms);
        var resolver = Substitute.For<IConsumerVfsContentResolver>();
        resolver.OpenAsync(Arg.Any<ConsumerVfsFile>(), Arg.Any<CancellationToken>())
            .Returns(call => native.OpenAsync(call.ArgAt<ConsumerVfsFile>(0), call.ArgAt<CancellationToken>(1)));
        await using var host = await Harness.Start(connections: connections, content: resolver);
        ConfigureFormatRepository(host, files.Original, files.Stored);
        UseBasic(host, username);
        using var request = new HttpRequestMessage(new HttpMethod(method is "304" or "412" or "416" ? "GET" : method), FormatUrl(host, file));
        if (method == "PROPFIND")
        {
            request.Headers.Add("Depth", "0");
            request.Content = new StringContent("<D:propfind xmlns:D='DAV:' xmlns:oc='http://owncloud.org/ns'><D:prop><D:getcontentlength/><D:getetag/><oc:checksums/></D:prop></D:propfind>", Encoding.UTF8, "application/xml");
        }
        if (method == "304")
            request.Headers.IfNoneMatch.Add(new($"\"{file.Sha256}\""));
        if (method == "412")
            request.Headers.IfMatch.Add(new("\"different\""));
        if (method is "HEAD" or "416")
            request.Headers.Range = new(file.Length, file.Length + 1);
        using var response = await host.Client.SendAsync(request);
        ((int)response.StatusCode).ShouldBe(status);
        if (method == "HEAD")
        {
            response.Content.Headers.ContentLength.ShouldBe(file.Length);
            response.Headers.ETag!.Tag.ShouldBe($"\"{file.Sha256}\"");
            response.Content.Headers.ContentRange.ShouldBeNull();
        }
        else if (method == "PROPFIND")
        {
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
            xml.Descendants(Dav + "getcontentlength").Single().Value.ShouldBe(file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            xml.Descendants(Dav + "getetag").Single().Value.ShouldBe($"\"{file.Sha256}\"");
            xml.Descendants(XName.Get("checksum", "http://owncloud.org/ns")).Single().Value.ShouldBe($"MD5:{file.Md5} SHA1:{file.Sha1}");
        }
        else
            (await response.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();
        resolver.ReceivedCalls().ShouldBeEmpty();
        cas.ReceivedCalls().ShouldBeEmpty();
        roms.ReceivedCalls().ShouldBeEmpty();
        await host.Repository.Received(1).GetFileAsync(Arg.Any<ConsumerLibraryScope>(), 1, file.RelativePath,
            Arg.Any<CancellationToken>(), format);
    }

    [Theory]
    [InlineData(VfsFormat.Stored)]
    [InlineData(VfsFormat.Original)]
    public async Task Format_FullObjectAndRange_OpenBoundBytesAndKeepDeliveredHeaders(VfsFormat format)
    {
        var files = DeliveryFiles();
        var file = format == VfsFormat.Stored ? files.Stored : files.Original;
        byte[] expected = format == VfsFormat.Stored ? files.StoredBytes : files.OriginalBytes;
        var connections = Substitute.For<IVfsConnections>();
        var connectionId = Guid.NewGuid();
        string username = $"romd-{connectionId:N}";
        connections.AuthenticateAsync(username, "secret", Arg.Any<CancellationToken>())
            .Returns(new AuthenticatedVfsConnection(Guid.NewGuid(), connectionId, format));
        var cas = Substitute.For<IContentAddressableStore>();
        cas.OpenRandomAccessAsync(StorageKey.FromHash(files.Original.Sha256), Arg.Any<CancellationToken>())
            .Returns(_ => new BytesContent(files.OriginalBytes));
        cas.OpenRandomAccessAsync(StorageKey.FromHash(files.Stored.Sha256), Arg.Any<CancellationToken>())
            .Returns(_ => new BytesContent(files.StoredBytes));
        var catalog = Substitute.For<IRomContentCatalog>();
        var identity = MediaFactFixtures.Identity(files.OriginalBytes);
        var measurement = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), identity.Sha256, identity.Size, identity,
            RomContainerFacts.Identity.Instance, new RomDecoderIdentity.Managed("endpoint-fixture-hasher/v1"), "identity/v1",
            "endpoint-fixture-verifier/v1", DateTimeOffset.UnixEpoch, null, null);
        var original = MediaDeliveryFactFixtures.MeasuredWhole(11, 1, 21, 1, true,
            RomContainerFacts.Identity.Instance, measurement, identity.Md5, identity.Sha1);
        var container = MediaFactFixtures.Rvz(identity.Size, 1, 5, 19, 131072, false);
        var binding = (RomManagedReadBinding)files.Stored.Target!.ReadBinding!;
        var rvzProof = MediaFactFixtures.WholeMeasurement(binding.MeasurementId, files.Stored.Sha256, files.Stored.Length,
            identity, container, new RomDecoderIdentity.Managed(binding.DecoderBuild), "logical-sha256/v1",
            "vfs-fixture-verifier/v1", DateTimeOffset.UnixEpoch, null, null);
        var rvz = MediaDeliveryFactFixtures.MeasuredWhole(binding.RepresentationId, 2, binding.OutputId, 1, false,
            container, rvzProof, files.Stored.Md5, files.Stored.Sha1);
        catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(new RomContentDescriptor(1, 21, identity,
            "disc.iso", [original, rvz], [], []));
        var decoder = Substitute.For<IRomRepresentationDecoder>();
        decoder.CanDecode(Arg.Any<RomRepresentationEncoding>()).Returns(true);
        if (format == VfsFormat.Stored)
            decoder.DecodeAsync(Arg.Any<RomRepresentationEncoding>(), Arg.Any<IRandomAccessContent>(),
                Arg.Any<CancellationToken>(), Arg.Any<Sha256?>(), Arg.Any<TimeSpan?>())
                .Returns<Task<IRandomAccessContent>>(_ => throw new ResourceCapacityException("decoder full"));
        else
            decoder.DecodeAsync(RomRepresentationEncoding.Identity, Arg.Any<IRandomAccessContent>(),
                Arg.Any<CancellationToken>(), Arg.Any<Sha256?>(), Arg.Any<TimeSpan?>())
                .Returns(call => call.ArgAt<IRandomAccessContent>(1));
        var resolver = new ConsumerVfsContentResolver(cas, new RomContentReader(catalog, cas, decoder,
            new ManagedMediaTestAdmission("vfs-fixture-managed/v1")));
        await using var host = await Harness.Start(connections: connections, content: resolver);
        ConfigureFormatRepository(host, files.Original, files.Stored);
        UseBasic(host, username);
        using var full = await host.Client.GetAsync(FormatUrl(host, file));
        full.StatusCode.ShouldBe(HttpStatusCode.OK);
        full.Content.Headers.ContentLength.ShouldBe(file.Length);
        full.Headers.ETag!.Tag.ShouldBe($"\"{file.Sha256}\"");
        byte[] delivered = await full.Content.ReadAsByteArrayAsync();
        delivered.ShouldBe(expected);
        Sha256.FromSpan(System.Security.Cryptography.SHA256.HashData(delivered)).ShouldBe(file.Sha256);
        using var request = new HttpRequestMessage(HttpMethod.Get, FormatUrl(host, file));
        request.Headers.Range = new(3, 7);
        using var range = await host.Client.SendAsync(request);
        range.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        range.Content.Headers.ContentLength.ShouldBe(5);
        range.Content.Headers.ContentRange!.Length.ShouldBe(file.Length);
        range.Headers.ETag!.Tag.ShouldBe($"\"{file.Sha256}\"");
        (await range.Content.ReadAsByteArrayAsync()).ShouldBe(expected[3..8]);
        await cas.Received(2).OpenRandomAccessAsync(StorageKey.FromHash(file.Sha256), Arg.Any<CancellationToken>());
        if (format == VfsFormat.Stored)
        {
            await catalog.Received(2).FindByRomIdAsync(1, Arg.Any<CancellationToken>());
            decoder.ReceivedCalls().ShouldBeEmpty();
            await cas.DidNotReceive().OpenRandomAccessAsync(StorageKey.FromHash(files.Original.Sha256), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task Format_StoredRvzWithoutPhysicalChecksums_OmitsOwnCloudFactsWithoutOpeningContent()
    {
        var files = DeliveryFiles();
        var unknown = files.Stored with
        {
            Md5 = default,
            Sha1 = default,
            Target = files.Stored.Target! with { Md5 = null, Sha1 = null }
        };
        await using var host = await Harness.Start();
        ConfigureFormatRepository(host, files.Original, unknown);
        using var response = await host.Propfind(FormatUrl(host, unknown), "0");
        response.StatusCode.ShouldBe(HttpStatusCode.MultiStatus);
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        xml.Descendants(XName.Get("checksum", "http://owncloud.org/ns")).ShouldBeEmpty();
        xml.Descendants(Dav + "getcontentlength").Single().Value.ShouldBe(unknown.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        host.Resolver.Opened.ShouldBe(0);
    }

    private static void UseBasic(Harness host, string username)
    {
        host.Client.BaseAddress = new Uri("https://localhost");
        host.Client.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":secret")));
    }

    private static string FormatUrl(Harness host, ConsumerVfsFile file) => host.Folder +
        string.Join('/', file.RelativePath.Split('/').Select(Uri.EscapeDataString));

    private static void ConfigureFormatRepository(Harness host, ConsumerVfsFile original, ConsumerVfsFile stored)
    {
        var summary = new ConsumerVfsReleaseSummary(1, 1, "Game", "Platform", "gc");
        ConsumerVfsFile Selected(VfsFormat format) => format == VfsFormat.Stored ? stored : original;
        host.Repository.GetReleaseAsync(Arg.Any<ConsumerLibraryScope>(), 1, Arg.Any<CancellationToken>(), Arg.Any<VfsFormat>())
            .Returns(call => new ConsumerLibraryReadResult<ConsumerVfsRelease>.Found(1,
                new(summary, ConsumerVfsReleaseTree.Create([Selected(call.ArgAt<VfsFormat>(3))]).Value)));
        host.Repository.GetFileAsync(Arg.Any<ConsumerLibraryScope>(), 1, Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<VfsFormat>())
            .Returns(call =>
            {
                var selected = Selected(call.ArgAt<VfsFormat>(4));
                return call.ArgAt<string>(2) == selected.RelativePath
                    ? (ConsumerLibraryReadResult<ConsumerVfsResolvedFile>)new ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.Found(1,
                        new(summary, selected, [selected.RelativePath]))
                    : new ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound(1);
            });
        host.Repository.ClearReceivedCalls();
    }

    private static (ConsumerVfsFile Original, ConsumerVfsFile Stored, byte[] OriginalBytes, byte[] StoredBytes) DeliveryFiles()
    {
        byte[] original = Enumerable.Range(0, 128).Select(value => (byte)value).ToArray();
        byte[] stored = Enumerable.Range(0, 20).Select(value => (byte)(value + 200)).ToArray();
        var logical = Sha256.FromSpan(System.Security.Cryptography.SHA256.HashData(original));
        var artifact = Sha256.FromSpan(System.Security.Cryptography.SHA256.HashData(stored));
        var logicalMd5 = Md5.FromSpan(System.Security.Cryptography.MD5.HashData(original));
        var logicalSha1 = Sha1.FromSpan(System.Security.Cryptography.SHA1.HashData(original));
        var artifactMd5 = Md5.FromSpan(System.Security.Cryptography.MD5.HashData(stored));
        var artifactSha1 = Sha1.FromSpan(System.Security.Cryptography.SHA1.HashData(stored));
        var originalTarget = new RomDeliveryTarget(1, 1, RomDeliveryFormat.Original, "game.iso", logical, original.Length,
            logical, original.Length, logical, original.Length, RomRepresentationEncoding.Identity, logicalMd5, logicalSha1);
        var rvzTarget = new RomDeliveryTarget(1, 2, RomDeliveryFormat.Rvz, "game.rvz", artifact, stored.Length,
            logical, original.Length, artifact, stored.Length, RomRepresentationEncoding.Rvz, artifactMd5, artifactSha1,
            new RomManagedReadBinding(12, 22, Guid.Parse("4bfef8cc-6d78-4ff0-bf78-9444745cc396"), "vfs-fixture-managed/v1"));
        return (new("Disc/game.iso", logical, original.Length, logicalMd5, logicalSha1, Target: originalTarget),
            new("Disc/game.rvz", artifact, stored.Length, artifactMd5, artifactSha1, Target: rvzTarget), original, stored);
    }

    [Fact]
    public async Task Admission_FreshPrincipalChangesConnection_DeniesBeforeCatalogOrContent()
    {
        var connections = Substitute.For<IVfsConnections>();
        var userId = Guid.NewGuid();
        var initialId = Guid.NewGuid();
        var changedId = Guid.NewGuid();
        string username = $"romd-{initialId:N}";
        int checks = 0;
        connections.AuthenticateAsync(username, "secret", Arg.Any<CancellationToken>()).Returns(_ =>
            Task.FromResult<AuthenticatedVfsConnection?>(Interlocked.Increment(ref checks) == 1
                ? new(userId, initialId, VfsFormat.Stored) : new(userId, changedId, VfsFormat.Original)));
        await using var host = await Harness.Start(connections: connections);
        var file = new ConsumerVfsFile("100% # game.bin", Sha256.Parse(new string('a', 64)), 256);
        host.Repository.GetFileAsync(Arg.Any<ConsumerLibraryScope>(), 1, file.RelativePath, Arg.Any<CancellationToken>(), Arg.Any<VfsFormat>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.Found(1,
                new(new(1, 1, "Game", "Platform"), file, [file.RelativePath])));
        host.Repository.ClearReceivedCalls();
        host.Client.BaseAddress = new Uri("https://localhost");
        host.Client.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":secret")));
        using var response = await host.Client.GetAsync(host.FileUrl);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        checks.ShouldBe(2);
        host.Repository.ReceivedCalls().ShouldBeEmpty();
        host.Resolver.Opened.ShouldBe(0);
    }

    [Fact]
    public async Task Admission_CredentialInvalidatedWhileWaiting_DeniesBeforeCatalogAccess()
    {
        var connections = Substitute.For<IVfsConnections>();
        var userId = Guid.NewGuid();
        string username = $"romd-{Guid.NewGuid():N}";
        var authenticated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int checks = 0;
        connections.AuthenticateAsync(username, "secret", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (Interlocked.Increment(ref checks) == 1)
            {
                authenticated.TrySetResult();
                return Task.FromResult<AuthenticatedVfsConnection?>(new(userId, Guid.ParseExact(username[5..], "N")));
            }
            return Task.FromResult<AuthenticatedVfsConnection?>(null);
        });
        await using var host = await Harness.Start(connections: connections);
        var admission = host.App.Services.GetRequiredService<ConsumerVfsAdmission>();
        using var blocker = await admission.EnterAsync(Guid.NewGuid(), null, default);
        host.Client.BaseAddress = new Uri("https://localhost");
        host.Client.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":secret")));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = host.Client.GetAsync(host.FileUrl, deadline.Token);
        await authenticated.Task.WaitAsync(deadline.Token);
        while (admission.QueuedRequestCount == 0)
            await Task.Delay(1, deadline.Token);
        blocker!.Dispose();
        using var response = await request;
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ShouldNotBeEmpty();
        checks.ShouldBe(2);
        host.Repository.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Admission_LibraryAccessRemovedWhileQueued_DeniesBeforeOpeningContent()
    {
        await using var host = await Harness.Start(queueWaitMilliseconds: 5000);
        var admission = host.App.Services.GetRequiredService<ConsumerVfsAdmission>();
        using var blocker = await admission.EnterAsync(Guid.NewGuid(), null, default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = host.Client.GetAsync(host.FileUrl, deadline.Token);
        while (admission.QueuedRequestCount == 0)
            await Task.Delay(1, deadline.Token);
        host.Repository.ReceivedCalls().ShouldBeEmpty();
        host.Repository.GetReleaseAsync(Arg.Any<ConsumerLibraryScope>(), 1, Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsRelease>.ItemNotFound(1));
        blocker!.Dispose();
        using var response = await request;
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        host.Resolver.Opened.ShouldBe(0);
    }

    [Fact]
    public async Task Admission_AtCapacity_Returns503AndCanRecover()
    {
        await using var host = await Harness.Start();
        var admission = host.App.Services.GetRequiredService<ConsumerVfsAdmission>();
        admission.TryEnter(Guid.Empty).ShouldBeTrue();
        using var busy = await host.Client.GetAsync(host.FileUrl);
        busy.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        admission.Exit(Guid.Empty);
        using var recovered = await host.Client.GetAsync(host.FileUrl);
        recovered.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Propfind_AssignmentChangesBetweenPages_DoesNotReturnMixedLibraries()
    {
        await using var host = await Harness.Start();
        host.Repository.ListPlatformsAsync(Arg.Any<ConsumerLibraryScope>(), 0, 200, Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsPlatformPage>.Found(1, new([new(1, "Platform")], 1)));
        host.Repository.ListPlatformsAsync(Arg.Any<ConsumerLibraryScope>(), 1, 200, Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsPlatformPage>.Found(2, new([], null)));
        using var response = await host.Propfind("/vfs/library/", "1");
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(1));
        (await response.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ContentWritePolicy.Raw)]
    [InlineData(ContentWritePolicy.SeekableZstandard)]
    public async Task Get_RealCas_CrossFrameRangeMatchesOriginal(ContentWritePolicy policy)
    {
        string root = Path.Combine(Path.GetTempPath(), "romd-vfs-http-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddContentAddressableStorage(options =>
            {
                options.RootPath = root;
                options.WritePolicy = policy;
                options.SeekableFrameSize = 65536;
            });
            await using var provider = services.BuildServiceProvider();
            var cas = provider.GetRequiredService<IContentAddressableStore>();
            byte[] data = Enumerable.Range(0, 200000).Select(index => (byte)(index % 251)).ToArray();
            var stored = await cas.StoreAsync(new MemoryStream(data));
            stored.IsCompressed.ShouldBe(policy == ContentWritePolicy.SeekableZstandard);
            var file = new ConsumerVfsFile("100% # game.bin", Sha256.Parse(stored.Key.ToString()), data.Length);
            await using var host = await Harness.Start(cas: cas, file: file);
            using var request = new HttpRequestMessage(HttpMethod.Get, host.FileUrl);
            request.Headers.Range = new RangeHeaderValue(65530, 65549);
            using var response = await host.Client.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
            (await response.Content.ReadAsByteArrayAsync()).ShouldBe(data[65530..65550]);
            using var full = await host.Client.GetAsync(host.FileUrl);
            (await full.Content.ReadAsByteArrayAsync()).ShouldBe(data);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Get_CancelledTransfer_DisposesReaderAndReleasesAdmission(bool networkListener)
    {
        var resolver = new BlockingResolver();
        await using var host = await Harness.Start(content: resolver, networkListener: networkListener);
        using var cancellation = new CancellationTokenSource();
        Task<HttpResponseMessage> transfer = host.Client.GetAsync(host.FileUrl, cancellation.Token);
        await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await transfer);
        await resolver.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Disposal happens before the outer admission finally block; explicitly await the request ending.
        var admission = host.App.Services.GetRequiredService<ConsumerVfsAdmission>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!admission.TryEnter(Guid.Empty))
            await Task.Delay(10, timeout.Token);
        admission.Exit(Guid.Empty);
    }

    [Fact]
    public async Task Get_ClientDisconnectAfterResponseStarts_DisposesReaderAndAdmitsNextRequest()
    {
        var resolver = new BlockingResolver(sendPrefix: true);
        await using var host = await Harness.Start(content: resolver, networkListener: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await host.Client.GetAsync(host.FileUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        byte[] prefix = new byte[1];
        (await body.ReadAsync(prefix, timeout.Token)).ShouldBe(1);
        prefix[0].ShouldBe((byte)42);
        await resolver.Started.Task.WaitAsync(timeout.Token);

        // The streaming request must still own the only content slot.
        using var busy = await host.Client.GetAsync(host.FileUrl, timeout.Token);
        busy.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Dispose(); // Zero response-drain allowance closes the real TCP connection.
        await resolver.Disposed.Task.WaitAsync(timeout.Token);

        // Stream disposal precedes the endpoint's finally block. Synchronize on
        // admission release before checking recovery through another HTTP request.
        var admission = host.App.Services.GetRequiredService<ConsumerVfsAdmission>();
        while (!admission.TryEnter(Guid.Empty))
            await Task.Delay(10, timeout.Token);
        admission.Exit(Guid.Empty);
        using var recovered = await host.Propfind(host.Folder, "0");
        recovered.StatusCode.ShouldBe(HttpStatusCode.MultiStatus);
    }

    [Fact]
    public async Task Disabled_Returns404WithoutLookup()
    {
        await using var host = await Harness.Start(enabled: false);
        using var response = await host.Client.GetAsync(host.FileUrl);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        host.Repository.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Get_OneAccountAtItsLimit_LeavesCapacityForAnotherAndRecoversAfterCancellation()
    {
        var resolver = new ConcurrentResolver();
        await using var host = await Harness.Start(content: resolver, networkListener: true, capacity: 8, perUser: 4);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var other = new HttpClient { BaseAddress = host.Client.BaseAddress };
        other.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, Guid.NewGuid().ToString());
        var first = host.Client.GetAsync(host.FileUrl, cancelled.Token);
        var pending = Enumerable.Range(0, 3).Select(_ => host.Client.GetAsync(host.FileUrl, deadline.Token)).ToList();
        try
        {
            for (int i = 0; i < 4; i++)
                await resolver.Started.WaitAsync(deadline.Token);
            int lookups = host.Repository.ReceivedCalls().Count();
            using (var rejected = await host.Client.GetAsync(host.FileUrl, deadline.Token))
            {
                rejected.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
                rejected.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(1));
                (await rejected.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();
            }
            resolver.Opened.ShouldBe(4);
            host.Repository.ReceivedCalls().Count().ShouldBe(lookups);
            pending.Add(other.GetAsync(host.FileUrl, deadline.Token));
            await resolver.Started.WaitAsync(deadline.Token);
            resolver.Active.ShouldBe(5);
            cancelled.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(async () => await first);
            await resolver.Disposed.WaitAsync(deadline.Token);
            // Wait for the endpoint's finally, which follows stream disposal.
            Guid userId = Guid.Parse(host.Client.DefaultRequestHeaders.GetValues(TestAuthHandler.UserIdHeader).Single());
            var admission = host.App.Services.GetRequiredService<ConsumerVfsAdmission>();
            while (!admission.TryEnter(userId))
                await Task.Delay(10, deadline.Token);
            admission.Exit(userId);
            pending.Add(host.Client.GetAsync(host.FileUrl, deadline.Token));
            await resolver.Started.WaitAsync(deadline.Token);
            resolver.Active.ShouldBe(5);
            resolver.Release.TrySetResult();
            foreach (var task in pending)
            {
                using var response = await task;
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                (await response.Content.ReadAsByteArrayAsync()).ShouldBe(resolver.Bytes);
            }
        }
        finally
        {
            deadline.Cancel();
            resolver.Release.TrySetResult();
            foreach (var task in pending.Prepend(first))
            {
                try
                { (await task).Dispose(); }
                catch (OperationCanceledException) { }
                catch (HttpRequestException) { }
            }
        }
    }

    [Fact]
    public async Task Get_ConcurrentTcpTransfers_BoundsReadersAndRecoversEverySlotAfterCancellation()
    {
        const int capacity = 8;
        var resolver = new ConcurrentResolver();
        await using var host = await Harness.Start(content: resolver, networkListener: true, capacity: capacity);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var admission = host.App.Services.GetRequiredService<ConsumerVfsAdmission>();
        for (int round = 0; round < 3; round++)
        {
            resolver.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellations = Enumerable.Range(0, capacity)
                .Select(_ => CancellationTokenSource.CreateLinkedTokenSource(deadline.Token)).ToArray();
            var pending = cancellations.Select(cancel => host.Client.GetAsync(host.FileUrl, cancel.Token)).ToArray();
            try
            {
                for (int i = 0; i < capacity; i++)
                    await resolver.Started.WaitAsync(deadline.Token);
                Volatile.Read(ref resolver.Active).ShouldBe(capacity);
                int opened = Volatile.Read(ref resolver.Opened);
                int lookups = host.Repository.ReceivedCalls().Count();
                await Task.WhenAll(Enumerable.Range(0, 32).Select(async _ =>
                {
                    using var busy = await host.Client.GetAsync(host.FileUrl, deadline.Token);
                    busy.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
                    busy.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(1));
                    (await busy.Content.ReadAsByteArrayAsync(deadline.Token)).ShouldBeEmpty();
                }));
                // Overload must be rejected before catalog work or content allocation.
                Volatile.Read(ref resolver.Opened).ShouldBe(opened);
                host.Repository.ReceivedCalls().Count().ShouldBe(lookups);
                for (int i = 0; i < capacity / 2; i++)
                    cancellations[i].Cancel();
                for (int i = 0; i < capacity / 2; i++)
                {
                    int index = i;
                    await Should.ThrowAsync<OperationCanceledException>(async () => await pending[index]);
                    await resolver.Disposed.WaitAsync(deadline.Token);
                }
                await WaitForSlots(capacity / 2);
                // The other four original requests remain live while four replacements enter.
                var replacement = Enumerable.Range(0, capacity / 2)
                    .Select(_ => host.Client.GetAsync(host.FileUrl, deadline.Token)).ToArray();
                for (int i = 0; i < capacity / 2; i++)
                    await resolver.Started.WaitAsync(deadline.Token);
                Volatile.Read(ref resolver.Active).ShouldBe(capacity);
                resolver.Release.TrySetResult();
                foreach (var transfer in pending.Skip(capacity / 2).Concat(replacement))
                {
                    using var response = await transfer;
                    response.StatusCode.ShouldBe(HttpStatusCode.OK);
                    (await response.Content.ReadAsByteArrayAsync(deadline.Token)).ShouldBe(resolver.Bytes);
                }
                for (int i = 0; i < capacity; i++)
                    await resolver.Disposed.WaitAsync(deadline.Token);
                await WaitForSlots(capacity);
                Volatile.Read(ref resolver.Active).ShouldBe(0);
            }
            finally
            {
                foreach (var cancellation in cancellations)
                { cancellation.Cancel(); cancellation.Dispose(); }
                resolver.Release.TrySetResult();
            }
        }
        Volatile.Read(ref resolver.Peak).ShouldBe(capacity);
        Volatile.Read(ref resolver.Opened).ShouldBe(36);

        async Task WaitForSlots(int count)
        {
            int acquired = 0;
            try
            {
                while (acquired < count)
                {
                    if (admission.TryEnter(Guid.Empty))
                        acquired++;
                    else
                        await Task.Delay(10, deadline.Token);
                }
            }
            finally { for (int i = 0; i < acquired; i++) admission.Exit(Guid.Empty); }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(129)]
    public void Registration_InvalidPerAccountLimit_FailsStartup(int limit)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Romd:Vfs:MaximumConcurrentRequestsPerUser"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }).Build();
        Should.Throw<InvalidOperationException>(() => new ServiceCollection().AddRomdConsumerVfs(configuration));
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required IConsumerVfsRepository Repository { get; init; }
        public required Resolver Resolver { get; init; }
        public string PlatformFolder => "/vfs/library/" + Uri.EscapeDataString(IdCoder.Encode(1) + " Platform") + "/";
        public string Folder => PlatformFolder + Uri.EscapeDataString(IdCoder.Encode(1) + " Game") + "/";
        public string FileUrl => Folder + Uri.EscapeDataString("100% # game.bin");

        public static async Task<Harness> Start(bool enabled = true, IConsumerVfsContentResolver? content = null,
            ConsumerVfsFile? file = null, bool networkListener = false, int capacity = 1, int? perUser = null, IVfsLayout? layout = null, IVfsConnections? connections = null, int queueWaitMilliseconds = 100, IContentAddressableStore? cas = null,
            Action<IServiceCollection>? configureServices = null)
        {
            var builder = WebApplication.CreateBuilder();
            if (networkListener)
                builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            else
                builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Romd:Vfs:MaximumQueueWaitMilliseconds"] = connections is null ? queueWaitMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) : "5000",
                ["Romd:Vfs:MaximumConcurrentRequestsPerConnection"] = capacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Romd:Vfs:AllowDevelopmentBearer"] = "true",
                ["Romd:Vfs:Enabled"] = enabled.ToString(),
                ["Romd:Vfs:MaximumConcurrentRequests"] = capacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Romd:Vfs:MaximumConcurrentRequestsPerUser"] = (perUser ?? capacity).ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
            builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped<ICurrentUser, CurrentUser>();
            builder.Services.AddRomdConsumerVfs(builder.Configuration);
            builder.Services.AddAuthorization(options => options.AddPolicy(VfsBasicAuthenticationHandler.PolicyName,
                policy =>
                {
                    if (connections is not null)
                        policy.AddAuthenticationSchemes(VfsBasicAuthenticationHandler.SchemeName);
                    policy.RequireAuthenticatedUser();
                }));
            if (connections is not null)
                builder.Services.AddSingleton(connections);
            if (layout is not null)
                builder.Services.AddSingleton(layout);
            var repository = Substitute.For<IConsumerVfsRepository>();
            var summary = new ConsumerVfsReleaseSummary(1, 1, "Game", "Platform");
            file ??= new ConsumerVfsFile("100% # game.bin", Sha256.Parse(new string('a', 64)), 256);
            var tree = ConsumerVfsReleaseTree.Create([file]).Value;
            repository.ListPlatformsAsync(Arg.Any<ConsumerLibraryScope>(), 0, 200, Arg.Any<CancellationToken>())
                .Returns(new ConsumerLibraryReadResult<ConsumerVfsPlatformPage>.Found(1, new([new(1, "Platform")], null)));
            repository.GetPlatformAsync(Arg.Any<ConsumerLibraryScope>(), 1, Arg.Any<CancellationToken>())
                .Returns(new ConsumerLibraryReadResult<ConsumerVfsPlatform>.Found(1, new(1, "Platform")));
            repository.ListReleasesAsync(Arg.Any<ConsumerLibraryScope>(), 1, 0, 200, Arg.Any<CancellationToken>())
                .Returns(new ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found(1, new([summary], null,
                    new Dictionary<int, IReadOnlyList<string>> { [1] = [file.RelativePath] })));
            repository.GetReleaseAsync(Arg.Any<ConsumerLibraryScope>(), 1, Arg.Any<CancellationToken>())
                .Returns(new ConsumerLibraryReadResult<ConsumerVfsRelease>.Found(1, new(summary, tree)));
            repository.GetFileAsync(Arg.Any<ConsumerLibraryScope>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    var read = await repository.GetReleaseAsync(call.ArgAt<ConsumerLibraryScope>(0), call.ArgAt<int>(1), call.ArgAt<CancellationToken>(3));
                    if (read is ConsumerLibraryReadResult<ConsumerVfsRelease>.Found found &&
                        found.Value.Tree.Find(call.ArgAt<string>(2))?.File is { } selected)
                        return (ConsumerLibraryReadResult<ConsumerVfsResolvedFile>)new ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.Found(
                            found.LibraryId, new(found.Value.Summary, selected, [selected.RelativePath]));
                    return read is ConsumerLibraryReadResult<ConsumerVfsRelease>.ProjectionInconsistent
                        ? new ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ProjectionInconsistent()
                        : new ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound(1);
                });
            var resolver = new Resolver();
            builder.Services.AddSingleton(repository);
            builder.Services.AddSingleton<IConsumerVfsContentResolver>(content ?? (cas is null ? resolver : new ConsumerVfsContentResolver(cas)));
            configureServices?.Invoke(builder.Services);
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapConsumerVfsEndpoints();
            await app.StartAsync();
            var client = networkListener
                ? new HttpClient(new SocketsHttpHandler { MaxResponseDrainSize = 0 }) { BaseAddress = new Uri(app.Urls.Single()) }
                : app.GetTestClient();
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, Guid.NewGuid().ToString());
            return new() { App = app, Client = client, Repository = repository, Resolver = resolver };
        }

        public Task<HttpResponseMessage> Propfind(string path, string depth, string body = "")
        {
            var request = new HttpRequestMessage(new("PROPFIND"), path);
            if (depth.Length > 0)
                request.Headers.Add("Depth", depth);
            request.Content = new StringContent(body, Encoding.UTF8, "application/xml");
            return Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }

    private sealed class Resolver : IConsumerVfsContentResolver
    {
        public byte[] Bytes { get; } = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        public int Opened { get; private set; }
        public TrackingStream? Last { get; private set; }
        public Task<Stream?> OpenAsync(ConsumerVfsFile file, CancellationToken ct = default)
        {
            Opened++;
            Last = new(Bytes);
            return Task.FromResult<Stream?>(Last);
        }
    }

    private sealed class BytesContent(byte[] bytes) : IRandomAccessContent
    {
        public long Length => bytes.Length;
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;

        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            int start = (int)Math.Min(offset, bytes.Length);
            int count = Math.Min(bytes.Length - start, destination.Length);
            bytes.AsMemory(start, count).CopyTo(destination);
            return ValueTask.FromResult(count);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ConcurrentResolver : IConsumerVfsContentResolver
    {
        public byte[] Bytes { get; } = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        public SemaphoreSlim Started { get; } = new(0);
        public SemaphoreSlim Disposed { get; } = new(0);
        public TaskCompletionSource Release { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Active, Opened, Peak;

        public Task<Stream?> OpenAsync(ConsumerVfsFile file, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Opened);
            int active = Interlocked.Increment(ref Active);
            int peak;
            do
            { peak = Volatile.Read(ref Peak); }
            while (active > peak && Interlocked.CompareExchange(ref Peak, active, peak) != peak);
            return Task.FromResult<Stream?>(new GatedStream(this, Release.Task));
        }

        private sealed class GatedStream(ConcurrentResolver owner, Task release) : MemoryStream(owner.Bytes)
        {
            private int _started, _disposed;
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                if (Interlocked.Exchange(ref _started, 1) == 0)
                    owner.Started.Release();
                await release.WaitAsync(ct);
                return await base.ReadAsync(buffer, ct);
            }
            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Decrement(ref owner.Active);
                    owner.Disposed.Release();
                }
            }
        }
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class BlockingResolver(bool sendPrefix = false) : IConsumerVfsContentResolver
    {
        private bool SendPrefix => sendPrefix;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Stream?> OpenAsync(ConsumerVfsFile file, CancellationToken ct = default) =>
            Task.FromResult<Stream?>(new BlockingStream(this));

        private sealed class BlockingStream(BlockingResolver owner) : MemoryStream(new byte[256])
        {
            private bool _sentPrefix;
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                if (owner.SendPrefix && !_sentPrefix)
                {
                    _sentPrefix = true;
                    buffer.Span[0] = 42;
                    return 1;
                }
                owner.Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            }
            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                owner.Disposed.TrySetResult();
            }
        }
    }
}
