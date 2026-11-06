using System.Diagnostics;
using System.Xml.Linq;

namespace Romd.Hosting.IntegrationTests.Vfs;

/// <summary>Bounded APFS volume for opt-in disk-pressure tests; never fills the host disk.</summary>
internal sealed class VfsScratchVolume : IAsyncDisposable
{
    private readonly string _root = "/private/tmp/romd-vfs-disk-" + Guid.NewGuid().ToString("N");
    private string Volume => Path.Combine(_root, "volume");
    private string Image => Path.Combine(_root, "scratch.dmg");
    public string StateDirectory => Path.Combine(Volume, "state");
    private string Filler => Path.Combine(Volume, "synthetic-pressure.bin");
    public long AvailableBytes => new DriveInfo(Volume).AvailableFreeSpace;

    public long ApplyPressure(long targetAvailableBytes)
    {
        // Refuse to fill anything except the bounded fixture volume, even if its
        // mount unexpectedly disappeared. Flush each block before re-measuring.
        long capacity = new DriveInfo(Volume).TotalSize;
        if (capacity <= 0 || capacity > 128L * 1024 * 1024 || targetAvailableBytes < 8L * 1024 * 1024)
            throw new IOException("Unexpected scratch capacity or pressure target.");
        byte[] bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(1024 * 1024);
        using var stream = new FileStream(Filler, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        while (AvailableBytes > targetAvailableBytes && stream.Length < 64L * 1024 * 1024)
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        if (AvailableBytes > targetAvailableBytes)
            throw new IOException("Scratch pressure did not reach its target.");
        return stream.Length;
    }

    public static async Task<VfsScratchVolume> CreateAsync()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("The APFS scratch volume requires macOS.");
        var result = new VfsScratchVolume();
        Directory.CreateDirectory(result.Volume, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            await Run("/usr/bin/hdiutil", "create", "-size", "64m", "-fs", "APFS", "-volname", "ROMDVfsPressure", "-type", "UDIF", result.Image);
            await Run("/usr/bin/hdiutil", "attach", "-nobrowse", "-mountpoint", result.Volume, result.Image);
            if (!(await Run("/sbin/mount")).Contains(" on " + result.Volume + " (", StringComparison.Ordinal))
                throw new IOException("Scratch volume was not mounted at its expected path.");
            return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }

    public void Fill()
    {
        byte[] bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(1024 * 1024);
        using var stream = new FileStream(Filler, new FileStreamOptions
        { Mode = FileMode.CreateNew, Access = FileAccess.Write, BufferSize = 1 });
        foreach (int size in new[] { bytes.Length, 4096, 1 })
        {
            IOException? full = null;
            try
            {
                while (stream.Length < 128L * 1024 * 1024)
                    stream.Write(bytes, 0, size);
            }
            catch (IOException error) { full = error; }
            if (full is null || full.HResult != 28)
                throw new IOException("Expected ENOSPC on bounded scratch volume.", full);
        }
        Console.WriteLine($"VFS cache-volume ENOSPC: {stream.Length} synthetic pressure bytes.");
    }

    public void ReleasePressure() => File.Delete(Filler);

    public async ValueTask DisposeAsync()
    {
        string mounts = await Run("/sbin/mount");
        if (mounts.Split('\n').Any(line => line.Contains(" on " + Volume + "/", StringComparison.Ordinal)))
            throw new IOException("Preserved scratch volume because a nested mount remains: " + _root);
        if (mounts.Contains(" on " + Volume + " (", StringComparison.Ordinal))
            await Run("/usr/bin/hdiutil", "detach", Volume);
        if ((await Run("/sbin/mount")).Contains(" on " + Volume + " (", StringComparison.Ordinal))
            throw new IOException("Preserved scratch volume because its mount remains: " + _root);
        var attached = XDocument.Parse(await Run("/usr/bin/hdiutil", "info", "-plist"));
        if (attached.Descendants("key").Any(element => element.Value == "image-path" &&
            element.ElementsAfterSelf().FirstOrDefault()?.Value == Image))
            throw new IOException("Preserved attached scratch image: " + _root);
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private static async Task<string> Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        try
        { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        if (process.ExitCode != 0)
            throw new IOException("Scratch-volume operation failed: " + await error.WaitAsync(TimeSpan.FromSeconds(10)));
        return await output.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
