using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Romd.Hosting.IntegrationTests.Hosting;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Vfs;

/// <summary>Runs a shipped ingress template against a disposable upstream host.</summary>
internal sealed class NginxTlsProxy : IAsyncDisposable
{
    private readonly string _directory;
    private Process? _process;
    private Task<string>? _error;
    private int _temporaryResponseFileCount;

    private NginxTlsProxy(string directory, Uri origin)
    {
        _directory = directory;
        Origin = origin;
    }

    public Uri Origin { get; }

    // nginx can unlink an open temporary file immediately. Its warn-level event
    // records disk spooling even when a directory listing cannot see the file.
    public int TemporaryResponseFileCount => Volatile.Read(ref _temporaryResponseFileCount);

    public static Uri AllocateOrigin(bool tls = true)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new Uri($"{(tls ? "https" : "http")}://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}/");
    }

    public static async Task<NginxTlsProxy> StartAsync(Uri upstream, X509Certificate2 certificate, Uri origin, bool tls = true)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        string directory = Path.Combine(Path.GetTempPath(), "romd-nginx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        int port = origin.Port;
        var proxy = new NginxTlsProxy(directory, origin);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "fullchain.pem"), certificate.ExportCertificatePem());
            using var key = certificate.GetRSAPrivateKey()!;
            await File.WriteAllTextAsync(Path.Combine(directory, "privkey.pem"), key.ExportPkcs8PrivateKeyPem());
            File.SetUnixFileMode(Path.Combine(directory, "privkey.pem"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            string template = await File.ReadAllTextAsync(Path.Combine(HangfirePostgreSqlTestSupport.RepositoryRoot,
                $"deploy/examples/{(tls ? "https" : "cloudflare")}/templates/default.conf.template"));
            // Only topology/certificate substitutions: retain shipped forwarding,
            // routing, buffering, URI and timeout directives unchanged.
            string configuration = template
                .Replace("${ROMD_ADMIN_HOST}", "admin.invalid", StringComparison.Ordinal)
                .Replace("${ROMD_CONSUMER_HOST}", "localhost", StringComparison.Ordinal)
                .Replace("${ROMD_PLAYER_HOST}", "player.invalid", StringComparison.Ordinal)
                .Replace("http://romd-consumer:8080", upstream.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal)
                .Replace("listen 443 ssl default_server;", $"listen 127.0.0.1:{port} ssl default_server;", StringComparison.Ordinal)
                .Replace("listen 80 default_server;", $"listen 127.0.0.1:{port} default_server;", StringComparison.Ordinal)
                .Replace("/etc/romd/tls/fullchain.pem", Quote(Path.Combine(directory, "fullchain.pem")), StringComparison.Ordinal)
                .Replace("/etc/romd/tls/privkey.pem", Quote(Path.Combine(directory, "privkey.pem")), StringComparison.Ordinal);
            configuration = "daemon off;\nmaster_process off;\npid " + Quote(Path.Combine(directory, "nginx.pid")) + ";\n"
                + "error_log stderr warn;\nevents { worker_connections 64; }\nhttp {\naccess_log off;\n"
                + "client_body_temp_path " + Quote(Path.Combine(directory, "client-body")) + ";\n"
                + "proxy_temp_path " + Quote(Path.Combine(directory, "proxy-body")) + ";\n"
                + "fastcgi_temp_path " + Quote(Path.Combine(directory, "fastcgi-body")) + ";\n"
                + "uwsgi_temp_path " + Quote(Path.Combine(directory, "uwsgi-body")) + ";\n"
                + "scgi_temp_path " + Quote(Path.Combine(directory, "scgi-body")) + ";\n"
                + configuration + "\n}\n";
            string path = Path.Combine(directory, "nginx.conf");
            await File.WriteAllTextAsync(path, configuration);
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ROMD_VFS_NGINX")!)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                WorkingDirectory = directory
            };
            start.ArgumentList.Add("-p");
            start.ArgumentList.Add(directory + Path.DirectorySeparatorChar);
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("stderr");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(path);
            proxy._process = Process.Start(start) ?? throw new InvalidOperationException("nginx did not start.");
            proxy._error = proxy.ReadErrorAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                if (proxy._process.HasExited)
                    throw new InvalidOperationException("nginx exited: " + await proxy._error);
                try
                {
                    using var connection = new TcpClient();
                    await connection.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
                    return proxy;
                }
                catch (SocketException)
                {
                    await Task.Delay(50, deadline.Token);
                }
            }
        }
        catch
        {
            await proxy.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not null)
        {
            if (!_process.HasExited)
                _process.Kill(); // Single process; no shared nginx service.
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (_error is not null)
                await _error;
            _process.Dispose();
        }
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<string> ReadErrorAsync()
    {
        var error = new StringBuilder();
        while (await _process!.StandardError.ReadLineAsync() is { } line)
        {
            error.AppendLine(line);
            if (line.Contains("an upstream response is buffered to a temporary file", StringComparison.Ordinal))
                Interlocked.Increment(ref _temporaryResponseFileCount);
        }
        return error.ToString();
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("$", "\\$", StringComparison.Ordinal) + "\"";
}

public sealed class NginxFactAttribute : FactAttribute
{
    public NginxFactAttribute(bool requireRclone = false)
    {
        if ((!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) ||
            !File.Exists(Environment.GetEnvironmentVariable("ROMD_VFS_NGINX")))
            Skip = "Set ROMD_VFS_NGINX to an absolute nginx executable for the HTTPS ingress probe.";
        else if (requireRclone && !File.Exists(Environment.GetEnvironmentVariable("ROMD_VFS_RCLONE")))
            Skip = "Set ROMD_VFS_RCLONE to a verified rclone executable for the nginx client probe.";
    }
}
