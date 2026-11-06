using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Romd.Hosting.IntegrationTests.Vfs;

/// <summary>Opt-in compatibility evidence, not a frame-rate or gameplay benchmark.</summary>
internal static class VfsDolphinProbe
{
    public static async Task<object> RunAsync(string executable, string content, string directory)
    {
        string? targetText = Environment.GetEnvironmentVariable("ROMD_VFS_DVD_TARGET");
        int? targetReads = targetText is null ? null : int.Parse(targetText, CultureInfo.InvariantCulture);
        if (targetReads is < 32 or > 100000)
            throw new ArgumentOutOfRangeException(nameof(targetReads));
        string config = Path.Combine(directory, "Config");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "Dolphin.ini"), """
            [AutoUpdate]
            UpdateTrack =
            [Analytics]
            Enabled = False
            PermissionAsked = True
            [Interface]
            ConfirmStop = False
            [Core]
            SlotA = 255
            SlotB = 255
            [DSP]
            Volume = 0
            """);
        await File.WriteAllTextAsync(Path.Combine(config, "Logger.ini"), """
            [Options]
            WriteToFile = True
            WriteToConsole = False
            Verbosity = 4
            [Logs]
            BOOT = True
            CORE = True
            DVD = True
            DIO = True
            """);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in new[] { "--user", directory, "--batch", "--video_backend", "Metal", "--exec", content })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        bool endedEarly = false;
        bool forcedStop = false;
        string logPath = Path.Combine(directory, "Logs", "dolphin.log");
        long started = Stopwatch.GetTimestamp();
        try
        {
            if (targetReads is null)
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                endedEarly = true;
            }
            else
            {
                while (!process.HasExited && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(90))
                {
                    if (File.Exists(logPath) && Regex.Count(await File.ReadAllTextAsync(logPath), "Read: DVDOffset=") >= targetReads)
                        break;
                    await Task.Delay(250);
                }
                endedEarly = process.HasExited;
            }
        }
        catch (TimeoutException) { }
        finally
        {
            if (!process.HasExited)
            {
                var stop = new ProcessStartInfo("/bin/kill") { UseShellExecute = false };
                stop.ArgumentList.Add("-TERM");
                stop.ArgumentList.Add(process.Id.ToString(CultureInfo.InvariantCulture));
                using var signal = Process.Start(stop)!;
                await signal.WaitForExitAsync();
                try
                { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException)
                {
                    forcedStop = true;
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            await Task.WhenAll(stdout, stderr);
        }
        if (endedEarly)
            throw new InvalidOperationException("Dolphin exited before the compatibility observation completed.");
        string log = await File.ReadAllTextAsync(logPath);
        var reads = Regex.Matches(log, @"(?m)^(\d{2}):(\d{2}):(\d{3}) .*Read: DVDOffset=([0-9a-fA-F]+),.*SrcLength = ([0-9a-fA-F]+)")
            .Select(match => new
            {
                ClockMilliseconds = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 60000 +
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 1000 +
                    int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                Offset = long.Parse(match.Groups[4].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                Length = int.Parse(match.Groups[5].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            }).ToArray();
        if (reads.Length < (targetReads ?? 32))
            throw new InvalidOperationException("Dolphin did not reach the required DVD reads; check its startup prompts.");
        return new
        {
            targetReads,
            processWallSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds,
            dvdReadCount = reads.Length,
            dvdRequestedBytes = reads.Sum(read => (long)read.Length),
            maximumDvdReadBytes = reads.Max(read => read.Length),
            forcedStop,
            limitation = "DVD commands prove emulator I/O, not host-file offsets, frame pacing or input response. Results apply to the supplied image; NKit images have additional timing and compatibility limitations.",
            dvdReads = reads
        };
    }
}
