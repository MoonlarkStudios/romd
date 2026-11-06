using System.Text.Json;

namespace Moonlark.Libchdr.Benchmarks;

internal sealed record ChildFile(string Path, string Sha256);
internal sealed record ChildBuildReceipt(string ParentInputsSha256, string Partition, string BuildId, string BuildRoot,
    string BinariesDirectory, Dictionary<string, ChildFile> Files);
internal sealed record ChildBuildBinding(string ReceiptPath, string Sha256, string Partition, string BuildId, string BuildRoot, string BinariesDirectory);

/// <summary>Parent-issued identities of BDN's completed, relocated child build; children only verify these receipts.</summary>
internal static class ChildBuildInputs
{
    internal const string ReceiptName = "moonlark-child-inputs.json";

    internal static ChildBuildBinding Capture(string root, string parentPath, string parentHash, string buildRoot, string binaries,
        string partition, string project, string script, string source)
    {
        RunInputs.VerifyAssets(root, parentPath, parentHash);
        Dictionary<string, string> paths = OutputPaths(binaries, partition);
        paths.Add("project", project);
        paths.Add("build-script", script);
        paths.Add("program-source", source);
        foreach (string outputPath in paths.Values)
            RequireFile(buildRoot, outputPath);
        var receipt = new ChildBuildReceipt(parentHash, partition, Guid.NewGuid().ToString("N"), Path.GetFullPath(buildRoot), Path.GetFullPath(binaries),
            paths.ToDictionary(file => file.Key, file => new ChildFile(Path.GetFullPath(file.Value), RunInputs.Hash(file.Value)), StringComparer.Ordinal));
        Validate(receipt, parentHash, binaries, partition);
        string path = Path.Combine(binaries, ReceiptName);
        using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, receipt, BenchmarkMatrix.JsonOptions);
        return new ChildBuildBinding(path, RunInputs.Hash(path), partition, receipt.BuildId, receipt.BuildRoot, receipt.BinariesDirectory);
    }

    internal static void VerifyChild(string root, string parentPath, string parentHash, string binaries, string partition,
        string benchmarkAssembly, string libraryAssembly, string receiptHash)
    {
        string path = Path.Combine(binaries, ReceiptName);
        if (RunInputs.Hash(path) != receiptHash)
            throw new InvalidDataException("Child receipt differs from the parent's in-memory binding.");
        RunInputs.VerifyAssets(root, parentPath, parentHash);
        ChildBuildReceipt receipt = Read(path);
        Validate(receipt, parentHash, binaries, partition);
        RequireFile(receipt.BuildRoot, path);
        if (receipt.Files["benchmark-assembly"].Path != Path.GetFullPath(benchmarkAssembly)
            || receipt.Files["library-assembly"].Path != Path.GetFullPath(libraryAssembly))
            throw new InvalidDataException("Loaded child assemblies differ from the completed build.");
    }

    internal static void VerifyCompleted(string root, string parentPath, string parentHash, ChildBuildBinding binding)
    {
        if (binding.ReceiptPath != Path.Combine(binding.BinariesDirectory, ReceiptName)
            || RunInputs.Hash(binding.ReceiptPath) != binding.Sha256)
            throw new InvalidDataException("Completed child receipt changed.");
        ChildBuildReceipt receipt = Read(binding.ReceiptPath);
        if (receipt.BuildId != binding.BuildId || receipt.BuildRoot != binding.BuildRoot)
            throw new InvalidDataException("Completed child build identity changed.");
        VerifyChild(root, parentPath, parentHash, binding.BinariesDirectory, binding.Partition,
            Path.Combine(binding.BinariesDirectory, "Moonlark.Libchdr.Benchmarks.dll"),
            Path.Combine(binding.BinariesDirectory, "Moonlark.Libchdr.dll"), binding.Sha256);
    }

    private static ChildBuildReceipt Read(string path) => JsonSerializer.Deserialize<ChildBuildReceipt>(File.ReadAllText(path), BenchmarkMatrix.JsonOptions)
        ?? throw new InvalidDataException("Missing child receipt.");

    private static Dictionary<string, string> OutputPaths(string binaries, string partition)
    {
        if (string.IsNullOrWhiteSpace(partition) || Path.GetFileName(partition) != partition || partition is "." or "..")
            throw new InvalidDataException("Invalid child partition.");
        return new(StringComparer.Ordinal)
        {
            ["entry-assembly"] = Path.Combine(binaries, partition + ".dll"),
            ["benchmark-assembly"] = Path.Combine(binaries, "Moonlark.Libchdr.Benchmarks.dll"),
            ["library-assembly"] = Path.Combine(binaries, "Moonlark.Libchdr.dll"),
            ["deps"] = Path.Combine(binaries, partition + ".deps.json"),
            ["runtimeconfig"] = Path.Combine(binaries, partition + ".runtimeconfig.json"),
        };
    }

    private static void Validate(ChildBuildReceipt receipt, string parentHash, string binaries, string partition)
    {
        if (receipt.ParentInputsSha256 != parentHash || receipt.Partition != partition
            || receipt.BinariesDirectory != Path.GetFullPath(binaries)
            || !Guid.TryParseExact(receipt.BuildId, "N", out _) || receipt.BuildRoot != Path.GetFullPath(receipt.BuildRoot))
            throw new InvalidDataException("Child build does not match its parent or execution partition.");
        Dictionary<string, string> outputs = OutputPaths(binaries, partition);
        string[] keys = [.. outputs.Keys, "project", "build-script", "program-source"];
        if (receipt.Files is null || receipt.Files.Count != keys.Length || keys.Any(key => !receipt.Files.ContainsKey(key)))
            throw new InvalidDataException("Incomplete child build inventory.");
        foreach ((string key, ChildFile file) in receipt.Files)
        {
            if (file is null || file.Path != Path.GetFullPath(file.Path)
                || (outputs.TryGetValue(key, out string? expected) && file.Path != Path.GetFullPath(expected)))
                throw new InvalidDataException("Child output path does not match its inventory.");
            RequireFile(receipt.BuildRoot, file.Path);
            if (RunInputs.Hash(file.Path) != file.Sha256)
                throw new InvalidDataException("Child output bytes changed: " + key);
        }
    }

    private static void RequireFile(string root, string path)
    {
        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(fullPath))
            throw new InvalidDataException("Missing child output or path outside its build directory.");
        for (FileSystemInfo? item = new FileInfo(fullPath); item is not null; item = Directory.GetParent(item.FullName))
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Child output contains a symbolic link.");
            if (item.FullName == fullRoot)
                break;
        }
    }
}
