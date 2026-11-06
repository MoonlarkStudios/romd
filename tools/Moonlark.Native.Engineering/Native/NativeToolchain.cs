using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Native;

/// <summary>Tool identities recorded in the recipe. Compiler identity comes from CMake's toolchains reply, not from a version banner with paths.</summary>
internal static class NativeToolchain
{
    /// <summary>cmake, ninja and the platform SDK or C library versions, recorded as their tools report them.</summary>
    internal static Result<JsonObject> CollectTools(string rid, IReadOnlyDictionary<string, string> environment, TextWriter? log)
    {
        var tools = new JsonObject();
        List<(string Name, string[] Command)> commands = [("cmake", ["cmake", "--version"]), ("ninja", ["ninja", "--version"])];
        if (rid == "osx-arm64")
            commands.Add(("sdkVersion", ["xcrun", "--show-sdk-version"]));
        else if (rid.StartsWith("linux-", StringComparison.Ordinal))
            commands.Add(("hostGlibc", ["getconf", "GNU_LIBC_VERSION"]));
        foreach ((string name, string[] command) in commands)
        {
            Result<string> output = ProcessRunner.Run(command, environment, log);
            if (!output.Succeeded)
                return output.Failure;
            tools[name] = output.Value;
        }
        tools["hostSystem"] = OperatingSystem.IsMacOS() ? "Darwin" : OperatingSystem.IsLinux() ? "Linux" : RuntimeInformation.OSDescription;
        tools["hostMachine"] = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture other => other.ToString(),
        };
        return tools;
    }

    /// <summary>Adds the effective compiler identity after enforcing the RID's compiler family.</summary>
    internal static Result<JsonObject> WithCompiler(JsonObject tools, CompilerIdentity compiler, string rid)
    {
        if (CheckCompiler(compiler.Id, rid) is { } unsupported)
            return unsupported;
        var toolchain = (JsonObject)tools.DeepClone();
        toolchain["compilerId"] = compiler.Id;
        toolchain["compilerVersion"] = compiler.Version;
        return toolchain;
    }

    internal static Failure? CheckCompiler(string id, string rid) =>
        NativePolicy.CompilerIds(rid).Contains(id, StringComparer.Ordinal) ? null : new Failure("Unsupported C compiler: " + id);
}
