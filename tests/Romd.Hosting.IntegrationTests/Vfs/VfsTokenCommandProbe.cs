using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Romd.Hosting.IntegrationTests.Vfs;

/// <summary>Test-only token command bridge. Secrets remain in memory and child environment.</summary>
internal sealed class VfsTokenCommandProbe : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _directory;
    private readonly string _nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private int _calls;

    private VfsTokenCommandProbe(WebApplication app, string directory)
    {
        _app = app;
        _directory = directory;
    }

    public int Calls => Volatile.Read(ref _calls);

    public static async Task<VfsTokenCommandProbe> CreateAsync(Func<Task<string>> nextToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        string directory = Path.Combine(Path.GetTempPath(), "romd-vfs-token-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var probe = new VfsTokenCommandProbe(app, directory);
        app.MapGet("/token", async (HttpContext context) =>
        {
            if (context.Request.Headers.Authorization != "Bearer " + probe._nonce)
                return Results.Unauthorized();
            Interlocked.Increment(ref probe._calls);
            try
            { return Results.Text(await nextToken()); }
            catch { return Results.StatusCode(503); }
        });
        await File.WriteAllTextAsync(Path.Combine(directory, "token.py"), """
            import os, sys, urllib.request
            try:
                request = urllib.request.Request(os.environ['ROMD_PROBE_TOKEN_URL'],
                    headers={'Authorization': 'Bearer ' + os.environ['ROMD_PROBE_TOKEN_NONCE']})
                with urllib.request.urlopen(request, timeout=15) as response:
                    token = response.read(65536).decode('utf-8')
                if not token or any(c.isspace() for c in token):
                    raise ValueError()
                print(token)
            except Exception:
                print('Token command failed.', file=sys.stderr)
                sys.exit(1)
            """);
        await app.StartAsync();
        return probe;
    }

    public void Configure(ProcessStartInfo process)
    {
        string python = Environment.GetEnvironmentVariable("ROMD_VFS_PYTHON") ?? "/usr/bin/python3";
        string script = Path.Combine(_directory, "token.py");
        process.Environment.Remove("RCLONE_CONFIG_ROMD_BEARER_TOKEN");
        process.Environment["RCLONE_CONFIG_ROMD_BEARER_TOKEN_COMMAND"] = $"\"{python}\" \"{script}\"";
        process.Environment["ROMD_PROBE_TOKEN_URL"] = _app.Urls.Single() + "/token";
        process.Environment["ROMD_PROBE_TOKEN_NONCE"] = _nonce;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Directory.Delete(_directory, true);
    }
}
