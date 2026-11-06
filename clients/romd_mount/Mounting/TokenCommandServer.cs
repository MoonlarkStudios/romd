using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Romd.Mount.Mounting;

/// <summary>Private per-mount IPC for rclone's token command; no credential files or shell.</summary>
public sealed class TokenCommandServer : IAsyncDisposable
{
    private readonly string _directory;
    private readonly string _path;
    private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(32);
    private readonly Socket _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    internal MacSocketDirectoryIdentity? RecoveryDirectory { get; }

    public TokenCommandServer(Func<CancellationToken, Task<string>> token)
    {
        if (OperatingSystem.IsWindows() || (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()))
            throw new PlatformNotSupportedException();
        // macOS Unix-domain socket paths are short; the random private directory avoids collisions.
        _directory = "/tmp/romd-token-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _path = Path.Combine(_directory, "ipc");
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { _listener.Bind(new UnixDomainSocketEndPoint(_path)); _listener.Listen(4); }
        catch { _listener.Dispose(); Directory.Delete(_directory, true); throw; }
        if (OperatingSystem.IsMacOS())
        {
            try { RecoveryDirectory = MacDirectoryCleanup.CaptureSocketDirectory("/private/tmp/" + Path.GetFileName(_directory)); }
            catch { _listener.Dispose(); Directory.Delete(_directory, true); throw; }
        }
        _loop = ServeAsync(token);
    }

    public void Configure(ProcessStartInfo start)
    {
        start.Environment["ROMD_MOUNT_TOKEN_SOCKET"] = _path;
        start.Environment["ROMD_MOUNT_TOKEN_NONCE"] = Convert.ToHexString(_nonce);
    }

    public static string SelfCommand()
    {
        static string Quote(string value)
        {
            if (value.IndexOfAny(['"', '\\', '\r', '\n']) >= 0) throw new IOException("Unsupported executable path.");
            return "\"" + value + "\"";
        }
        string executable = Environment.ProcessPath ?? throw new IOException("Cannot locate the running helper.");
        return Quote(executable) + (Path.GetFileNameWithoutExtension(executable) == "dotnet"
            ? " " + Quote(typeof(Program).Assembly.Location) : "") + " _token";
    }

    private async Task ServeAsync(Func<CancellationToken, Task<string>> token)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var socket = await _listener.AcceptAsync(_stop.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(70));
                try
                {
                    await using var stream = new NetworkStream(socket, ownsSocket: false);
                    byte[] supplied = new byte[32];
                    await stream.ReadExactlyAsync(supplied, deadline.Token);
                    if (!CryptographicOperations.FixedTimeEquals(supplied, _nonce)) continue;
                    string value = await token(deadline.Token);
                    if (value.Length is 0 or > 32768 || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) continue;
                    byte[] bytes = Encoding.UTF8.GetBytes(value);
                    try { await stream.WriteAsync(bytes, deadline.Token); }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or MountIdentityException or Credentials.MountCredentialException) { }
            }
        }
        catch (Exception ex) when (_stop.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    public static async Task<string> ReadAsync(string path, string nonce, CancellationToken ct)
    {
        byte[] bytes = Convert.FromHexString(nonce);
        if (bytes.Length != 32) throw new IOException("Invalid token command nonce.");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await stream.WriteAsync(bytes, ct);
        byte[] result = new byte[32769];
        try
        {
            int count = await stream.ReadAtLeastAsync(result, result.Length, throwOnEndOfStream: false, ct);
            if (count is 0 or > 32768) throw new IOException("Token command failed.");
            string value = new UTF8Encoding(false, true).GetString(result, 0, count);
            if (value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) throw new IOException("Invalid token command response.");
            return value;
        }
        finally { CryptographicOperations.ZeroMemory(result); }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Dispose();
        try { await _loop; }
        finally
        {
            _stop.Dispose();
            CryptographicOperations.ZeroMemory(_nonce);
            if (RecoveryDirectory is not null) MacDirectoryCleanup.ClearSocketDirectory(RecoveryDirectory);
            else Directory.Delete(_directory, true);
        }
    }
}
