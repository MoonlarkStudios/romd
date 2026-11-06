using Romd.Mount.Credentials;
using System.Diagnostics;
using Romd.Mount.Mounting;

namespace Romd.Mount.TestHost;

// Child-process fixture; the test supplies its own disposable directory and synthetic key.
public static class Program
{
    public static async Task Main(string[] args)
    {
        using var lease = MountProfileLease.Acquire(args[0], args[1]);
        Process? gatedChild = null;
        if (args.Length == 3 && args[2] == "reserve-gated")
        {
            MountReservation.Create(Path.Combine(args[0], "mounts"), args[1],
                Path.Combine(args[0], "unmounted"), "nfs-development", "/bin/sleep", launchGated: true);
            var start = new ProcessStartInfo("/bin/sleep") { UseShellExecute = false };
            start.ArgumentList.Add("120");
            GatedMountLaunch.Configure(start);
            gatedChild = Process.Start(start)!;
            Console.WriteLine("CHILD " + gatedChild.Id);
            // Deliberately leave this child unrecorded and unreleased until owner death.
        }
        if (args.Length == 3 && args[2] == "reserve")
            Romd.Mount.Mounting.MountReservation.Create(Path.Combine(args[0], "mounts"), args[1],
                "/synthetic/unmounted-path", "nfs-development", "/synthetic/rclone");
        if (args.Length == 3 && args[2] == "reserve-starting")
        {
            var reservation = MountReservation.Create(Path.Combine(args[0], "mounts"), args[1],
                Path.Combine(args[0], "unmounted"), "nfs-development", "/bin/sleep");
            using var child = Process.Start(new ProcessStartInfo("/bin/sleep", "120") { UseShellExecute = false })!;
            reservation.RecordChild(child);
        }
        Console.WriteLine("READY");
        await Task.Delay(Timeout.Infinite);
        GC.KeepAlive(gatedChild);
    }
}
