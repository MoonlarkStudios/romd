using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Xml.Linq;
using ErrorOr;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Libraries;
using Romd.Consumer.Application.Vfs;
using Romd.Domain.Hashing;
using Romd.Host.Endpoints;
using Romd.Hosting.Vfs;
using Romd.Infrastructure.Identity;
using Romd.Storage;

namespace Romd.Media.Probe;

internal static class WebChecks
{
    internal static async Task<object> Run(IContentAddressableStore cas, Artifact normalized, Artifact original, Digests iso, string isoPath)
    {
        var files = new Dictionary<string, ConsumerVfsFile>
        {
            ["game.iso"] = Describe("game.iso", iso),
            ["game.rvz"] = Describe("game.rvz", normalized.Hashes)
        };
        var normalizedFacts = await MeasuredProbeContent.VerifyRvzAsync(cas, normalized.Key, iso, default);
        var originalFacts = await MeasuredProbeContent.VerifyRvzAsync(cas, original.Key, iso, default);
        await using var resolver = new Resolver(cas, normalizedFacts);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Romd:Vfs:Enabled"] = "true",
            ["Romd:Vfs:AllowDevelopmentBearer"] = "true",
            ["Romd:Vfs:MaximumConcurrentRequestsPerUser"] = "8"
        });
        builder.Services.AddAuthentication("Probe").AddScheme<AuthenticationSchemeOptions, ProbeAuth>("Probe", _ => { });
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, CurrentUser>();
        builder.Services.AddRomdConsumerVfs(builder.Configuration);
        builder.Services.AddAuthorization(o => o.AddPolicy(VfsBasicAuthenticationHandler.PolicyName,
            p => p.AddAuthenticationSchemes("Probe").RequireAuthenticatedUser()));
        builder.Services.RemoveAll<IVfsLayout>();
        builder.Services.AddSingleton<IVfsLayout>(new Layout());
        var repository = Substitute.For<IConsumerVfsRepository>();
        bool authorized = true;
        repository.GetFileAsync(Arg.Any<ConsumerLibraryScope>(), 1, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => authorized && files.TryGetValue(call.ArgAt<string>(2), out var f)
                ? (ConsumerLibraryReadResult<ConsumerVfsResolvedFile>)new ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.Found(1,
                    new(new(1, 1, "Probe", "Probe"), f, files.Keys.ToArray()))
                : new ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound(1));
        builder.Services.AddSingleton(repository);
        builder.Services.AddSingleton<IConsumerVfsContentResolver>(resolver);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapConsumerVfsEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        int requests = 0, checks = 0;
        foreach (string name in files.Keys)
        {
            var f = files[name];
            string url = "/vfs/probe/" + name;
            string oraclePath = name.EndsWith(".iso", StringComparison.Ordinal) ? isoPath : normalized.Path;
            using var oracle = File.OpenHandle(oraclePath);
            int opened = resolver.Opened;
            using (var req = new HttpRequestMessage(new("PROPFIND"), url))
            {
                req.Headers.Add("Depth", "0");
                req.Content = new StringContent("<D:propfind xmlns:D='DAV:' xmlns:oc='http://owncloud.org/ns'><D:prop><D:getcontentlength/><D:getetag/><oc:checksums/></D:prop></D:propfind>", Encoding.UTF8, "application/xml");
                using var response = await client.SendAsync(req);
                requests++;
                Check.That((int)response.StatusCode == 207, "PROPFIND status");
                var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
                Check.That(xml.Descendants(XName.Get("getcontentlength", "DAV:")).Single().Value == f.Length.ToString(), "PROPFIND length");
                Check.That(xml.Descendants(XName.Get("getetag", "DAV:")).Single().Value == Tag(f), "PROPFIND ETag");
                Check.That(xml.Descendants(XName.Get("checksum", "http://owncloud.org/ns")).Single().Value == $"MD5:{f.Md5} SHA1:{f.Sha1}", "PROPFIND hashes");
                Check.That(resolver.Opened == opened, "listing must not open decoder");
                checks += 5;
            }
            using (var request = new HttpRequestMessage(HttpMethod.Head, url))
            using (var response = await client.SendAsync(request))
            {
                requests++;
                Check.That(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentLength == f.Length && response.Headers.ETag!.Tag == Tag(f), "HEAD metadata");
                await using var body = await response.Content.ReadAsStreamAsync();
                Check.That(await body.ReadAsync(new byte[1]) == 0, "HEAD empty body");
                checks += 2;
                Check.That(resolver.Opened == opened, "HEAD must not open decoder");
                checks++;
            }
            var ranges = new List<(long Offset, int Length)> { (0, 1024), (131071, 8192), (2097151, 8192), (f.Length - 1024, 4096) };
            var random = new Random(7654321);
            for (int i = 0; i < 50; i++)
                ranges.Add((random.NextInt64(f.Length - 4096), 4096));
            foreach (var (offset, length) in ranges)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Range", $"bytes={offset}-{offset + length - 1}");
                using var response = await client.SendAsync(request);
                requests++;
                int wanted = (int)Math.Min(length, f.Length - offset);
                Check.That(response.StatusCode == HttpStatusCode.PartialContent, "range status");
                Check.That(response.Content.Headers.ContentRange!.Length == f.Length && response.Content.Headers.ContentRange.From == offset && response.Content.Headers.ContentRange.To == offset + wanted - 1, "Content-Range");
                Check.That(response.Headers.ETag!.Tag == Tag(f), "range ETag");
                byte[] expected = new byte[wanted];
                await RandomAccess.ReadAsync(oracle, expected, offset);
                Check.That((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(expected), "HTTP byte comparison");
                checks += 4;
            }
            foreach (string range in new[] { "bytes=-1024", $"bytes={f.Length - 1024}-" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Range", range);
                using var response = await client.SendAsync(request);
                requests++;
                byte[] expected = new byte[1024];
                await RandomAccess.ReadAsync(oracle, expected, f.Length - 1024);
                Check.That(response.StatusCode == HttpStatusCode.PartialContent && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(expected), "suffix/open range");
                checks++;
            }
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                opened = resolver.Opened;
                request.Headers.TryAddWithoutValidation("Range", $"bytes={f.Length}-");
                using var response = await client.SendAsync(request);
                requests++;
                Check.That(response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && response.Content.Headers.ContentRange!.Length == f.Length, "416 at EOF");
                checks++;
                Check.That(resolver.Opened == opened, "416 must not open decoder");
                checks++;
            }
            foreach (var (header, value, status) in new[] { ("If-None-Match", Tag(f), 304), ("If-Match", "\"stale\"", 412) })
            {
                opened = resolver.Opened;
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation(header, value);
                using var response = await client.SendAsync(request);
                requests++;
                Check.That((int)response.StatusCode == status && (await response.Content.ReadAsByteArrayAsync()).Length == 0, header);
                checks++;
                Check.That(resolver.Opened == opened, header + " must not open decoder");
                checks++;
            }
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("Range", "bytes=0-1023");
                request.Headers.TryAddWithoutValidation("If-Range", Tag(f));
                using var response = await client.SendAsync(request);
                requests++;
                Check.That(response.StatusCode == HttpStatusCode.PartialContent, "matching If-Range");
                checks++;
            }
        }
        // Concurrent requests use separate native readers, selected through the production service.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            long offset = (long)i * (iso.Size / 9);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/vfs/probe/game.iso");
            request.Headers.TryAddWithoutValidation("Range", $"bytes={offset}-{offset + 4095}");
            using var response = await client.SendAsync(request);
            using var oracle = File.OpenHandle(isoPath);
            byte[] expected = new byte[4096];
            await RandomAccess.ReadAsync(oracle, expected, offset);
            Check.That(response.StatusCode == HttpStatusCode.PartialContent && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(expected), "concurrent HTTP");
        }));
        requests += 8;
        checks += 8;
        // Changing physical RVZ must preserve the ISO validator, while RVZ bytes get a new validator.
        var oldRvz = files["game.rvz"];
        resolver.Content = originalFacts;
        files["game.rvz"] = Describe("game.rvz", original.Hashes);
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/vfs/probe/game.iso"))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", Tag(files["game.iso"]));
            using var response = await client.SendAsync(request);
            requests++;
            Check.That(response.StatusCode == HttpStatusCode.NotModified, "ISO ETag survives re-encoding");
            checks++;
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/vfs/probe/game.rvz"))
        {
            request.Headers.TryAddWithoutValidation("Range", "bytes=0-1023");
            request.Headers.TryAddWithoutValidation("If-Range", Tag(oldRvz));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            requests++;
            Check.That(response.StatusCode == HttpStatusCode.OK && response.Headers.ETag!.Tag == Tag(files["game.rvz"]), "stale If-Range must restart transfer");
            var actual = await Digests.StreamAsync(await response.Content.ReadAsStreamAsync());
            Check.That(actual == original.Hashes, "stale cache receives exact full replacement");
            checks += 2;
        }
        int beforeDenied = resolver.Opened;
        authorized = false;
        using (var response = await client.GetAsync("/vfs/probe/game.iso"))
        {
            requests++;
            Check.That(response.StatusCode == HttpStatusCode.NotFound && resolver.Opened == beforeDenied, "denied catalog entry never opens CAS");
            checks++;
        }
        return new
        {
            Requests = requests,
            Assertions = checks,
            SameIsoEtagAfterReencoding = true,
            RvzReplacementInvalidatesRangeCache = true,
            ListingOpensDecoder = false,
            HeadOpensDecoder = false,
            BodylessConditionalOpensDecoder = false,
            UnsatisfiableRangeOpensDecoder = false,
            Note = "Authentication and catalog are test doubles. Metadata-only responses are asserted without content opening."
        };
    }
    private static ConsumerVfsFile Describe(string name, Digests hashes) => new(name, Sha256.Parse(hashes.Sha256), hashes.Size,
        Md5.Parse(hashes.Md5), Sha1.Parse(hashes.Sha1));
    private static string Tag(ConsumerVfsFile file) => "\"" + file.Sha256 + "\"";
    private sealed class Resolver(IContentAddressableStore cas, MeasuredProbeContent content) : IConsumerVfsContentResolver, IAsyncDisposable
    {
        private readonly RomRepresentationDecoder _decoder = new(new RomMediaAdapterRegistry(), new ReadDecoderOptions { MaxConcurrent = 8 });
        public MeasuredProbeContent Content = content;
        public ValueTask DisposeAsync() => _decoder.DisposeAsync();
        public int Opened;
        public async Task<Stream?> OpenAsync(ConsumerVfsFile file, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Opened);
            var catalog = Substitute.For<IRomContentCatalog>();
            catalog.FindAsync(file.Sha256, Arg.Any<CancellationToken>()).Returns(
                file.RelativePath.EndsWith(".iso", StringComparison.Ordinal) ? Content.Logical : Content.Opaque);
            var reader = new RomContentReader(catalog, cas, _decoder, ProbeMediaFacts.ManagedAdmission);
            return await new ConsumerVfsContentResolver(cas, reader).OpenAsync(file, ct);
        }
    }
    private sealed class Layout : IVfsLayout
    {
        public string Key => "probe";
        public ConsumerVfsFileLocator? LocateFile(string path) => new(1, path);
        public string? FilePath(ConsumerVfsResolvedFile file) => file.File.RelativePath;
        public Task<ErrorOr<VfsLayoutRead>> ReadAsync(ConsumerLibraryScope scope, string path, bool children, int maximum,
            CancellationToken ct = default, Romd.Domain.Vfs.VfsFormat format = Romd.Domain.Vfs.VfsFormat.Stored) =>
            Task.FromResult<ErrorOr<VfsLayoutRead>>(ConsumerVfsErrors.NotFound());
    }
    private sealed class ProbeAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,
                "c7155b2c-98ef-4f54-96bf-17ffb396ef1c")], "Probe")), "Probe")));
    }
}
