using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Romd.Mount.Cli;

internal sealed class ProfileBinding(string stateDirectory, Uri origin, string profile)
{
    public string LockDirectory => Path.Combine(stateDirectory, "locks");
    public string Key { get; } = Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { origin.AbsoluteUri, profile }))));
    private string FilePath => Path.Combine(stateDirectory, "profiles", Key + ".json");

    public async Task<Guid?> ReadAsync(CancellationToken ct)
    {
        if (new FileInfo(FilePath).LinkTarget is not null) throw new IOException("Profile binding is a symbolic link.");
        if (!File.Exists(FilePath)) return null;
        await using var stream = File.OpenRead(FilePath);
        if (stream.Length > 4096) throw new IOException("Invalid profile binding.");
        byte[] bytes = new byte[4097];
        int count = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, ct);
        if (count > 4096) throw new IOException("Invalid profile binding.");
        using var json = JsonDocument.Parse(bytes.AsMemory(0, count));
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3 ||
            !root.TryGetProperty("origin", out var savedOrigin) || savedOrigin.GetString() != origin.AbsoluteUri ||
            !root.TryGetProperty("profile", out var savedProfile) || savedProfile.GetString() != profile ||
            !root.TryGetProperty("instanceId", out var instance) || !Guid.TryParseExact(instance.GetString(), "D", out var id))
            throw new IOException("Invalid profile binding.");
        return id;
    }

    public async Task SaveAsync(Guid instance, CancellationToken ct)
    {
        string directory = Path.GetDirectoryName(FilePath)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (new DirectoryInfo(directory).LinkTarget is not null) throw new IOException("Profile directory is a symbolic link.");
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(stream, new { origin = origin.AbsoluteUri, profile, instanceId = instance.ToString("D") }, cancellationToken: ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
