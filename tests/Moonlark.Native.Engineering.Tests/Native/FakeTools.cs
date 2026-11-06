using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Tests.Native;

/// <summary>What a fake tool prints for one exact invocation, and the exit code it returns.</summary>
internal sealed record CannedReply(string Output, int ExitCode = 0);

/// <summary>
/// POSIX shell stand-ins for platform tools, written inside a test directory. Each script answers only the exact argument
/// vectors it was given and otherwise exits 97, so composition tests also pin which tool runs against which binary.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static class FakeTools
{
    /// <summary>Writes one script per tool below <paramref name="directory"/> and returns the directory holding them.</summary>
    internal static string Install(string directory, IEnumerable<(string[] Command, CannedReply Reply)> replies)
    {
        string bin = Directory.CreateDirectory(Path.Combine(directory, "bin")).FullName;
        string outputs = Directory.CreateDirectory(Path.Combine(directory, "replies")).FullName;
        int index = 0;
        foreach (IGrouping<string, (string[] Command, CannedReply Reply)> tool in replies.GroupBy(reply => reply.Command[0], StringComparer.Ordinal))
        {
            var script = new StringBuilder("#!/bin/sh\n");
            foreach ((string[] command, CannedReply reply) in tool)
            {
                string output = Path.Combine(outputs, (index++).ToString(CultureInfo.InvariantCulture) + ".out");
                File.WriteAllText(output, reply.Output);
                script.Append(Matches(command[1..])).Append("; then /bin/cat ").Append(Quote(output))
                    .Append("; exit ").Append(reply.ExitCode.ToString(CultureInfo.InvariantCulture)).Append("; fi\n");
            }
            script.Append("echo \"fake ").Append(tool.Key).Append(": unexpected arguments: $*\" >&2\nexit 97\n");
            WriteExecutable(Path.Combine(bin, tool.Key), script.ToString());
        }
        return bin;
    }

    /// <summary>An explicit environment whose PATH holds only <paramref name="bin"/>, so no host tool can answer.</summary>
    internal static IReadOnlyDictionary<string, string> OnlyOnPath(string bin) =>
        new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = bin };

    internal static void WriteExecutable(string path, string script)
    {
        File.WriteAllText(path, script);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string Matches(string[] arguments) =>
        "if [ \"$#\" -eq " + arguments.Length.ToString(CultureInfo.InvariantCulture) + " ]"
        + string.Concat(arguments.Select((argument, index) => " && [ \"${" + (index + 1).ToString(CultureInfo.InvariantCulture) + "}\" = " + Quote(argument) + " ]"));
}

/// <summary>Representative per-RID tool output for a binary, keyed by tool and first argument so tests can replace one reply.</summary>
internal static class CannedTools
{
    internal static readonly string[] Exports = ["chd_close", "chd_read", ExportInventory.BuildInfoExport];

    internal static string[] Dependencies(string rid) => rid switch
    {
        "osx-arm64" => ["/usr/lib/libSystem.B.dylib"],
        _ => ["libm.so.6", "libc.so.6"],
    };

    internal static JsonObject Platform(string rid) => rid switch
    {
        "osx-arm64" => new JsonObject { ["architecture"] = "arm64", ["minimumOs"] = "14.0" },
        _ => new JsonObject { ["architecture"] = ElfMachine(rid), ["maximumRequiredGlibc"] = "2.17", ["glibcBaseline"] = "2.31" },
    };

    internal static Dictionary<string, (string[] Command, CannedReply Reply)> For(string rid, string binary, IReadOnlyList<string> symbols,
        IReadOnlyList<string> dependencies)
    {
        (string[] Command, CannedReply Reply)[] replies = rid switch
        {
            "osx-arm64" =>
            [
                (["lipo", "-archs", binary], new("arm64\n")),
                (["nm", "-gU", binary], new(Nm(symbols, "_"))),
                (["otool", "-L", binary], new(binary + ":\n\t@loader_path/" + Path.GetFileName(binary) + " (compatibility version 0.0.0, current version 0.0.0)\n"
                    + string.Concat(dependencies.Select(name => "\t" + name + " (compatibility version 1.0.0, current version 1351.0.0)\n")))),
                (["otool", "-l", binary], new("Load command 9\n      cmd LC_BUILD_VERSION\n  cmdsize 32\n platform 1\n    minos 14.0\n      sdk 15.0\n")),
                (["codesign", "--verify", "--strict", binary], new("")),
            ],
            _ =>
            [
                (["readelf", "-h", binary], new(ElfHeader("DYN (Shared object file)", ElfMachine(rid)))),
                (["nm", "-D", "--defined-only", binary], new(Nm(symbols, ""))),
                (["readelf", "-d", binary], new("Dynamic section at offset 0x2de0 contains 26 entries:\n  Tag        Type                         Name/Value\n"
                    + string.Concat(dependencies.Select(name => " 0x0000000000000001 (NEEDED)             Shared library: [" + name + "]\n"))
                    + " 0x000000000000000e (SONAME)             Library soname: [libmoonlark_chdr.so]\n")),
                (["readelf", "--version-info", binary], new(GlibcVersions("2.17"))),
            ],
        };
        return replies.ToDictionary(reply => reply.Command[0] + " " + reply.Command[1], StringComparer.Ordinal);
    }

    internal static string ElfMachine(string rid) => rid == "linux-arm64" ? "AArch64" : "Advanced Micro Devices X86-64";

    internal static string ElfHeader(string type, string machine) =>
        "ELF Header:\n  Class:                             ELF64\n  Type:                              " + type
        + "\n  Machine:                           " + machine + "\n";

    internal static string GlibcVersions(string maximum) =>
        "Version needs section '.gnu.version_r' contains 2 entries:\n  000000: Version: 1  File: libm.so.6  Cnt: 1\n"
        + "  0x0010:   Name: GLIBC_2.2.5  Flags: none  Version: 3\n  0x0020: Version: 1  File: libc.so.6  Cnt: 1\n"
        + "  0x0030:   Name: GLIBC_" + maximum + "  Flags: none  Version: 2\n";

    private static string Nm(IEnumerable<string> symbols, string prefix) =>
        string.Concat(symbols.Select((name, index) => (0x1000 + index * 16).ToString("x16", CultureInfo.InvariantCulture) + " T " + prefix + name + "\n"));
}
