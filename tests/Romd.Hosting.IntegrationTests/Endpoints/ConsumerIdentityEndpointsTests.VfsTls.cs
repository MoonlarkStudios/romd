using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Server.AspNetCore;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Hosting.IntegrationTests.Vfs;
using Romd.Hosting.Vfs;
using Romd.Mount;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class ConsumerIdentityEndpointsTests
{
    [Fact]
    public Task Vfs_Https_ValidatesCertificatesAndServesRevocableAuthenticatedRanges() => RunVfsHttpsAsync(false, false);

    [RcloneFact]
    public Task Vfs_Https_RcloneValidatesCertificatesAndReadsAuthenticatedContent() => RunVfsHttpsAsync(true, false);

    [RcloneFact]
    public Task Vfs_Https_RcloneRegisteredConnections_UseExportAndEnforceLifecycle() => RunVfsHttpsAsync(true, false, registeredConnections: true);

    [RegisteredFskitFact]
    public Task Vfs_Https_RegisteredFskit_MountedReadsAndWritesUseExport() => RunVfsHttpsAsync(true, false, registeredConnections: true, registeredMount: true);

    [RegisteredFskitFact]
    public Task Vfs_Https_RegisteredFskit_RecoversAndEvictsUnderPressure() => RunVfsHttpsAsync(true, false, registeredConnections: true, registeredMount: true, registeredResilience: true);

    [RegisteredFskitFact]
    public Task Vfs_Https_RegisteredFskit_RefreshesSameSizeReplacement() => RunVfsHttpsAsync(true, false, registeredConnections: true, registeredMount: true, registeredFreshness: true);

    [RegisteredFskitFact]
    public Task Vfs_Https_RegisteredFskit_DeliversStoredRvzAndOriginal() =>
        RunVfsHttpsAsync(true, false, registeredConnections: true, registeredMount: true, mediaFormats: true);

    [RcloneMountFact]
    public Task Vfs_Https_MountedReadsMatchAuthorizedCas() => RunVfsHttpsAsync(true, true);

    [NginxFact]
    public Task Vfs_NginxHttps_DeploymentTemplateServesRevocableAuthenticatedRanges() => RunVfsHttpsAsync(false, false, true);

    [NginxFact(true)]
    public Task Vfs_NginxHttps_RcloneReadsThroughDeploymentTemplate() => RunVfsHttpsAsync(true, false, true);

    private static async Task RunVfsHttpsAsync(bool rclone, bool mount, bool nginx = false, bool registeredConnections = false, bool registeredMount = false, bool registeredResilience = false, bool registeredFreshness = false, bool mediaFormats = false, CancellationToken ct = default)
    {
        int requestDelayMilliseconds = int.Parse(Environment.GetEnvironmentVariable("ROMD_VFS_REQUEST_DELAY_MS") ?? "0",
            System.Globalization.CultureInfo.InvariantCulture);
        if (requestDelayMilliseconds is < 0 or > 500)
            throw new ArgumentOutOfRangeException(nameof(requestDelayMilliseconds));
        string? environmentReport = Environment.GetEnvironmentVariable("ROMD_VFS_CONNECTION_REPORT");
        if (!string.IsNullOrWhiteSpace(environmentReport))
            await File.WriteAllTextAsync(environmentReport + ".environment.json",
                JsonSerializer.Serialize(new { requestDelayMilliseconds, mediaFormats, registeredMount }), ct);
        // Trust exists only in these handlers. No OS certificate installation or
        // permissive validation callback is used, including for hostname checks.
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=ROMD test root", rootKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest("CN=localhost", serverKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        if (nginx)
            names.AddDnsName("unrecognized.invalid"); // Test HTTP host rejection after valid TLS.
        serverRequest.CertificateExtensions.Add(names.Build());
        using var publicCertificate = serverRequest.Create(root, DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        using var certificate = publicCertificate.CopyWithPrivateKey(serverKey);

        var proxyOrigin = nginx ? NginxTlsProxy.AllocateOrigin() : null;
        // The shipped templates advertise the validated hostname on HTTPS443.
        // Keep that public authority while mapping transport to an owned high port.
        var publicOrigin = nginx ? new Uri("https://localhost/") : null;
        SocketsHttpHandler NetworkHandler(bool trustTestRoot = true)
        {
            var policy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
                DisableCertificateDownloads = true
            };
            policy.CustomTrustStore.Add(root);
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                Credentials = null,
                ConnectTimeout = TimeSpan.FromSeconds(10)
            };
            if (trustTestRoot)
                handler.SslOptions.CertificateChainPolicy = policy;
            if (nginx)
                handler.ConnectCallback = async (_, token) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(IPAddress.Loopback, proxyOrigin!.Port, token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                };
            return handler;
        }

        var faults = registeredResilience ? new VfsConnectionFaultProbe() : null;
        await using var fixture = await ConsumerHostFixture.CreateAsync(services =>
        {
            services.AddSingleton(new ConsumerVfsOptions { AllowDevelopmentBearer = true, Enabled = true });
            if (faults is not null)
                services.AddSingleton<IStartupFilter>(faults);
            services.Configure<ContentStoreOptions>(options => options.WritePolicy = ContentWritePolicy.Raw);
            // Exercise the production OAuth HTTPS requirement even though the fixture
            // uses Testing for its disposable database and startup provisioning.
            services.PostConfigure<OpenIddictServerAspNetCoreOptions>(options => options.DisableTransportSecurityRequirement = false);
            if (requestDelayMilliseconds > 0)
                services.AddSingleton<IStartupFilter>(new VfsDelayStartupFilter(requestDelayMilliseconds));
        }, networkListener: true, serverCertificate: nginx ? null : certificate, configureHost: builder =>
        {
            if (!nginx)
                return;
            builder.UseSetting("Romd:ForwardedHeaders:Enabled", "true");
            builder.UseSetting("Romd:ForwardedHeaders:KnownProxies:0", "127.0.0.1");
            builder.UseSetting("Romd:ForwardedHeaders:ForwardLimit", "1");
            builder.UseSetting("Romd:ConsumerHost:PublicUrl", publicOrigin!.GetLeftPart(UriPartial.Authority));
        });
        using var addressClient = fixture.Factory.CreateClient();
        await using var proxy = nginx ? await NginxTlsProxy.StartAsync(addressClient.BaseAddress!, certificate, proxyOrigin!) : null;
        var origin = publicOrigin ?? new UriBuilder(addressClient.BaseAddress!) { Host = "localhost", Scheme = "https" }.Uri;

        if (nginx)
        {
            using var direct = await addressClient.GetAsync("/.well-known/openid-configuration");
            direct.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            using var unknownHost = new HttpRequestMessage(HttpMethod.Get, "/.well-known/openid-configuration");
            unknownHost.Headers.Host = "unrecognized.invalid";
            using var proxyClient = new HttpClient(NetworkHandler()) { BaseAddress = origin };
            using var unknown = await proxyClient.SendAsync(unknownHost);
            ((int)unknown.StatusCode).ShouldBe(421);
            using var forged = new HttpRequestMessage(HttpMethod.Get, "/.well-known/openid-configuration");
            forged.Headers.Add("X-Forwarded-Host", "attacker.invalid");
            forged.Headers.Add("X-Forwarded-Proto", "http");
            forged.Headers.Add("X-Forwarded-For", "203.0.113.7");
            using var discovery = await proxyClient.SendAsync(forged);
            discovery.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync());
            document.RootElement.GetProperty("issuer").GetString().ShouldBe(origin.AbsoluteUri);
            document.RootElement.GetProperty("token_endpoint").GetString().ShouldBe(new Uri(origin, "/connect/token").AbsoluteUri);
        }

        using (var untrusted = new MountIdentityClient(origin, NetworkHandler(trustTestRoot: false)))
        {
            var failure = await Should.ThrowAsync<MountIdentityException>(() => untrusted.DiscoverInstanceAsync());
            failure.Failure.ShouldBe(IdentityFailure.Network);
        }
        using (var wrongHost = new MountIdentityClient(new UriBuilder(origin) { Host = "127.0.0.1" }.Uri, NetworkHandler()))
        {
            var failure = await Should.ThrowAsync<MountIdentityException>(() => wrongHost.DiscoverInstanceAsync());
            failure.Failure.ShouldBe(IdentityFailure.Network);
        }

        int library = await fixture.CreateLibraryAsync("TLS library");
        await fixture.CreateConsumerUserAsync("tls-player", "tls-player@localhost", "Password123", user => user.LibraryId = library);
        await using var source = await VfsProbeContent.CreateAsync(ct);
        string? mediaSystem = mediaFormats ? Environment.GetEnvironmentVariable("ROMD_VFS_MEDIA_SYSTEM") : null;
        if (mediaFormats && (mediaSystem is not ("gc" or "wii") ||
                            !File.Exists(Environment.GetEnvironmentVariable("ROMD_VFS_PROBE_FILE"))))
            throw new ArgumentException("Media format probe requires a real ISO in ROMD_VFS_PROBE_FILE and gc/wii in ROMD_VFS_MEDIA_SYSTEM.");
        string relativePath = await fixture.SeedVfsProbeAsync(library, source, mediaSystem, ct);
        string? freshPath = registeredResilience ? (await fixture.AddCacheProbeAliasesAsync(library, relativePath, 2))[1] : null;
        string path = "/vfs/library/" + string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));
        using var client = new HttpClient(NetworkHandler()) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30) };
        using var identity = new MountIdentityClient(origin, NetworkHandler());
        var bound = await identity.BindAsync();
        var login = await bound.BeginLoginAsync();
        login.VerificationUri.Scheme.ShouldBe("https");
        login.VerificationUri.GetLeftPart(UriPartial.Authority).ShouldBe(origin.GetLeftPart(UriPartial.Authority));
        var approvalHandler = NetworkHandler();
        approvalHandler.UseCookies = true;
        using var approvalBrowser = new HttpClient(approvalHandler) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30) };
        using var approval = await approvalBrowser.PostAsync("/connect/verify", await BrowserAuthenticationForm.CreateAsync(approvalBrowser, "/connect/verify", new Dictionary<string, string>
        {
            ["user_code"] = login.UserCode,
            ["login"] = "tls-player",
            ["password"] = "Password123"
        }));
        // Browser approval redirects home; this client deliberately does not follow it.
        approval.StatusCode.ShouldBe(HttpStatusCode.Found);
        approval.Headers.Location!.OriginalString.ShouldBe("/");
        var issued = await bound.PollLoginAsync(login.DeviceCode);
        var active = await identity.RefreshAsync(issued.RefreshToken);
        active.RefreshToken.ShouldNotBe(issued.RefreshToken);

        var vfsOptions = fixture.Factory.Services.GetRequiredService<ConsumerVfsOptions>();
        vfsOptions.AllowDevelopmentBearer = false;
        using (var productionBearer = new HttpRequestMessage(HttpMethod.Get, path))
        {
            productionBearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", active.AccessToken);
            using var denied = await client.SendAsync(productionBearer);
            denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            denied.Headers.WwwAuthenticate.Single().Scheme.ShouldBe("Basic");
        }
        vfsOptions.AllowDevelopmentBearer = true;
        using (var listing = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/vfs/library/"))
        {
            listing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", active.AccessToken);
            listing.Headers.Add("Depth", "1");
            using var response = await client.SendAsync(listing);
            response.StatusCode.ShouldBe(HttpStatusCode.MultiStatus);
        }
        async Task<HttpResponseMessage> ReadRange(string? access)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (access is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            request.Headers.Range = new RangeHeaderValue(65530, 69625);
            return await client.SendAsync(request);
        }
        using (var anonymous = await ReadRange(null))
            anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using (var read = await ReadRange(active.AccessToken))
        {
            read.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
            read.Content.Headers.ContentRange!.From.ShouldBe(65530);
            read.Content.Headers.ContentRange.To.ShouldBe(69625);
            (await read.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual(source.ReadRange(65530, 4096)).ShouldBeTrue();
            read.Content.Headers.ContentLength.ShouldBe(4096);
            var etag = read.Headers.ETag.ShouldNotBeNull();
            etag.IsWeak.ShouldBeFalse();
            using var head = new HttpRequestMessage(HttpMethod.Head, path);
            head.Headers.Authorization = new AuthenticationHeaderValue("Bearer", active.AccessToken);
            using var metadata = await client.SendAsync(head);
            metadata.StatusCode.ShouldBe(HttpStatusCode.OK);
            metadata.Content.Headers.ContentLength.ShouldBe(source.Length);
            metadata.Headers.ETag.ShouldBe(etag);
            await using (var body = await metadata.Content.ReadAsStreamAsync(ct))
                (await body.ReadAsync(new byte[1], ct)).ShouldBe(0);
            using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
            conditional.Headers.Authorization = new AuthenticationHeaderValue("Bearer", active.AccessToken);
            conditional.Headers.IfNoneMatch.Add(etag);
            using var unchanged = await client.SendAsync(conditional);
            unchanged.StatusCode.ShouldBe(HttpStatusCode.NotModified);
            (await unchanged.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();
        }
        string? certificateDirectory = null;
        try
        {
            // rclone reads VFS directly, so it can use the listener's transport URL.
            string url = new Uri(proxy?.Origin ?? origin, "/vfs/library/").AbsoluteUri;
            string? caFile = null;
            if (rclone)
            {
                certificateDirectory = Path.Combine(Path.GetTempPath(), "romd-vfs-ca-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(certificateDirectory);
                caFile = Path.Combine(certificateDirectory, "root.pem");
                // Only the public root is written; all private keys remain in memory.
                await File.WriteAllTextAsync(caFile, root.ExportCertificatePem());
                if (registeredConnections)
                {
                    fixture.Factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Romd:ConsumerHost:PublicUrl"] = origin.GetLeftPart(UriPartial.Authority);
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", active.AccessToken);
                    vfsOptions.AllowDevelopmentBearer = false;
                    if (mediaFormats)
                        await ProbeRegisteredMediaFormatsAsync(client, fixture, source, relativePath, caFile, ct);
                    else
                        await ProbeRegisteredConnectionsAsync(client, fixture, source, relativePath, caFile, registeredMount, faults, freshPath, registeredFreshness ? () => fixture.ReplaceProbeContentAsync(library, source) : null);
                    return;
                }
                var untrusted = await RunRcloneAsync(url, active.AccessToken, "cat", "romd:" + relativePath,
                    "--offset", "65530", "--count", "4096");
                untrusted.ExitCode.ShouldNotBe(0);
                untrusted.Bytes.ShouldBeEmpty();
                untrusted.Error.ShouldContain("certificate");
                var wrongHost = await RunRcloneAsync(new UriBuilder(url) { Host = "127.0.0.1" }.Uri.AbsoluteUri,
                    active.AccessToken, "cat", "romd:" + relativePath, "--offset", "65530", "--count", "4096", "--ca-cert", caFile);
                wrongHost.ExitCode.ShouldNotBe(0);
                wrongHost.Bytes.ShouldBeEmpty();
                wrongHost.Error.ShouldContain("certificate");
                var listing = await RunRcloneAsync(url, active.AccessToken, "lsjson", "romd:", "--recursive", "--files-only", "--ca-cert", caFile);
                listing.ExitCode.ShouldBe(0, listing.Error);
                using var entries = JsonDocument.Parse(listing.Bytes);
                entries.RootElement.EnumerateArray().Select(entry => entry.GetProperty("Path").GetString()).ShouldContain(relativePath);
                var read = await RunRcloneAsync(url, active.AccessToken, "cat", "romd:" + relativePath,
                    "--offset", "65530", "--count", "4096", "--ca-cert", caFile);
                read.ExitCode.ShouldBe(0, read.Error);
                read.Bytes.AsSpan().SequenceEqual(source.ReadRange(65530, 4096)).ShouldBeTrue();
                if (mount)
                {
                    object direct = await ProbeHttpReadsAsync(client, active.AccessToken, relativePath, source);
                    await ProbeMountedReadsAsync(url, active.AccessToken, relativePath, source, direct, requestDelayMilliseconds, caFile);
                }
            }
            await identity.RevokeAsync(active.RefreshToken);
            using var denied = await ReadRange(active.AccessToken);
            denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await denied.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();
            if (rclone)
            {
                var deniedClient = await RunRcloneAsync(url, active.AccessToken, "cat", "romd:" + relativePath,
                    "--offset", "65530", "--count", "4096", "--ca-cert", caFile!);
                deniedClient.ExitCode.ShouldNotBe(0);
                deniedClient.Bytes.ShouldBeEmpty();
                deniedClient.Error.ShouldNotContain(active.AccessToken);
                deniedClient.Error.ShouldNotContain(active.RefreshToken);
            }
        }
        finally
        {
            if (certificateDirectory is not null)
            {
                File.Delete(Path.Combine(certificateDirectory, "root.pem"));
                Directory.Delete(certificateDirectory);
            }
        }
    }
}
