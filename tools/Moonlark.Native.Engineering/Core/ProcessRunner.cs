using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Moonlark.Native.Engineering.Core;

/// <summary>Captured process output. <see cref="Combined"/> mirrors a stderr-into-stdout merge.</summary>
internal sealed record ProcessOutput(int ExitCode, string Stdout, string Stderr, bool TimedOut)
{
    internal string Combined => Stdout + Stderr;
}

/// <summary>Runs tools with an explicit, complete environment and appends evidence to an optional log.</summary>
internal static class ProcessRunner
{
    internal static ProcessOutput Execute(IReadOnlyList<string> command, IReadOnlyDictionary<string, string> environment,
        TextWriter? log = null, string? workingDirectory = null, TimeSpan? timeout = null)
    {
        log?.Write("$ " + Display(command) + "\n");
        log?.Flush();
        if (ResolveExecutable(command[0], environment) is not { } executable)
            return Missing(command[0], "not found on the explicit PATH", log);
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? "",
        };
        foreach (string argument in command.Skip(1))
            start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach ((string name, string value) in environment)
            start.Environment[name] = value;
        Process? started;
        try
        {
            started = Process.Start(start);
        }
        catch (Win32Exception exception)
        {
            // A missing tool is an environment failure, reported like a failed command rather than a crash.
            return Missing(command[0], exception.Message, log);
        }
        using Process process = started ?? throw new IOException("Could not start " + command[0]);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit(timeout ?? Timeout.InfiniteTimeSpan);
        if (!exited)
            process.Kill(entireProcessTree: true);
        process.WaitForExit();
        var output = new ProcessOutput(exited ? process.ExitCode : -1, stdout.Result, stderr.Result, !exited);
        log?.Write(output.Combined + (exited ? "" : "\ntimeout") + "\nexit=" + output.ExitCode.ToString(CultureInfo.InvariantCulture) + "\n");
        log?.Flush();
        return output;
    }

    /// <summary>
    /// Resolves a bare tool name only against absolute entries of the explicit environment's PATH, never the
    /// application or current directory, which .NET would otherwise search first. Names with a directory are used as given.
    /// </summary>
    internal static string? ResolveExecutable(string name, IReadOnlyDictionary<string, string> environment)
    {
        if (name.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) || name.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            return name;
        string search = environment.FirstOrDefault(pair => string.Equals(pair.Key, "PATH", StringComparison.OrdinalIgnoreCase)).Value ?? "";
        string[] extensions = OperatingSystem.IsWindows() && Path.GetExtension(name).Length == 0
            ? (environment.FirstOrDefault(pair => string.Equals(pair.Key, "PATHEXT", StringComparison.OrdinalIgnoreCase)).Value ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        return search.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathFullyQualified)
            .SelectMany(directory => extensions.Select(extension => Path.Combine(directory, name + extension)))
            .FirstOrDefault(IsExecutableFile);
    }

    private static bool IsExecutableFile(string path) =>
        File.Exists(path) && (OperatingSystem.IsWindows()
            || (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0);

    private static ProcessOutput Missing(string name, string reason, TextWriter? log)
    {
        var missing = new ProcessOutput(-1, "", $"Could not start {name}: {reason}", false);
        log?.Write(missing.Combined + "\nexit=-1\n");
        log?.Flush();
        return missing;
    }

    /// <summary>Runs a command that must succeed and returns its trimmed combined output.</summary>
    internal static Result<string> Run(IReadOnlyList<string> command, IReadOnlyDictionary<string, string> environment,
        TextWriter? log = null, string? workingDirectory = null, TimeSpan? timeout = null)
    {
        ProcessOutput output = Execute(command, environment, log, workingDirectory, timeout);
        if (output.TimedOut)
            return new Failure("Command timed out: " + Display(command));
        if (output.ExitCode != 0)
            return new Failure($"Command failed ({output.ExitCode.ToString(CultureInfo.InvariantCulture)}): {Display(command)}\n{output.Combined}");
        return output.Combined.Trim();
    }

    /// <summary>POSIX-shell quoting for evidence logs only; commands are never executed through a shell.</summary>
    internal static string Display(IReadOnlyList<string> command) => string.Join(' ', command.Select(Quote));

    private static string Quote(string argument) =>
        argument.Length > 0 && argument.All(character => char.IsAsciiLetterOrDigit(character) || "@%+=:,./-_".Contains(character, StringComparison.Ordinal))
            ? argument
            : "'" + argument.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
