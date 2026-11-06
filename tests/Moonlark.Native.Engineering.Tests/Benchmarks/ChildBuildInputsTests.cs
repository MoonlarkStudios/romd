using System.Text.Json;
using Moonlark.Libchdr.Benchmarks;
using Xunit;

namespace Moonlark.Native.Engineering.Tests.Benchmarks;

/// <summary>Completed child builds are bound by the parent before execution; child bytes never authorize themselves.</summary>
public sealed class ChildBuildInputsTests
{
    /// <summary>Relocated child DLLs may differ from parent DLLs, but must match their own parent-issued identities.</summary>
    [Fact]
    public void CapturedChildMatchesItsOwnBuild()
    {
        using var child = new ChildDirectory();
        child.Verify();
        ChildBuildInputs.VerifyCompleted(child.Inputs.Root, child.Inputs.Receipt, child.ParentHash, child.Binding);
        Assert.NotEqual(child.Inputs.Hashes["benchmark-assembly"], RunInputs.Hash(child.Benchmark));
    }

    /// <summary>Missing and wrong parent, child, partition or inventory evidence cannot pass child setup.</summary>
    [Theory]
    [InlineData("receipt-digest")]
    [InlineData("parent-binding")]
    [InlineData("missing-parent-binding")]
    [InlineData("parent-bytes")]
    [InlineData("partition")]
    [InlineData("binary-directory")]
    [InlineData("inventory-missing")]
    [InlineData("inventory-extra")]
    [InlineData("benchmark-bytes")]
    [InlineData("library-bytes")]
    [InlineData("entry-bytes")]
    [InlineData("missing-library")]
    [InlineData("missing-receipt")]
    [InlineData("native-bytes")]
    [InlineData("fixture-bytes")]
    [InlineData("escaped-project")]
    [InlineData("linked-project")]
    public void RejectsCorruptedChildEvidence(string change)
    {
        using var child = new ChildDirectory();
        ChildBuildReceipt receipt = child.Read();
        switch (change)
        {
            case "receipt-digest":
                File.AppendAllText(child.Binding.ReceiptPath, " ");
                break;
            case "parent-binding":
                child.Write(receipt with { ParentInputsSha256 = new string('0', 64) });
                break;
            case "missing-parent-binding":
                var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(child.Binding.ReceiptPath))!.AsObject();
                json.Remove("ParentInputsSha256");
                File.WriteAllText(child.Binding.ReceiptPath, json.ToJsonString());
                child.AcceptDigest();
                break;
            case "parent-bytes":
                File.AppendAllText(child.Inputs.Receipt, " ");
                break;
            case "partition":
                child.Write(receipt with { Partition = "another-job" });
                break;
            case "binary-directory":
                child.Write(receipt with { BinariesDirectory = child.BuildRoot });
                break;
            case "inventory-missing":
                receipt.Files.Remove("entry-assembly");
                child.Write(receipt);
                break;
            case "inventory-extra":
                receipt.Files.Add("other", receipt.Files["entry-assembly"]);
                child.Write(receipt);
                break;
            case "benchmark-bytes":
                File.AppendAllText(child.Benchmark, "changed");
                break;
            case "library-bytes":
                File.AppendAllText(child.Library, "changed");
                break;
            case "entry-bytes":
                File.AppendAllText(Path.Combine(child.Binaries, "job-a.dll"), "changed");
                break;
            case "missing-library":
                File.Delete(child.Library);
                break;
            case "missing-receipt":
                File.Delete(child.Binding.ReceiptPath);
                break;
            case "native-bytes":
                File.AppendAllText(Path.Combine(child.Inputs.Root, child.Inputs.Hashes.Keys.Single(key => key.Contains("/native/", StringComparison.Ordinal) && !key.EndsWith(".json", StringComparison.Ordinal))), "changed");
                break;
            case "fixture-bytes":
                File.AppendAllText(Path.Combine(child.Inputs.Root, "artifacts/fixtures/libchdr-final/dvd-lzma.chd"), "changed");
                break;
            case "escaped-project":
                string outside = Path.Combine(child.Inputs.Root, "outside.csproj");
                File.WriteAllText(outside, "outside");
                receipt.Files["project"] = new ChildFile(outside, RunInputs.Hash(outside));
                child.Write(receipt);
                break;
            case "linked-project":
                string project = receipt.Files["project"].Path;
                string target = project + ".target";
                File.Move(project, target);
                File.CreateSymbolicLink(project, target);
                break;
            default:
                throw new InvalidOperationException(change);
        }
        Assert.ThrowsAny<Exception>(child.Verify);
    }

    /// <summary>Parent in-memory bindings detect receipt replacement and changed completed build outputs after execution.</summary>
    [Theory]
    [InlineData("receipt")]
    [InlineData("output")]
    [InlineData("substitution")]
    [InlineData("build-id")]
    [InlineData("fixture")]
    public void ParentRejectsPostRunChanges(string change)
    {
        using var child = new ChildDirectory();
        if (change == "receipt")
            File.AppendAllText(child.Binding.ReceiptPath, " ");
        else if (change == "output")
            File.AppendAllText(child.Library, "changed");
        else if (change == "build-id")
            child.Binding = child.Binding with { BuildId = Guid.NewGuid().ToString("N") };
        else if (change == "fixture")
            File.AppendAllText(Path.Combine(child.Inputs.Root, "artifacts/fixtures/libchdr-final/dvd-lzma.chd"), "changed");
        else
            child.Binding = child.Binding with { Partition = "another-job" };
        Assert.ThrowsAny<Exception>(() => ChildBuildInputs.VerifyCompleted(child.Inputs.Root, child.Inputs.Receipt, child.ParentHash, child.Binding));
    }

    /// <summary>Capture rejects invalid build outputs before issuing any receipt, and never overwrites a previous receipt.</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("escaped")]
    [InlineData("linked")]
    [InlineData("exists")]
    public void CaptureRejectsInvalidBuild(string change)
    {
        using var child = new ChildDirectory();
        string project = Path.Combine(child.BuildRoot, "generated.csproj");
        if (change != "exists")
            File.Delete(child.Binding.ReceiptPath);
        if (change == "missing")
            File.Delete(child.Library);
        if (change == "escaped")
        { project = Path.Combine(child.Inputs.Root, "outside.csproj"); File.WriteAllText(project, "outside"); }
        if (change == "linked")
        { File.Move(project, project + ".target"); File.CreateSymbolicLink(project, project + ".target"); }
        Assert.Throws(change == "exists" ? typeof(IOException) : typeof(InvalidDataException), () => ChildBuildInputs.Capture(child.Inputs.Root, child.Inputs.Receipt, child.ParentHash,
            child.BuildRoot, child.Binaries, "job-a", project, Path.Combine(child.BuildRoot, "build.sh"), Path.Combine(child.BuildRoot, "generated.cs")));
    }

    /// <summary>A failing delegated executor cannot leave another launch's receipt identity in the environment.</summary>
    [Fact]
    public void ExecutorRestoresReceiptAfterFailure()
    {
        string? original = Environment.GetEnvironmentVariable(ChildBuildToolchain.ChildHashVariable);
        Assert.Throws<InvalidOperationException>(() => ChildBuildToolchain.WithChildReceipt<int>("test-parent-digest", () =>
        {
            Assert.Equal("test-parent-digest", Environment.GetEnvironmentVariable(ChildBuildToolchain.ChildHashVariable));
            throw new InvalidOperationException("delegated executor failed");
        }));
        Assert.Equal(original, Environment.GetEnvironmentVariable(ChildBuildToolchain.ChildHashVariable));
    }

    private sealed class ChildDirectory : IDisposable
    {
        internal RunInputsTests.InputDirectory Inputs { get; } = new();
        internal string BuildRoot { get; }
        internal string Binaries { get; }
        internal string Benchmark => Path.Combine(Binaries, "Moonlark.Libchdr.Benchmarks.dll");
        internal string Library => Path.Combine(Binaries, "Moonlark.Libchdr.dll");
        internal string ParentHash { get; }
        internal ChildBuildBinding Binding { get; set; }

        internal ChildDirectory()
        {
            BuildRoot = Directory.CreateDirectory(Path.Combine(Inputs.Root, "build-job-a")).FullName;
            Binaries = Directory.CreateDirectory(Path.Combine(BuildRoot, "bin")).FullName;
            foreach (string name in new[] { "job-a.dll", "Moonlark.Libchdr.Benchmarks.dll", "Moonlark.Libchdr.dll", "job-a.deps.json", "job-a.runtimeconfig.json" })
                File.WriteAllText(Path.Combine(Binaries, name), "child " + name);
            foreach (string name in new[] { "generated.csproj", "build.sh", "generated.cs" })
                File.WriteAllText(Path.Combine(BuildRoot, name), "generated " + name);
            ParentHash = RunInputs.Hash(Inputs.Receipt);
            Binding = ChildBuildInputs.Capture(Inputs.Root, Inputs.Receipt, ParentHash, BuildRoot, Binaries, "job-a",
                Path.Combine(BuildRoot, "generated.csproj"), Path.Combine(BuildRoot, "build.sh"), Path.Combine(BuildRoot, "generated.cs"));
        }

        internal ChildBuildReceipt Read() => JsonSerializer.Deserialize<ChildBuildReceipt>(File.ReadAllText(Binding.ReceiptPath), BenchmarkMatrix.JsonOptions)!;
        internal void Write(ChildBuildReceipt receipt)
        {
            File.WriteAllText(Binding.ReceiptPath, JsonSerializer.Serialize(receipt, BenchmarkMatrix.JsonOptions));
            AcceptDigest(); // Exercise semantic validation independently of the parent's receipt-byte binding.
        }
        internal void AcceptDigest() => Binding = Binding with { Sha256 = RunInputs.Hash(Binding.ReceiptPath) };
        internal void Verify() => ChildBuildInputs.VerifyChild(Inputs.Root, Inputs.Receipt, ParentHash, Binaries, "job-a", Benchmark, Library, Binding.Sha256);
        public void Dispose() => Inputs.Dispose();
    }
}
