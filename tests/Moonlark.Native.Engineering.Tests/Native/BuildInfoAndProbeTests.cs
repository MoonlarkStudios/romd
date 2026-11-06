using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Native;
using Moonlark.Native.Engineering.Tests.TestSupport;
using Xunit;

namespace Moonlark.Native.Engineering.Tests.Native;

/// <summary>The build-info header and bounded in-process read, plus layout probe receipt validation.</summary>
public sealed class BuildInfoAndProbeTests
{
    /// <summary>The 16 KiB ABI limit includes the terminator.</summary>
    [Fact]
    public void BuildInfoSizeIncludesTerminator()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "build_info.h");
        Assert.Null(NativeBuildInfo.Write(path, new JsonObject { ["a"] = new string('x', 16383 - 8) }));
        Assert.Contains("16 KiB", NativeBuildInfo.Write(path, new JsonObject { ["a"] = new string('x', 16384 - 8) })?.Message, StringComparison.Ordinal);
    }

    /// <summary>The header is a C string literal of exactly the canonical JSON; a symlinked header is refused.</summary>
    [Fact]
    public void HeaderEmbedsCanonicalJsonAndRefusesSymlinks()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "build_info.h");
        var info = new JsonObject { ["z"] = "quote \" slash \\ question ??=", ["a"] = 1 };
        Assert.Null(NativeBuildInfo.Write(path, info));
        string header = File.ReadAllText(path);
        const string prefix = "#define MOONLARK_CHDR_BUILD_INFO_JSON \"";
        Assert.StartsWith(prefix, header, StringComparison.Ordinal);
        string literal = header[prefix.Length..^2];
        var unescaped = new StringBuilder();
        for (int index = 0; index < literal.Length; index++)
            unescaped.Append(literal[index] == '\\' ? literal[++index] : literal[index]);
        Assert.Equal(Encoding.ASCII.GetString(CanonicalJson.Serialize(info)), unescaped.ToString());
        Assert.DoesNotContain("??", literal, StringComparison.Ordinal);
        Assert.Null(NativeBuildInfo.WritePlaceholder(path));
        Assert.StartsWith("#error", File.ReadAllText(path), StringComparison.Ordinal);
        string link = Path.Combine(directory.Path, "link.h");
        File.CreateSymbolicLink(link, path);
        Assert.Contains("symlink", NativeBuildInfo.Write(link, info)?.Message, StringComparison.Ordinal);
    }

    /// <summary>The read stops at the terminator within the limit and requires an immutable pointer.</summary>
    [Fact]
    public void BuildInfoReadIsBoundedAndRequiresImmutablePointer()
    {
        byte[] json = Encoding.ASCII.GetBytes("{\"a\":\"" + new string('x', 16383 - 8) + "\"}");
        WithMemory([.. json, 0], address =>
        {
            Assert.Equal(16383 - 8, ((string)NativeBuildInfo.Read(() => address).Value["a"]!).Length);
            int calls = 0;
            Assert.Contains("immutable", NativeBuildInfo.Read(() => address + calls++).Failure.Message, StringComparison.Ordinal);
        });
        WithMemory(Enumerable.Repeat((byte)'x', NativeBuildInfo.MaximumBytes).ToArray(), address =>
            Assert.Contains("16 KiB", NativeBuildInfo.Read(() => address).Failure.Message, StringComparison.Ordinal));
        WithMemory([.. "{\"a\":1,\"a\":2}"u8, 0], address =>
            Assert.Contains("not valid JSON", NativeBuildInfo.Read(() => address).Failure.Message, StringComparison.Ordinal));
        Assert.Contains("NULL", NativeBuildInfo.Read(() => IntPtr.Zero).Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The probe must report this host's platform, architecture and schema.</summary>
    [Theory]
    [InlineData("platformMacros", "{\"apple\":false,\"linux\":false,\"windows\":false}", "target differs")]
    [InlineData("architectureMacros", "{\"arm64\":false,\"x64\":false}", "architecture differs")]
    [InlineData("schemaVersion", "2", "schema")]
    public void ProbeMeasurementsMustDescribeTheNativeHost(string field, string value, string message)
    {
        JsonObject measured = Measurements("osx-arm64");
        Assert.Null(LayoutProbe.ValidateMeasurements(measured, "osx-arm64"));
        Assert.Contains("differs", LayoutProbe.ValidateMeasurements(measured, "linux-x64")?.Message, StringComparison.Ordinal);
        measured[field] = JsonNode.Parse(value);
        Assert.Contains(message, LayoutProbe.ValidateMeasurements(measured, "osx-arm64")?.Message, StringComparison.Ordinal);
    }

    /// <summary>The receipt records the build system's exact probe compile command, which must be exported once.</summary>
    [Fact]
    public void ProbeCompileCommandComesFromTheCompilationDatabase()
    {
        using var directory = new TemporaryDirectory();
        string program = Path.Combine(directory.Path, LayoutProbe.ProgramPath);
        string database = Path.Combine(directory.Path, "compile_commands.json");
        Assert.False(LayoutProbe.CompileCommand(directory.Path, program).Succeeded);
        var entry = new JsonObject { ["directory"] = directory.Path, ["command"] = "cc -std=c11 -c probe.c", ["file"] = program };
        File.WriteAllText(database, new JsonArray(entry.DeepClone(), new JsonObject { ["command"] = "other", ["file"] = "/other.c" }).ToJsonString());
        Assert.Equal("cc -std=c11 -c probe.c", LayoutProbe.CompileCommand(directory.Path, program).Value);
        File.WriteAllText(database, new JsonArray(entry.DeepClone(), entry.DeepClone()).ToJsonString());
        Assert.False(LayoutProbe.CompileCommand(directory.Path, program).Succeeded);
    }

    /// <summary>Every supported compiler must build the probe.</summary>
    [Theory]
    [InlineData("AppleClang")]
    [InlineData("Clang")]
    [InlineData("GNU")]
    public void EverySupportedCompilerBuildsTheProbe(string id) =>
        Assert.True(LayoutProbe.IsBuiltBy(new CompilerIdentity(id, "1.0", "compiler")));

    /// <summary>An unsupported compiler family is refused before the probe is built.</summary>
    [Fact]
    public void UnsupportedCompilerDoesNotBuildTheProbe() =>
        Assert.False(LayoutProbe.IsBuiltBy(new CompilerIdentity("MSVC", "1.0", "compiler")));

    /// <summary>Signedness must be measured as a boolean; neither signed nor unsigned implies an ABI failure.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("\"false\"")]
    public void ProbeRequiresMeasuredEnumSignedness(string? invalid)
    {
        JsonObject measured = Measurements("linux-x64");
        measured["primitives"] = new JsonObject { ["chd_error"] = new JsonObject { ["isSigned"] = true } };
        Assert.Null(LayoutProbe.ValidateMeasurements(measured, "linux-x64"));
        measured["primitives"]!["chd_error"]!["isSigned"] = false;
        Assert.Null(LayoutProbe.ValidateMeasurements(measured, "linux-x64"));
        measured["primitives"]!["chd_error"]!["isSigned"] = invalid is null ? null : JsonNode.Parse(invalid);
        Assert.Contains("signedness", LayoutProbe.ValidateMeasurements(measured, "linux-x64")?.Message, StringComparison.Ordinal);
    }

    private static JsonObject Measurements(string rid) => new()
    {
        ["schemaVersion"] = 1,
        ["primitives"] = new JsonObject { ["chd_error"] = new JsonObject { ["isSigned"] = false } },
        ["platformMacros"] = new JsonObject { ["apple"] = rid == "osx-arm64", ["linux"] = rid.StartsWith("linux-", StringComparison.Ordinal), ["windows"] = false },
        ["architectureMacros"] = new JsonObject { ["arm64"] = rid.EndsWith("arm64", StringComparison.Ordinal), ["x64"] = rid.EndsWith("x64", StringComparison.Ordinal) },
    };

    private static void WithMemory(byte[] bytes, Action<IntPtr> action)
    {
        IntPtr address = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, address, bytes.Length);
            action(address);
        }
        finally
        {
            Marshal.FreeHGlobal(address);
        }
    }
}
