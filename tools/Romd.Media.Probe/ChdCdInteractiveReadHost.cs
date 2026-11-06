using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Romd.Application.Common.Content;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Vfs;
using Romd.Host.Endpoints;
using Romd.Hosting.Vfs;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Storage;
using Romd.Persistence.Identity;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;

namespace Romd.Media.Probe;

/// <summary>Loopback HTTPS with pinned ephemeral TLS, real VFS credentials and live PostgreSQL library lookup.</summary>
internal sealed class ChdCdInteractiveReadHost : IAsyncDisposable
{
    private readonly IHost _app;
    private readonly X509Certificate2 _certificate;
    private readonly ConvergedReadDecoder _runtime;
    private readonly List<HttpClient> _clients = [];
    private int _disposed;

    internal ChdCdInteractiveReadHost(IHost app, X509Certificate2 certificate, ConvergedReadDecoder runtime,
        InteractiveDecoderObservations observations, JsonElement httpLimits)
    {
        _app = app;
        _certificate = certificate;
        _runtime = runtime;
        Observations = observations;
        HttpLimits = httpLimits;
    }

    internal IReadOnlyList<HttpClient> Clients => _clients;
    internal ConvergedReadDecoder Runtime => _runtime;
    internal InteractiveDecoderObservations Observations { get; }
    internal JsonElement HttpLimits { get; }
    internal ConsumerVfsAdmission Admission => _app.Services.GetRequiredService<ConsumerVfsAdmission>();

    internal static JsonElement CaptureDefaultOptions(ConsumerVfsOptions options)
    {
        var captured = JsonSerializer.SerializeToElement(options);
        var expected = JsonSerializer.SerializeToElement(new ConsumerVfsOptions { Enabled = true });
        Check.That(captured.GetRawText() == expected.GetRawText(), "interactive probe requires unchanged HTTP admission defaults and Basic-only credentials");
        return captured;
    }

    internal static async Task<ChdCdInteractiveReadHost> StartAsync(PostgreSqlTestDatabase database, IContentAddressableStore cas,
        RomMediaComposition media, string nativeDirectory, IReadOnlyList<ChdCdReadCatalog.User> users, CancellationToken ct)
    {
        ConvergedReadDecoder? runtime = null;
        X509Certificate2? certificate = null;
        WebApplication? app = null;
        ChdCdInteractiveReadHost? host = null;
        try
        {
            var read = ChdCdVfsReadChecks.CreateReadComposition(nativeDirectory, media, ct);
            runtime = read.Decoder;
            certificate = CreateCertificate();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Romd:Vfs:Enabled"] = "true" });
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddScoped(_ => database.CreateContext());
            builder.Services.AddSingleton(database.CreateOptions());
            builder.Services.AddScoped<IRomContentCatalog, RomContentCatalog>();
            builder.Services.AddSingleton(cas);
            var observations = new InteractiveDecoderObservations(runtime.Decoder);
            builder.Services.AddSingleton<IRomRepresentationDecoder>(observations);
            builder.Services.AddSingleton(read.Admission);
            builder.Services.AddSingleton(media.DeliveryPolicy);
            builder.Services.AddScoped<IRomContentReader, RomContentReader>();
            builder.Services.AddScoped<IConsumerVfsContentResolver, ConsumerVfsContentResolver>();
            builder.Services.AddScoped<IConsumerVfsRepository, ConsumerVfsRepository>();
            builder.Services.AddScoped<IVfsConnections, VfsConnectionStore>();
            builder.Services.AddScoped<ICurrentUser, CurrentUser>();
            builder.Services.AddAuthentication(VfsBasicAuthenticationHandler.SchemeName);
            builder.Services.AddRomdConsumerVfs(builder.Configuration);
            builder.Services.AddAuthorization(options => options.AddPolicy(VfsBasicAuthenticationHandler.PolicyName,
                policy => policy.AddAuthenticationSchemes(VfsBasicAuthenticationHandler.SchemeName).RequireAuthenticatedUser()));
            app = builder.Build();
            var httpLimits = CaptureDefaultOptions(app.Services.GetRequiredService<ConsumerVfsOptions>());
            host = new(app, certificate, runtime, observations, httpLimits);
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapConsumerVfsEndpoints();
            await app.StartAsync(ct);
            byte[] pin = certificate.GetCertHash(HashAlgorithmName.SHA256);
            foreach (var user in users)
            {
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed is not null &&
                        CryptographicOperations.FixedTimeEquals(observed.GetCertHash(HashAlgorithmName.SHA256), pin)
                };
                var client = new HttpClient(handler) { BaseAddress = new Uri(app.Urls.Single()), Timeout = Timeout.InfiniteTimeSpan };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(user.Credential.Connection.Username + ":" + user.Credential.Password)));
                host._clients.Add(client);
            }
            return host;
        }
        catch
        {
            if (host is not null)
                await host.DisposeAsync();
            else
            {
                try
                { if (app is not null) await app.DisposeAsync(); }
                finally
                {
                    try
                    { if (runtime is not null) await runtime.DisposeAsync(); }
                    finally { certificate?.Dispose(); }
                }
            }
            throw;
        }
    }

    internal static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(4));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            foreach (var client in _clients)
                client.Dispose();
        }
        finally
        {
            try
            { await _app.StopAsync(CancellationToken.None); }
            finally
            {
                try
                {
                    if (_app is IAsyncDisposable asyncApp)
                        await asyncApp.DisposeAsync();
                    else
                        _app.Dispose();
                }
                finally
                {
                    try
                    { await _runtime.DisposeAsync(); }
                    finally { _certificate.Dispose(); }
                }
            }
        }
    }
}
