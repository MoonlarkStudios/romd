using System.Diagnostics;

namespace Romd.Mount.Mounting;

/// <summary>Does not execute the mount program until its identity is durably recorded.</summary>
internal static class GatedMountLaunch
{
    // macOS /bin/sh can report /bin/bash as its process executable. Launch the
    // exact executable we will later verify instead of broadening accepted paths.
    internal const string Launcher = "/bin/bash";

    internal static void Configure(ProcessStartInfo start)
    {
        string executable = Path.GetFullPath(start.FileName);
        string[] arguments = start.ArgumentList.ToArray();
        start.FileName = Launcher;
        start.RedirectStandardInput = true;
        start.ArgumentList.Clear();
        start.ArgumentList.Add("--noprofile");
        start.ArgumentList.Add("--norc");
        start.ArgumentList.Add("-c");
        // Arguments are positional parameters, never interpolated into shell source.
        // EOF (including supervisor death) exits without executing the mount.
        start.ArgumentList.Add("IFS= read -r signal || exit 125; [ \"$signal\" = ROMD_START ] || exit 125; exec \"$@\" </dev/null");
        start.ArgumentList.Add("romd-mount-launch");
        start.ArgumentList.Add(executable);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Remove("BASH_ENV");
        start.Environment.Remove("ENV");
    }

    internal static async Task ReleaseAsync(Process process, CancellationToken ct)
    {
        await process.StandardInput.WriteLineAsync("ROMD_START".AsMemory(), ct);
        await process.StandardInput.FlushAsync(ct);
        process.StandardInput.Close();
    }
}
