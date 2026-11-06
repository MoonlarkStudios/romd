using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Checks assembly boundaries independently of native availability.</summary>
public sealed class AssemblyContractTests
{
    private static readonly Assembly Library = Assembly.Load("Moonlark.Libchdr");

    /// <summary>The generated C layer must never enter the supported public API.</summary>
    [Fact]
    public void RawInteropIsNotPublic() =>
        Assert.DoesNotContain(Library.GetExportedTypes(), type =>
            type.Namespace?.StartsWith("Moonlark.Libchdr.Interop", StringComparison.Ordinal) == true);

    /// <summary>Runtime marshalling must remain disabled for the blittable boundary.</summary>
    [Fact]
    public void RuntimeMarshallingIsDisabled() =>
        Assert.NotNull(Library.GetCustomAttribute<DisableRuntimeMarshallingAttribute>());

    /// <summary>Version metadata must identify the actual untagged upstream pin recorded in native/libchdr/libchdr.json.</summary>
    [Fact]
    public void UpstreamIdentityIsExplicit()
    {
        using JsonDocument pin = JsonDocument.Parse(File.ReadAllText(PinPath()));
        string commit = pin.RootElement.GetProperty("commit").GetString() ?? "";
        var metadata = Library.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.Ordinal);
        Assert.Matches("^[0-9a-f]{40}$", commit);
        Assert.Equal(commit, metadata["UpstreamCommit"]);
        Assert.Equal(pin.RootElement.GetProperty("upstreamVersion").GetString(), metadata["UpstreamVersion"]);
        Assert.Equal("Family", metadata["NativeVersionMode"]);
        Assert.Equal(new Version(1, 0, 0, 0), Library.GetName().Version);
    }

    /// <summary>Walks up from the test binaries; NativeTestEnvironment is avoided because it loads the native library.</summary>
    private static string PinPath()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "native", "libchdr", "libchdr.json");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("Assembly contract tests require the source checkout's native/libchdr/libchdr.json.");
    }
}
