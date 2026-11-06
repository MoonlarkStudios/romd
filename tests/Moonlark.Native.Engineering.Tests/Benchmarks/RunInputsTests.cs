using System.Text.Json;
using Moonlark.Libchdr.Benchmarks;
using Moonlark.Native.Engineering.Tests.TestSupport;
using Xunit;

namespace Moonlark.Native.Engineering.Tests.Benchmarks;

/// <summary>Run receipts bind actual source assets and loaded assemblies without loading fake native files or measuring time.</summary>
public sealed class RunInputsTests
{
    /// <summary>Changing any input category after the receipt was written fails before a benchmark can use it.</summary>
    [Theory]
    [InlineData("native")]
    [InlineData("native-manifest")]
    [InlineData("fixture-manifest")]
    [InlineData("fixture")]
    public void ChangedInputBytesFailVerification(string category)
    {
        using var fixture = new InputDirectory();
        RunInputs.Verify(fixture.Root, fixture.Receipt);
        string file = fixture.Hashes.Keys.Single(key => category switch
        {
            "native" => key.Contains("/native/", StringComparison.Ordinal) && !key.EndsWith(".json", StringComparison.Ordinal),
            "native-manifest" => key.EndsWith("build-manifest.json", StringComparison.Ordinal),
            "fixture-manifest" => key.EndsWith("fixtures-manifest.json", StringComparison.Ordinal),
            _ => key.EndsWith("dvd-lzma.chd", StringComparison.Ordinal),
        });
        File.AppendAllText(Path.Combine(fixture.Root, file), "changed");
        Assert.Throws<InvalidDataException>(() => RunInputs.Verify(fixture.Root, fixture.Receipt));
    }

    /// <summary>Missing, substituted and unknown file hashes cannot validate a partial or mismatched receipt.</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("benchmark-assembly")]
    [InlineData("library-assembly")]
    public void WrongReceiptInventoryFailsVerification(string change)
    {
        using var fixture = new InputDirectory();
        if (change == "missing")
            fixture.Hashes.Remove("benchmark-assembly");
        else if (change == "unknown")
            fixture.Hashes.Add("other-file", new string('0', 64));
        else
            fixture.Hashes[change] = new string('0', 64);
        fixture.Save();
        Assert.Throws<InvalidDataException>(() => RunInputs.Verify(fixture.Root, fixture.Receipt));
    }

    /// <summary>Child compilation must still use the original clean source commit.</summary>
    [Theory]
    [InlineData("changed", "")]
    [InlineData("same", " M source.cs")]
    [InlineData("same", "?? new.cs")]
    [InlineData("", "")]
    public void SourceChangesFailVerification(string actual, string status) =>
        Assert.Throws<InvalidDataException>(() => RunInputs.VerifySourceIdentity(new string('a', 40), actual == "same" ? new string('a', 40) : actual, status));

    /// <summary>The unchanged original clean source identity is accepted.</summary>
    [Fact]
    public void CleanSourceIdentityPasses() => RunInputs.VerifySourceIdentity(new string('a', 40), new string('a', 40), "");

    internal sealed class InputDirectory : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        internal string Root => _directory.Path;
        internal string Receipt => Path.Combine(Root, "inputs.json");
        internal Dictionary<string, string> Hashes { get; }

        internal InputDirectory()
        {
            foreach ((string rid, string file) in new[] { ("osx-arm64", "libmoonlark_chdr.dylib"), ("linux-x64", "libmoonlark_chdr.so"),
                ("linux-arm64", "libmoonlark_chdr.so"), ("win-x64", "moonlark_chdr.dll") })
            {
                string native = Path.Combine(Root, "artifacts", "native", "libchdr", rid);
                Directory.CreateDirectory(Path.Combine(native, "native"));
                File.WriteAllText(Path.Combine(native, "native", file), "fake native, never loaded");
                File.WriteAllText(Path.Combine(native, "build-manifest.json"), "{}");
            }
            string fixtures = Path.Combine(Root, "artifacts", "fixtures", "libchdr-final");
            Directory.CreateDirectory(fixtures);
            File.WriteAllText(Path.Combine(fixtures, "fixtures-manifest.json"), "{}");
            foreach (string fixture in BenchmarkMatrix.Fixtures)
                File.WriteAllText(Path.Combine(fixtures, fixture + ".chd"), "fake fixture");
            Hashes = RunInputs.Files(Root);
            Save();
        }

        internal void Save() => File.WriteAllText(Receipt, JsonSerializer.Serialize(new { Files = Hashes }));
        public void Dispose() => _directory.Dispose();
    }
}
